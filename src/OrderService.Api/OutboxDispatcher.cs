using Microsoft.EntityFrameworkCore;

namespace OrderService.Api;

public sealed class OutboxDispatcher(OrderDbContext db, OutboxDelivery delivery, TimeProvider timeProvider,
    ILogger<OutboxDispatcher> logger)
{
    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        // Do not compose additional LINQ over this SQL: keep the lock on the selected row.
        var rows = await db.OutboxMessages.FromSqlInterpolated($"""
            SELECT * FROM "OutboxMessages"
            WHERE "PublishedAt" IS NULL
              AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= {now})
            ORDER BY "OccurredAt", "Id"
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (rows.Count == 0) return false;

        var message = rows[0];
        await delivery.DeliverAsync(message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (message.PublishedAt is not null)
            logger.LogInformation("Committed publish marker for outbox {MessageId}, order {OrderId}",
                message.Id, message.OrderId);
        return true;
    }
}
