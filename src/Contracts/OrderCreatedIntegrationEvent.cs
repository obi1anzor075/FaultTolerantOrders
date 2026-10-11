namespace Contracts;

public sealed record OrderCreatedIntegrationEvent(
    Guid MessageId,
    Guid OrderId,
    Guid UserId,
    decimal Total,
    DateTimeOffset OccurredAt);

public static class RabbitTopology
{
    public const string Exchange = "orders.events";
    public const string Queue = "notifications.order-created";
    public const string RoutingKey = "order.created";
}
