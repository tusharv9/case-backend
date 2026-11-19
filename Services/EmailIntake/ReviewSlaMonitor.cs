namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Notifications;
using CaseManagement.Api.Services.Tickets;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The sender-review half of the SLA monitor: reminds the mailbox's teams about emails still waiting at their reminder time, and
/// escalates those still waiting at their escalation time — each once, with the escalation recorded in the audit trail. Runs
/// inside the SLA monitor's cycle, under its single-instance lock.
/// </summary>
/// <remarks>
/// An email that started waiting before review deadlines existed gets its times here, from its mailbox's settings, counted from
/// when it started waiting; if those times have already passed it is not alerted (the day this ships must not page everyone about
/// every email already waiting). It never bumps <see cref="InboundEmail.Version"/>: that guards a reviewer's decision against an
/// email that changed under them, and a reminder is not such a change.
/// </remarks>
public sealed class ReviewSlaMonitor(AppDbContext db, ICaseNotificationSink? sink, ILogger<ReviewSlaMonitor> logger)
{
    private const int BatchSize = 200;

    /// <summary>One pass. Returns how many reminders and escalations were raised.</summary>
    public async Task<(int Reminders, int Escalations)> RunAsync(TicketSlaClock clock, DateTime nowUtc, CancellationToken ct)
    {
        var mailboxes = await db.EmailMailboxes.AsNoTracking().ToDictionaryAsync(m => m.Id, ct);
        var alerts = new List<(InboundEmail Email, ReviewAlerts.Kind Kind)>();

        var lastId = Guid.Empty;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.InboundEmails
                .Where(e => e.Id > lastId && e.Status == EmailStatuses.PendingReview && e.ReviewEscalatedAt == null)
                .OrderBy(e => e.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var e in batch)
            {
                if (!mailboxes.TryGetValue(e.MailboxId, out var mailbox)) continue;
                var arrivedBeforeDeadlines = ReviewSla.Start(e, mailbox, clock, nowUtc);
                if (e.ReviewDueAt == null) continue;   // no deadline

                if (e.ReviewEscalateAt is { } escalateAt && escalateAt <= nowUtc)
                {
                    e.ReviewEscalatedAt = nowUtc;
                    e.ReviewReminderSentAt ??= nowUtc;   // past both: escalate, never also remind
                    if (arrivedBeforeDeadlines) continue;
                    alerts.Add((e, ReviewAlerts.Kind.Escalated));
                    db.CaseEvents.Add(new CaseEvent
                    {
                        CaseId = null, EventType = EventType.Other, CreatedAt = nowUtc, IsInternal = true, Module = "EmailReview", EntityName = "InboundEmail",
                        ActionType = "REVIEW_SLA_ESCALATED",
                        Message = $"An email from {e.FromAddress} in {mailbox.Address} passed its review deadline ({clock.Display(e.ReviewDueAt!.Value)}) and was escalated to "
                            + (mailbox.ReviewEscalateTo == ReviewEscalationTargets.Administrators ? "the administrators." : "the leads of the mailbox's teams."),
                        NewValue = System.Text.Json.JsonSerializer.Serialize(new { EmailId = e.Id, e.ReviewStartedAt, e.ReviewDueAt, EscalatedAt = nowUtc, mailbox.ReviewEscalateTo }),
                    });
                }
                else if (e.ReviewReminderAt is { } remindAt && remindAt <= nowUtc && e.ReviewReminderSentAt == null)
                {
                    e.ReviewReminderSentAt = nowUtc;
                    if (!arrivedBeforeDeadlines) alerts.Add((e, ReviewAlerts.Kind.Reminder));
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        foreach (var (email, kind) in alerts)
            await ReviewAlerts.NotifyAsync(db, sink, kind, email, mailboxes[email.MailboxId], logger, ct, clock);
        return (alerts.Count(a => a.Kind == ReviewAlerts.Kind.Reminder), alerts.Count(a => a.Kind == ReviewAlerts.Kind.Escalated));
    }
}
