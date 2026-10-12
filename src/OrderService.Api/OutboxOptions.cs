namespace OrderService.Api;

public sealed class OutboxOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 2000;
    public int BatchSize { get; set; } = 20;
    public int PublishTimeoutSeconds { get; set; } = 10;
}
