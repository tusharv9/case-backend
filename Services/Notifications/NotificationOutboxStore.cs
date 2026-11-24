namespace CaseManagement.Api.Services.Notifications;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OmniConnect.Outbox;

/// <summary>
/// Writes notifications to the notification outbox (<c>NotificationOutboxMessages</c>), from which the platform's dispatcher
/// delivers them to the Notification Service until it answers.
/// </summary>
/// <remarks>
/// When <paramref name="appDb"/> is in the middle of a transaction (an escalation), the rows are written on the same connection
/// inside that transaction: the change and its notifications commit together or not at all. Otherwise they are written in a
/// save of their own.
/// </remarks>
public sealed class NotificationOutboxStore(
    IDbContextFactory<NotificationOutboxDbContext> contexts,
    OutboxSignal<NotificationOutboxDbContext> signal,
    IOptions<HostIntegrationOptions> host)
{
    /// <summary>Builds each notification (Host ids, case values) and writes it; returns how many were written.</summary>
    public async Task<int> StageAsync(AppDbContext appDb, IReadOnlyList<(CaseNotificationRequest Request, DateTimeOffset At)> items, CancellationToken ct = default)
    {
        var rows = new List<OutboxMessage>();
        foreach (var (request, at) in items)
        {
            var built = await CaseNotificationBuilder.BuildAsync(appDb, request, host.Value.AppKey, at, ct);
            if (built == null) continue;   // nobody the platform knows: nothing to send
            var now = DateTimeOffset.UtcNow;
            rows.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventId = built.EventId,
                Topic = OutboxTopics.Notification,
                Payload = System.Text.Json.JsonSerializer.Serialize(built, Outbox<NotificationOutboxDbContext>.Json),
                Status = OutboxStates.Pending,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }
        if (rows.Count == 0) return 0;

        var transaction = appDb.Database.IsRelational() ? appDb.Database.CurrentTransaction : null;
        if (transaction != null)
        {
            // Same connection, same transaction: committed (or rolled back) with the change. No retrying strategy here: the
            // caller's own execution strategy retries the whole transaction.
            var options = new DbContextOptionsBuilder<NotificationOutboxDbContext>().UseNpgsql(appDb.Database.GetDbConnection()).Options;
            await using var inTransaction = new NotificationOutboxDbContext(options);
            await inTransaction.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);
            inTransaction.OutboxMessages.AddRange(rows);
            await inTransaction.SaveChangesAsync(ct);
            return rows.Count;   // the caller wakes the dispatcher once it has committed
        }

        await using var own = await contexts.CreateDbContextAsync(ct);
        own.OutboxMessages.AddRange(rows);
        await own.SaveChangesAsync(ct);
        signal.Wake();
        return rows.Count;
    }

    /// <summary>Wakes the notification dispatcher (after a transaction that wrote rows committed).</summary>
    public void Wake() => signal.Wake();
}
