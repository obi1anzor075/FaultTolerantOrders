using System.Text.Json;
using Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderService.Api;
using OrderService.Api.Migrations;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "SQLite")]
public sealed class OutboxTests
{
    private static CreateOrderRequest Request() => new(
        Guid.Parse("c7a5f714-0455-421c-b574-28f3f4c6c0ab"),
        [new(Guid.Parse("eb3c8700-4aa5-4d4e-88e1-dff06df6be2f"), 2, 1500)]);

    [Fact]
    public async Task Saved_order_contains_a_durable_event_with_matching_business_data()
    {
        await using var database = await TestDatabase.CreateAsync();
        var result = await new OrderWriter(database.Db).CreateAsync(Request(), default);
        database.Db.ChangeTracker.Clear();

        var order = await database.Db.Orders.Include(x => x.Items).SingleAsync();
        var outbox = await database.Db.OutboxMessages.SingleAsync();
        var message = JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(outbox.Payload)!;

        Assert.Equal(3000m, order.Total);
        Assert.Single(order.Items);
        Assert.Equal(order.Id, outbox.OrderId);
        Assert.Equal(result.MessageId, outbox.Id);
        Assert.Equal(outbox.Id, message.MessageId);
        Assert.Equal(order.Id, message.OrderId);
        Assert.Equal(Request().UserId, message.UserId);
        Assert.Equal(3000m, message.Total);
        Assert.Null(outbox.PublishedAt);
    }

    [Fact]
    public async Task Failed_outbox_insert_does_not_leave_an_order_or_items()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_outbox BEFORE INSERT ON "OutboxMessages"
            BEGIN SELECT RAISE(ABORT, 'Injected outbox insert failure'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new OrderWriter(database.Db).CreateAsync(Request(), default));
        database.Db.ChangeTracker.Clear();

        Assert.Equal(0, await database.Db.Orders.CountAsync());
        Assert.Equal(0, await database.Db.OrderItems.CountAsync());
        Assert.Equal(0, await database.Db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Published_marker_is_saved_only_after_publisher_confirmation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new OrderWriter(database.Db).CreateAsync(Request(), default);
        var row = await database.Db.OutboxMessages.SingleAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new ControlledPublisher(async (_, token) =>
        {
            entered.SetResult();
            await confirmed.Task.WaitAsync(token);
        });
        var delivery = CreateDelivery(database.Db, publisher);
        var operation = delivery.DeliverAsync(row, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var observer = database.NewContext();
        Assert.Null((await observer.OutboxMessages.SingleAsync()).PublishedAt);

        confirmed.SetResult();
        await operation;
        observer.ChangeTracker.Clear();
        Assert.NotNull((await observer.OutboxMessages.SingleAsync()).PublishedAt);
    }

    [Fact]
    public async Task Failed_publication_is_retryable_after_reopening_the_database_context()
    {
        await using var database = await TestDatabase.CreateAsync();
        var created = await new OrderWriter(database.Db).CreateAsync(Request(), default);
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-12T10:00:00Z"));
        var deliveredIds = new List<Guid>();
        var publisher = new ControlledPublisher((message, _) =>
        {
            deliveredIds.Add(message.MessageId);
            return deliveredIds.Count == 1
                ? Task.FromException(new IOException("Broker unavailable"))
                : Task.CompletedTask;
        });

        var row = await database.Db.OutboxMessages.SingleAsync();
        await CreateDelivery(database.Db, publisher, clock).DeliverAsync(row, default);
        database.Db.ChangeTracker.Clear();
        var failed = await database.Db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Null(failed.PublishedAt);
        Assert.Equal(1, failed.RetryCount);
        Assert.NotNull(failed.Error);
        Assert.True(failed.NextRetryAt > clock.GetUtcNow());

        clock.UtcNow = clock.GetUtcNow().AddMinutes(1);
        await using var restartedDb = database.NewContext();
        var retry = await restartedDb.OutboxMessages.SingleAsync();
        await CreateDelivery(restartedDb, publisher, clock).DeliverAsync(retry, default);
        restartedDb.ChangeTracker.Clear();
        var published = await restartedDb.OutboxMessages.SingleAsync();
        Assert.NotNull(published.PublishedAt);
        Assert.Null(published.Error);
        Assert.Equal(new[] { created.MessageId, created.MessageId }, deliveredIds);
    }

    [Fact]
    public async Task Shutdown_during_publish_leaves_the_event_pending()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new OrderWriter(database.Db).CreateAsync(Request(), default);
        var row = await database.Db.OutboxMessages.SingleAsync();
        using var cancellation = new CancellationTokenSource();
        var publisher = new ControlledPublisher((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateDelivery(database.Db, publisher).DeliverAsync(row, cancellation.Token));
        database.Db.ChangeTracker.Clear();
        var saved = await database.Db.OutboxMessages.SingleAsync();
        Assert.Null(saved.PublishedAt);
        Assert.Equal(0, saved.RetryCount);
    }

    [Fact]
    public async Task Reverting_the_outbox_migration_preserves_orders_and_their_items()
    {
        await using var database = await TestDatabase.CreateAsync();
        var created = await new OrderWriter(database.Db).CreateAsync(Request(), default);
        var migration = new InitialOrdersWithOutbox();
        var sqlGenerator = database.Db.GetService<IMigrationsSqlGenerator>();
        // Execute the actual downgrade operations against a relational DB, not a source-text assertion.
        foreach (var command in sqlGenerator.Generate(migration.DownOperations, database.Db.Model))
            await database.Db.Database.ExecuteSqlRawAsync(command.CommandText);
        database.Db.ChangeTracker.Clear();
        Assert.Equal(created.Id, (await database.Db.Orders.SingleAsync()).Id);
        Assert.Equal(1, await database.Db.OrderItems.CountAsync());
    }

    [Fact]
    public async Task Timed_out_publication_does_not_wait_for_slow_connection_cleanup()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new OrderWriter(database.Db).CreateAsync(Request(), default);
        var row = await database.Db.OutboxMessages.SingleAsync();
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new ControlledPublisher(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                // Models a client that is still closing a channel after publish cancellation.
                await releaseCleanup.Task;
                throw;
            }
        });
        var delivery = new OutboxDelivery(database.Db, publisher, TimeProvider.System,
            Options.Create(new OutboxOptions { PublishTimeoutSeconds = 1 }), NullLogger<OutboxDelivery>.Instance);
        var operation = delivery.DeliverAsync(row, default);
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
            database.Db.ChangeTracker.Clear();
            var saved = await database.Db.OutboxMessages.SingleAsync();
            Assert.Null(saved.PublishedAt);
            Assert.Equal(1, saved.RetryCount);
            Assert.NotNull(saved.NextRetryAt);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await operation;
        }
    }

    private static OutboxDelivery CreateDelivery(OrderDbContext db, IEventPublisher publisher,
        TimeProvider? timeProvider = null) => new(db, publisher, timeProvider ?? TimeProvider.System,
        Options.Create(new OutboxOptions()), NullLogger<OutboxDelivery>.Instance);

    private sealed class ControlledPublisher(
        Func<OrderCreatedIntegrationEvent, CancellationToken, Task> publish) : IEventPublisher
    {
        public Task PublishAsync(OrderCreatedIntegrationEvent message, CancellationToken token) => publish(message, token);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestDatabase(SqliteConnection connection, OrderDbContext db) : IAsyncDisposable
    {
        public OrderDbContext Db { get; } = db;
        public OrderDbContext NewContext() => new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseSqlite(connection).Options);

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
