namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Push for the Inbox: one IMAP IDLE connection per enabled mailbox. When the mail server says a new email arrived, that
/// mailbox is read and the email processed at once, so it reaches the Inbox (and Tickets) within seconds instead of at the
/// next <see cref="EmailIntakeWorker"/> poll, which stays as the fallback.
/// </summary>
/// <remarks>
/// Reading still goes through <see cref="EmailIntakeProcessor.SyncMailboxAsync"/> and its lease, so this worker and the poll (or
/// a second server) never read the same mail twice. The set of mailboxes is re-read every minute: a mailbox added, switched
/// off or given a new password is picked up without a restart. A failing connection backs off up to five minutes.
/// </remarks>
public sealed class EmailIntakeIdleWorker(
    IServiceScopeFactory scopes,
    IMailboxWatcher watcher,
    IMailboxSecretProtector secrets,
    IOptions<EmailIntakeOptions> options,
    ILogger<EmailIntakeIdleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxIdle = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private sealed record Watch(string Fingerprint, CancellationTokenSource Stop, Task Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || !options.Value.IdleEnabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        var watches = new Dictionary<Guid, Watch>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(watches, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not refresh the mailboxes to watch."); }
                await Task.Delay(Refresh, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var w in watches.Values) w.Stop.Cancel();
            await Task.WhenAll(watches.Values.Select(w => w.Task));
        }
    }

    private async Task ReconcileAsync(Dictionary<Guid, Watch> watches, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mailboxes = await db.EmailMailboxes.AsNoTracking().Where(m => m.IsEnabled && m.PasswordEncrypted != null).ToListAsync(ct);
        var wanted = mailboxes.ToDictionary(m => m.Id, m =>
            (Mailbox: m, Fingerprint: $"{m.Address}|{m.Username}|{m.ImapHost}|{m.ImapPort}|{m.ImapSecurity}|{m.PasswordEncrypted}"));

        foreach (var (id, w) in watches.ToList())
        {
            if (wanted.TryGetValue(id, out var x) && x.Fingerprint == w.Fingerprint && !w.Task.IsCompleted) continue;
            w.Stop.Cancel();
            watches.Remove(id);
        }
        foreach (var (id, x) in wanted)
        {
            if (watches.ContainsKey(id)) continue;
            MailboxConnection connection;
            try { connection = EmailIntakeProcessor.ConnectionFor(x.Mailbox, secrets); }
            catch (Exception ex) { logger.LogWarning(ex, "Mailbox {Address}: its password could not be read; not watched.", x.Mailbox.Address); continue; }
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            watches[id] = new Watch(x.Fingerprint, stop, Task.Run(() => WatchAsync(id, connection, stop.Token), CancellationToken.None));
        }
    }

    private async Task WatchAsync(Guid mailboxId, MailboxConnection connection, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await watcher.WaitForNewMailAsync(connection, MaxIdle, ct);
                backoff = TimeSpan.FromSeconds(5);
                if (result == MailboxWaitResult.NotSupported)
                {
                    logger.LogInformation("Mailbox {Address}: the server does not support IMAP IDLE; it is read every poll instead.", connection.Address);
                    return;
                }
                if (result == MailboxWaitResult.NewMail)
                {
                    using var scope = scopes.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<EmailIntakeProcessor>();
                    if (await processor.SyncMailboxAsync(mailboxId, ct) > 0) await processor.ProcessPendingAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // The poll records mailbox failures for administrators; here it is only logged, then retried with back-off.
                logger.LogDebug(ex, "Mailbox {Address}: IDLE connection failed; retrying in {Delay}.", connection.Address, backoff);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
            }
        }
    }
}
