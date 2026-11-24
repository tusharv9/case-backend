namespace CaseManagement.Api.Services.Notifications;

using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Where Case Management's notifications go: the notification outbox (<c>NotificationOutboxMessages</c>), from which the
/// platform's dispatcher delivers them to the Notification Service until it answers — across restarts, outages and retries,
/// written once there (the event id).
/// </summary>
/// <remarks>
/// <para><b>When the row is written.</b> A notification is raised after the change it reports was saved. It is kept here until
/// the end of the request (or background round) and then written by <see cref="FlushAsync"/> in a save of its own, so nothing
/// else the request left unsaved is ever saved with it — the same small window AuthService and Products document. Escalations
/// do better: <see cref="EscalationExecutor"/> writes theirs inside the escalation's own transaction.</para>
/// <para><b>Never fails the action.</b> A notification problem is logged; the change that raised it stands.</para>
/// </remarks>
public sealed class OutboxNotificationSink(
    IDbContextFactory<AppDbContext> contexts,
    NotificationOutboxStore store,
    ILogger<OutboxNotificationSink> logger) : ICaseNotificationSink, IAsyncDisposable, IDisposable
{
    private readonly List<(CaseNotificationRequest Request, DateTimeOffset At)> pending = new();
    private readonly object gate = new();

    public void Enqueue(CaseNotificationRequest request)
    {
        if (request.RecipientLocalIds.Count == 0) return;
        lock (gate) pending.Add((request, DateTimeOffset.UtcNow));
    }

    /// <summary>Writes what was raised so far to the notification outbox, in a save of its own.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<(CaseNotificationRequest Request, DateTimeOffset At)> batch;
        lock (gate)
        {
            if (pending.Count == 0) return;
            batch = [.. pending];
            pending.Clear();
        }
        try
        {
            // A context of its own to read the people and cases with: nothing the request left unsaved is touched.
            await using var db = await contexts.CreateDbContextAsync(ct);
            await store.StageAsync(db, batch, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write {Count} notification(s) to the outbox.", batch.Count);
        }
    }

    public async ValueTask DisposeAsync() => await FlushAsync(CancellationToken.None);

    // A background round's scope may be disposed synchronously.
    public void Dispose() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
}
