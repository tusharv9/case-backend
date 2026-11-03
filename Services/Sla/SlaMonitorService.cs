namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public interface ISlaMonitor
{
    /// <summary>
    /// One monitoring cycle: evaluates every open case through the SLA clock, records breaches, sends the early reminder,
    /// escalates cases whose trigger is met, and publishes breach notifications. Safe to run on several instances at once —
    /// only one does the work at a time.
    /// </summary>
    Task<SlaMonitorReport> RunCycleAsync(DateTime? nowUtc = null, CancellationToken ct = default);
}

public sealed record SlaMonitorReport(bool Ran, int Evaluated, int Reminders, int Escalations, int Breaches);

public class SlaMonitorService : ISlaMonitor
{
    /// <summary>Advisory-lock key that makes the monitor single-instance across every running copy of the app.</summary>
    public const long LockKey = 7_315_902_001L;
    private const int BatchSize = 200;

    private readonly AppDbContext _context;
    private readonly ISlaClockProvider _clockProvider;
    private readonly IEscalationService _escalation;
    private readonly INotificationService _notifications;
    private readonly ILogger<SlaMonitorService> _logger;

    public SlaMonitorService(
        AppDbContext context,
        ISlaClockProvider clockProvider,
        IEscalationService escalation,
        INotificationService notifications,
        ILogger<SlaMonitorService> logger)
    {
        _context = context;
        _clockProvider = clockProvider;
        _escalation = escalation;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<SlaMonitorReport> RunCycleAsync(DateTime? nowUtc = null, CancellationToken ct = default)
    {
        // The lock is session-scoped, so hold one explicit connection for the whole cycle.
        await _context.Database.OpenConnectionAsync(ct);
        try
        {
            var gotLock = await _context.Database.SqlQueryRaw<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct);
            if (!gotLock) return new SlaMonitorReport(false, 0, 0, 0, 0);

            try
            {
                return await RunLockedAsync(nowUtc ?? DateTime.UtcNow, ct);
            }
            finally
            {
                await _context.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await _context.Database.CloseConnectionAsync();
        }
    }

    private async Task<SlaMonitorReport> RunLockedAsync(DateTime now, CancellationToken ct)
    {
        var clock = await _clockProvider.GetAsync(ct);
        var policy = await _escalation.GetPolicyAsync(ct);

        int evaluated = 0, reminders = 0, escalations = 0;
        var breached = new List<SlaBreachCandidate>();
        var pendingNotifications = new List<PendingNotification>();

        Guid lastId = Guid.Empty;
        while (!ct.IsCancellationRequested)
        {
            var batch = await _context.Cases
                .Include(c => c.Owner)
                .Where(c => c.Id > lastId && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
                .OrderBy(c => c.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var c in batch)
            {
                evaluated++;
                try
                {
                    await EvaluateCaseAsync(c, clock, policy, now, breached, pendingNotifications,
                        () => reminders++, () => escalations++, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // One bad case must never stop the others from being monitored.
                    _logger.LogError(ex, "SLA evaluation failed for case {CaseNumber}.", c.CaseNumber);
                }
            }

            await _context.SaveChangesAsync(ct);
            _context.ChangeTracker.Clear();
        }

        foreach (var n in pendingNotifications)
        {
            try
            {
                await _notifications.CreateNotificationAsync(n.RecipientId, n.Type, n.Title, n.Message, n.CaseId, n.CaseNumber, n.Priority, 0, ct, n.EventKey);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not send {Type} notification for case {CaseNumber}.", n.Type, n.CaseNumber);
            }
        }

        try
        {
            await _notifications.PublishSlaBreachNotificationsAsync(breached, now, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not publish SLA breach notifications.");
        }

        try
        {
            await BackfillOutcomesAsync(clock, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not back-fill SLA outcomes.");
        }

        try
        {
            await _notifications.PurgeExpiredAsync(now, ct);   // housekeeping rides on the single-instance cycle
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not purge expired notifications.");
        }

        return new SlaMonitorReport(true, evaluated, reminders, escalations, breached.Count);
    }

    /// <summary>
    /// Cases finished before outcomes were recorded get theirs, a batch per cycle (so a large history never stalls a cycle).
    /// Until a case has one it counts as "Met" in reports — never as a breach it might not have had.
    /// </summary>
    private async Task BackfillOutcomesAsync(SlaClock clock, CancellationToken ct)
    {
        var finished = new[] { CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled };
        var batch = await _context.Cases.Where(c => finished.Contains(c.Status) && c.SlaOutcome == null).OrderBy(c => c.Id).Take(500).ToListAsync(ct);
        if (batch.Count == 0) return;

        foreach (var c in batch) c.SlaOutcome = clock.OutcomeOf(c, c.ResolvedAt ?? c.UpdatedAt ?? c.CreatedAt);
        await _context.SaveChangesAsync(ct);
        _context.ChangeTracker.Clear();
        _logger.LogInformation("Recorded the SLA outcome of {Count} finished cases.", batch.Count);
    }

    private async Task EvaluateCaseAsync(
        Case c, SlaClock clock, EscalationPolicy policy, DateTime now,
        List<SlaBreachCandidate> breached, List<PendingNotification> notifications,
        Action onReminder, Action onEscalation, CancellationToken ct)
    {
        // Cases created by older code paths may lack stored due dates; derive them once.
        if (c.InternalResolutionDueAt == null || c.ExternalResolutionDueAt == null) clock.RecomputeDueDates(c);

        var snapshot = clock.Evaluate(SlaInputs.From(c), now);

        if (!c.FirstResponseActualAt.HasValue && snapshot.FirstResponse.IsBreached && c.FirstResponseStatus != "Breached")
            c.FirstResponseStatus = "Breached";

        if (snapshot.IsPaused) return;   // a paused clock neither breaches, reminds nor escalates

        if (snapshot.Internal.IsBreached && !c.SlaBreachedAt.HasValue)
            c.SlaBreachedAt = now;

        if (snapshot.External.IsBreached)
            breached.Add(new SlaBreachCandidate(c.Id, c.CaseNumber, c.Title, c.OwnerId, c.SlaStartTime));

        // Automatic escalation only NOTIFIES: the case keeps its holder and its status. Reaching a level records it on the
        // case (so manual escalation continues from the next level) and tells whoever the level is configured to tell.
        var decision = _escalation.FindDueLevel(c, snapshot, policy, now);
        if (decision == null) return;

        var level = decision.Level;
        var recipients = await _escalation.NotificationRecipientsAsync(c, level, ct);
        var names = recipients.Count == 0
            ? new List<string>()
            : await _context.Users.AsNoTracking().Where(u => recipients.Contains(u.Id)).OrderBy(u => u.Name).Select(u => u.Name).ToListAsync(ct);

        var previous = c.EscalationLevel;
        c.EscalationLevel = level.LevelNumber;
        c.SlaReminderSent = true;
        onEscalation();

        var notified = names.Count > 0 ? string.Join(", ", names) : $"nobody — no active user holds '{level.TargetRole}'";
        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = c.Id,
            EventType = EventType.Escalate,
            ActionType = "AUTO_ESCALATION",
            Module = "SlaEscalation",
            OldValue = previous > 0 ? $"Level {previous}" : "Not escalated",
            NewValue = level.Name,
            Message = $"[Automatic Escalation] Reached {level.Name} ({level.TargetRole}). Trigger: {decision.Reason}. Reason: {level.ActionDescription}. Notified: {notified}.",
            IsInternal = true,
            CreatedAt = now,
            UserId = c.OwnerId
        });

        foreach (var recipient in recipients)
        {
            notifications.Add(new PendingNotification(recipient, "CASE_AUTOMATICALLY_ESCALATED", $"Escalation {level.Name}: Case {c.CaseNumber}",
                $"Case {c.CaseNumber} ({c.Title}) reached {level.Name} — {decision.Reason}. {level.ActionDescription}",
                c.Id, c.CaseNumber, level.NotificationPriority, $"escalation:{c.Id}:{level.LevelNumber}"));
        }
    }

    private sealed record PendingNotification(Guid RecipientId, string Type, string Title, string Message, Guid CaseId, string CaseNumber, string Priority, string EventKey);
}
