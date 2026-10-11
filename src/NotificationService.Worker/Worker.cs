using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NotificationService.Worker;

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumerAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Consumer stopped; retrying connection in 3 seconds");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task RunConsumerAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
            await db.Database.EnsureCreatedAsync(stoppingToken);
        }

        var uri = configuration["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
        var factory = new ConnectionFactory { Uri = new Uri(uri), ClientProvidedName = "notification-service" };
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await channel.ExchangeDeclareAsync(RabbitTopology.Exchange, ExchangeType.Direct, durable: true);
        await channel.QueueDeclareAsync(RabbitTopology.Queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(RabbitTopology.Queue, RabbitTopology.Exchange, RabbitTopology.RoutingKey);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                // RabbitMQ owns this buffer after the callback returns; deserialize it here.
                var message = JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(delivery.Body.Span)
                    ?? throw new JsonException("OrderCreated event was null.");

                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
                db.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(),
                    MessageId = message.MessageId,
                    OrderId = message.OrderId,
                    UserId = message.UserId,
                    Total = message.Total,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync(stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                logger.LogInformation("Created notification for order {OrderId}", message.OrderId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not process delivery {DeliveryTag}", delivery.DeliveryTag);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(RabbitTopology.Queue, autoAck: false, consumer);
        logger.LogInformation("Listening for OrderCreated events");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
