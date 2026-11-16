namespace CaseManagement.Api.Services.Tickets;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The ticket half of the SLA monitor: finds tickets still waiting past their assign-within target and tells the leads of the
/// mailbox's teams, once per ticket. Runs inside <see cref="SlaMonitorService"/>'s cycle, under its single-instance lock.
/// </summary>
/// <remarks>
/// It never bumps <see cref="Ticket.Version"/>: that version guards Assign/Close against a ticket that changed under the person
/// looking at it, and a monitor noting a breach is not such a change. Older tickets (from before ticket SLAs) get their target
/// and due time filled in here the first time they are seen.
/// </remarks>
public sealed class TicketSlaMonitor(AppDbContext db, ICaseNotificationSink? sink, ILogger<TicketSlaMonitor> logger)
{
    private const int BatchSize = 200;

    /// <summary>One pass. Returns how many tickets were found late.</summary>
    public async Task<int> RunAsync(TicketSlaClock clock, DateTime nowUtc, CancellationToken ct)
    {
        var targets = await TicketSlaTargets.LoadAsync(db, ct);
        var late = new List<(Guid Id, Guid MailboxId, string Number)>();

        var lastId = Guid.Empty;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Tickets
                .Where(t => t.Id > lastId && t.Status == TicketStatuses.New && !t.SlaBreachNotified)
                .OrderBy(t => t.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var t in batch)
            {
                var target = targets.Of(t);
                t.AssignTargetMinutes ??= target;
                if (target <= 0) continue;
                t.AssignDueAt ??= clock.DueAt(t.ReceivedAt, target);
                if (clock.Evaluate(t.ReceivedAt, target, null, nowUtc).State != TicketSlaStates.Breached) continue;
                t.SlaBreachedAt ??= t.AssignDueAt ?? nowUtc;
                t.SlaBreachNotified = true;
                late.Add((t.Id, t.MailboxId, t.TicketNumber));
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        if (late.Count > 0) await NotifyAsync(late, ct);
        return late.Count;
    }

    private async Task NotifyAsync(List<(Guid Id, Guid MailboxId, string Number)> late, CancellationToken ct)
    {
        if (sink == null) return;
        var mailboxIds = late.Select(l => l.MailboxId).Distinct().ToList();
        var mailboxes = await db.EmailMailboxes.AsNoTracking().Where(m => mailboxIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var leads = (await db.EmailMailboxTeams.AsNoTracking()
                .Where(x => mailboxIds.Contains(x.MailboxId) && x.Team!.IsActive && x.Team.OwnerId != null)
                .Select(x => new { x.MailboxId, Lead = x.Team!.OwnerId!.Value }).ToListAsync(ct))
            .GroupBy(x => x.MailboxId).ToDictionary(g => g.Key, g => g.Select(x => x.Lead).Distinct().ToList());

        foreach (var (id, mailboxId, number) in late)
        {
            if (!leads.TryGetValue(mailboxId, out var recipients) || recipients.Count == 0) continue;
            try
            {
                sink.Enqueue(new CaseNotificationRequest(CaseNotificationKinds.TicketSlaBreached, recipients,
                    CaseNumber: number, EventKey: $"ticket-sla:{id}",
                    Values: new Dictionary<string, string?> { ["mailbox"] = mailboxes.GetValueOrDefault(mailboxId) }, Link: $"tickets/{id}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not raise the ticket SLA alert for {Ticket}.", number);
            }
        }
    }
}
