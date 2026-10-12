using Contracts;

namespace OrderService.Api;

public interface IEventPublisher
{
    // Completes only after the broker confirms publication; errors must propagate.
    Task PublishAsync(OrderCreatedIntegrationEvent message, CancellationToken cancellationToken);
}
