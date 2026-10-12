using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using OrderService.Api;
using Xunit;

namespace OrderService.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class PostgreSqlOutboxTests
{
    [PostgreSqlFact]
    public async Task Migration_adopts_baseline_tables_without_losing_existing_orders()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        // Schema produced by the baseline EnsureCreated, including the existing FK index.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "Orders" (
                "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL,
                "Total" numeric(18,2) NOT NULL, "CreatedAt" timestamptz NOT NULL);
            CREATE TABLE "OrderItems" (
                "Id" uuid PRIMARY KEY, "OrderId" uuid NOT NULL REFERENCES "Orders"("Id") ON DELETE CASCADE,
                "ProductId" uuid NOT NULL, "Quantity" integer NOT NULL, "UnitPrice" numeric(18,2) NOT NULL);
            CREATE INDEX "IX_OrderItems_OrderId" ON "OrderItems"("OrderId");
            INSERT INTO "Orders" VALUES (
                '9346d211-b010-4b21-a71d-9fcc795a0476', 'c7a5f714-0455-421c-b574-28f3f4c6c0ab',
                3000, '2026-10-12T09:28:09Z');
            INSERT INTO "OrderItems" VALUES (
                'deb954ba-df25-4b8a-abd7-9a96c6402f38', '9346d211-b010-4b21-a71d-9fcc795a0476',
                'eb3c8700-4aa5-4d4e-88e1-dff06df6be2f', 2, 1500);
            """);

        await db.Database.MigrateAsync();
        var historical = await db.Orders.SingleAsync();
        Assert.Equal(Guid.Parse("9346d211-b010-4b21-a71d-9fcc795a0476"), historical.Id);
        Assert.Equal(3000m, historical.Total);
        Assert.Equal(0, await db.OutboxMessages.CountAsync());

        await new OrderWriter(db).CreateAsync(Request(), default);
        await db.Database.MigrateAsync(); // A second startup must not recreate or delete data.
        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());

        await db.GetService<IMigrator>().MigrateAsync("0");
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Orders.CountAsync());
        Assert.Equal(2, await db.OrderItems.CountAsync());
    }

    [PostgreSqlFact]
    public async Task Fresh_database_migration_supports_order_and_outbox_creation()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await db.Database.MigrateAsync();
        var result = await new OrderWriter(db).CreateAsync(Request(), default);
        Assert.Equal(result.Id, (await db.Orders.SingleAsync()).Id);
        Assert.Equal(result.MessageId, (await db.OutboxMessages.SingleAsync()).Id);
    }

    [PostgreSqlFact]
    public async Task Second_dispatcher_skips_the_row_while_first_is_waiting_for_confirmation()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using var setup = database.NewContext();
        await setup.Database.MigrateAsync();
        await new OrderWriter(setup).CreateAsync(Request(), default);
        await using var firstDb = database.NewContext();
        await using var secondDb = database.NewContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new ControlledPublisher(async (_, token) =>
        {
            entered.TrySetResult();
            await confirmed.Task.WaitAsync(token);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var first = Dispatcher(firstDb, publisher).DispatchNextAsync(deadline.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await Dispatcher(secondDb, publisher).DispatchNextAsync(deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            confirmed.TrySetResult();
        }
        Assert.True(await first);
        setup.ChangeTracker.Clear();
        Assert.NotNull((await setup.OutboxMessages.SingleAsync()).PublishedAt);
    }

    [PostgreSqlFact]
    public async Task Dispatcher_does_not_publish_a_retry_before_its_due_time()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await db.Database.MigrateAsync();
        await new OrderWriter(db).CreateAsync(Request(), default);
        var row = await db.OutboxMessages.SingleAsync();
        row.NextRetryAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var publisher = new ControlledPublisher((_, _) => throw new InvalidOperationException("Publish was not due"));

        Assert.False(await Dispatcher(db, publisher).DispatchNextAsync(default));
        db.ChangeTracker.Clear();
        var saved = await db.OutboxMessages.SingleAsync();
        Assert.Null(saved.PublishedAt);
        Assert.Equal(0, saved.RetryCount);
    }

    private static CreateOrderRequest Request() => new(Guid.NewGuid(), [new(Guid.NewGuid(), 2, 1500)]);

    private static OutboxDispatcher Dispatcher(OrderDbContext db, IEventPublisher publisher)
    {
        var delivery = new OutboxDelivery(db, publisher, TimeProvider.System, Options.Create(new OutboxOptions()),
            NullLogger<OutboxDelivery>.Instance);
        return new OutboxDispatcher(db, delivery, TimeProvider.System, NullLogger<OutboxDispatcher>.Instance);
    }

    private sealed class ControlledPublisher(Func<OrderCreatedIntegrationEvent, CancellationToken, Task> action)
        : IEventPublisher
    {
        public Task PublishAsync(OrderCreatedIntegrationEvent message, CancellationToken token) => action(message, token);
    }

    private sealed class PostgreSqlTestDatabase(string adminConnection, string schema) : IAsyncDisposable
    {
        public OrderDbContext NewContext()
        {
            var builder = new NpgsqlConnectionStringBuilder(adminConnection) { SearchPath = schema };
            return new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
                .UseNpgsql(builder.ConnectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema))
                .Options);
        }

        public static async Task<PostgreSqlTestDatabase> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("OUTBOX_TEST_POSTGRES")
                ?? throw new InvalidOperationException("OUTBOX_TEST_POSTGRES is required");
            var schema = $"outbox_test_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
            return new PostgreSqlTestDatabase(connectionString, schema);
        }

        public async ValueTask DisposeAsync()
        {
            // Only the schema generated by this test is removed, never the demonstration tables.
            if (!schema.StartsWith("outbox_test_", StringComparison.Ordinal) ||
                !Guid.TryParseExact(schema[12..], "N", out _))
                throw new InvalidOperationException("Refusing to remove a non-test schema");
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OUTBOX_TEST_POSTGRES")))
            Skip = "Set OUTBOX_TEST_POSTGRES to run migration and row-lock tests against PostgreSQL.";
    }
}
