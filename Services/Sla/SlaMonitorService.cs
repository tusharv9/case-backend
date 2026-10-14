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

        // Early reminder: the first escalation level's trigger, sent to whoever holds the case.
        if (policy.ReminderPercent.HasValue && !c.SlaReminderSent && snapshot.Internal.ConsumedPercent >= (double)policy.ReminderPercent.Value)
        {
            c.SlaReminderSent = true;
            onReminder();
            var ownerName = c.Owner?.Name ?? "Agent";
            _context.CaseEvents.Add(new CaseEvent
            {
                CaseId = c.Id,
                EventType = EventType.Note,
                Message = $"SLA reached {policy.ReminderPercent.Value:0.##}% — reminder sent to assigned agent ({ownerName}).",
                IsInternal = true,
                CreatedAt = now,
                UserId = c.OwnerId
            });
            notifications.Add(new PendingNotification(c.OwnerId, "SLA_REMINDER", $"SLA {policy.ReminderPercent.Value:0.##}% Consumed",
                $"SLA has reached {policy.ReminderPercent.Value:0.##}% consumption for Case {c.CaseNumber} ({c.Title}). Please take the required action before SLA breach.",
                c.Id, c.CaseNumber, "Medium", $"reminder:{c.Id}"));
        }

        var decision = _escalation.FindDueLevel(c, snapshot, policy, now);
        if (decision == null) return;

        var level = decision.Level;
        var target = await _escalation.ResolveTargetAsync(c, level, ct);
        var oldOwnerName = c.Owner?.Name ?? "Agent";

        c.EscalationLevel = level.LevelNumber;
        c.Status = CaseStatus.Escalated;
        if (target != null && level.ReassignOwner) c.OwnerId = target.Id;
        onEscalation();

        var who = target != null ? $"{level.TargetRole}: {target.Name}" : $"no active user found for '{level.TargetRole}' — owner unchanged";
        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = c.Id,
            EventType = EventType.Escalate,
            Message = $"[Automatic Escalation] Escalated to {level.Name} ({who}) from {oldOwnerName}. Trigger: {decision.Reason}. Action: {level.ActionDescription}",
            IsInternal = true,
            CreatedAt = now,
            UserId = target?.Id ?? c.OwnerId
        });

        if (target != null)
        {
            notifications.Add(new PendingNotification(target.Id, "CASE_AUTOMATICALLY_ESCALATED", $"Automatic Escalation ({level.Name})",
                $"Case {c.CaseNumber} has been automatically escalated to you. Trigger: {decision.Reason}",
                c.Id, c.CaseNumber, "Critical", $"escalation:{c.Id}:{level.LevelNumber}"));
        }
    }

    private sealed record PendingNotification(Guid RecipientId, string Type, string Title, string Message, Guid CaseId, string CaseNumber, string Priority, string EventKey);
}
