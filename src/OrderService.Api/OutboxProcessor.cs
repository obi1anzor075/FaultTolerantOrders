using Microsoft.Extensions.Options;

namespace OrderService.Api;

public sealed class OutboxProcessor(IServiceScopeFactory scopeFactory, IOptions<OutboxOptions> options,
    TimeProvider timeProvider, ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Outbox processor is disabled; events will remain pending");
            return;
        }

        logger.LogInformation("Outbox processor started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var index = 0; index < options.Value.BatchSize; index++)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                    if (!await dispatcher.DispatchNextAsync(stoppingToken)) break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A DB/commit failure never discards the event; this scope/transaction is disposed.
                logger.LogError(exception, "Outbox batch failed; pending events will be retried");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds),
                    timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
