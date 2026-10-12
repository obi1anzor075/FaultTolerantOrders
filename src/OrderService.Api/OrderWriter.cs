using System.Text.Json;
using Contracts;

namespace OrderService.Api;

public sealed class OrderWriter(OrderDbContext db)
{
    // Save order with message to outbox table in one local transaction.
    public async Task<CreatedOrderResult> CreateAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
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
        var message = new OrderCreatedIntegrationEvent(
            Guid.NewGuid(), order.Id, order.UserId, order.Total, order.CreatedAt);

        db.Orders.Add(order);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            OrderId = order.Id,
            Type = nameof(OrderCreatedIntegrationEvent),
            Payload = JsonSerializer.Serialize(message),
            OccurredAt = message.OccurredAt
        });

        // EF Core commits the order, its items, and the event in one local transaction.
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedOrderResult(order.Id, order.UserId, order.Total, order.CreatedAt, message.MessageId);
    }
}

public sealed record CreatedOrderResult(Guid Id, Guid UserId, decimal Total, DateTimeOffset CreatedAt, Guid MessageId);
