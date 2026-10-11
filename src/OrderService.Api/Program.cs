using Contracts;
using Microsoft.EntityFrameworkCore;
using OrderService.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Orders")));
builder.Services.AddSingleton<RabbitPublisher>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapPost("/api/orders", async (
    CreateOrderRequest request,
    HttpContext http,
    OrderDbContext db,
    RabbitPublisher publisher,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (request.UserId == Guid.Empty || request.Items is not { Count: > 0 } ||
        request.Items.Any(item => item.ProductId == Guid.Empty || item.Quantity <= 0 || item.UnitPrice <= 0))
    {
        return Results.BadRequest(new { error = "UserId and at least one valid item are required." });
    }

    var order = new Order
    {
        Id = Guid.NewGuid(),
        UserId = request.UserId,
        CreatedAt = DateTimeOffset.UtcNow,
        Items = request.Items.Select(item => new OrderItem
        {
            Id = Guid.NewGuid(),
            ProductId = item.ProductId,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice
        }).ToList()
    };
    order.Total = order.Items.Sum(item => item.Quantity * item.UnitPrice);

    db.Orders.Add(order);
    await db.SaveChangesAsync(cancellationToken);

    // Deliberate failure window in the baseline: the order is committed, but no event exists yet
    if (app.Environment.IsDevelopment() &&
        http.Request.Headers.TryGetValue("X-Fault-After-Commit", out var fault) && fault == "true")
    {
        Environment.FailFast($"Injected crash after committing order {order.Id}");
    }

    var integrationEvent = new OrderCreatedIntegrationEvent(
        Guid.NewGuid(), order.Id, order.UserId, order.Total, order.CreatedAt);

    try
    {
        await publisher.PublishAsync(integrationEvent, cancellationToken);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Order {OrderId} was saved but its event was not published", order.Id);
        return Results.Json(new
        {
            error = "Order was saved, but event publication failed.",
            orderId = order.Id
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Created($"/api/orders/{order.Id}", new
    {
        order.Id,
        order.UserId,
        order.Total,
        order.CreatedAt,
        integrationEvent.MessageId
    });
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
