namespace OrderService.Api;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? Error { get; set; }
}
