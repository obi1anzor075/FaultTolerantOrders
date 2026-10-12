using Microsoft.Extensions.Options;
using System.Text.Json;
using Contracts;

namespace OrderService.Api;

public sealed class OutboxDelivery(OrderDbContext db, IEventPublisher publisher, TimeProvider timeProvider,
    IOptions<OutboxOptions> options, ILogger<OutboxDelivery> logger)
{
    public async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.PublishedAt is not null) return;

        try
        {
            if (message.Type != nameof(OrderCreatedIntegrationEvent))
                throw new InvalidOperationException($"Unsupported outbox event type: {message.Type}");
            var integrationEvent = JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(message.Payload)
                ?? throw new JsonException("Outbox payload was null.");
            if (integrationEvent.MessageId != message.Id || integrationEvent.OrderId != message.OrderId)
                throw new InvalidOperationException("Outbox identity does not match its payload.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds));
            var publication = publisher.PublishAsync(integrationEvent, timeout.Token);
            var messageId = message.Id;
            // Client cleanup can continue after cancellation. Observe its eventual error without
            // retaining the DB transaction while a broken channel is being closed.
            _ = publication.ContinueWith(failed =>
                logger.LogDebug(failed.Exception, "Publication task finished with error for {MessageId}", messageId),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await publication.WaitAsync(timeout.Token);

            message.PublishedAt = timeProvider.GetUtcNow();
            message.NextRetryAt = null;
            message.Error = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown leaves the transaction uncommitted; the next process can retry.
            throw;
        }
        catch (Exception exception)
        {
            message.RetryCount++;
            var delaySeconds = Math.Min(30, Math.Pow(2, Math.Min(message.RetryCount - 1, 5)));
            message.NextRetryAt = timeProvider.GetUtcNow().AddSeconds(delaySeconds);
            var error = $"{exception.GetType().Name}: {exception.Message}";
            message.Error = error.Length > 2000 ? error[..2000] : error;
            logger.LogWarning(exception, "Outbox {MessageId} publication failed; retry {RetryCount} after {NextRetryAt}",
                message.Id, message.RetryCount, message.NextRetryAt);
        }

        // The dispatcher owns the transaction and holds the row lock until commit.
        await db.SaveChangesAsync(cancellationToken);
    }
}
