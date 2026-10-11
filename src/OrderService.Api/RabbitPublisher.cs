using System.Text.Json;
using Contracts;
using RabbitMQ.Client;

namespace OrderService.Api;

public sealed class RabbitPublisher(IConfiguration configuration) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;

    public async Task PublishAsync(OrderCreatedIntegrationEvent message, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(RabbitTopology.Exchange, ExchangeType.Direct, durable: true);
        await channel.QueueDeclareAsync(RabbitTopology.Queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(RabbitTopology.Queue, RabbitTopology.Exchange, RabbitTopology.RoutingKey);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.MessageId.ToString()
        };
        await channel.BasicPublishAsync(
            RabbitTopology.Exchange,
            RabbitTopology.RoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: JsonSerializer.SerializeToUtf8Bytes(message),
            cancellationToken: cancellationToken);
    }

    private async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            if (_connection is not null) await _connection.DisposeAsync();

            var uri = configuration["RabbitMq:Uri"]
                ?? throw new InvalidOperationException("RabbitMq:Uri is required.");
            var factory = new ConnectionFactory { Uri = new Uri(uri), ClientProvidedName = "order-service" };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _connectionLock.Dispose();
    }
}
