using Microsoft.EntityFrameworkCore;
using OrderService.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));
builder.Services.AddSingleton<IEventPublisher, RabbitPublisher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<OrderWriter>();
builder.Services.AddScoped<OutboxDelivery>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddOptions<OutboxOptions>()
    .Bind(builder.Configuration.GetSection("Outbox"))
    .Validate(options => options.PollIntervalMilliseconds > 0 && options.BatchSize is > 0 and <= 100 &&
                         options.PublishTimeoutSeconds is > 0 and <= 60, "Invalid Outbox timing or batch size")
    .ValidateOnStart();
builder.Services.AddHostedService<OutboxProcessor>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
}

app.MapPost("/api/orders", async (
    CreateOrderRequest request,
    HttpContext http,
    OrderWriter writer,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (request.UserId == Guid.Empty || request.Items is not { Count: > 0 } ||
        request.Items.Any(item => item.ProductId == Guid.Empty || item.Quantity <= 0 || item.UnitPrice <= 0))
    {
        return Results.BadRequest(new { error = "UserId and at least one valid item are required." });
    }

    var order = await writer.CreateAsync(request, cancellationToken);
    logger.LogInformation("Committed order {OrderId} and pending outbox {MessageId}", order.Id, order.MessageId);

    // The same crash point as baseline; now the event is already durable in PostgreSQL.
    if (app.Environment.IsDevelopment() &&
        http.Request.Headers.TryGetValue("X-Fault-After-Commit", out var fault) && fault == "true")
    {
        Environment.FailFast($"Injected crash after committing order {order.Id} and outbox {order.MessageId}");
    }

    return Results.Created($"/api/orders/{order.Id}", order);
});

app.MapGet("/api/orders", async (OrderDbContext db, CancellationToken cancellationToken) =>
    await db.Orders.AsNoTracking()
        .OrderBy(order => order.CreatedAt)
        .Select(order => new { order.Id, order.UserId, order.Total, order.CreatedAt })
        .ToListAsync(cancellationToken));

app.MapGet("/api/orders/{id:guid}", async (Guid id, OrderDbContext db, CancellationToken cancellationToken) =>
{
    var order = await db.Orders.AsNoTracking()
        .Where(order => order.Id == id)
        .Select(order => new { order.Id, order.UserId, order.Total, order.CreatedAt })
        .SingleOrDefaultAsync(cancellationToken);
    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.Run();

public sealed record CreateOrderRequest(Guid UserId, List<CreateOrderItemRequest> Items);
public sealed record CreateOrderItemRequest(Guid ProductId, int Quantity, decimal UnitPrice);
