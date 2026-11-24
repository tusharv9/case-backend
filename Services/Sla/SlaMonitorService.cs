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
    private readonly Tickets.TicketSlaMonitor? _tickets;
    private readonly EmailIntake.ReviewSlaMonitor? _reviews;
    private readonly PauseLimitService? _pauseLimits;
    private readonly IEscalationExecutor _executor;

    public SlaMonitorService(
        AppDbContext context,
        ISlaClockProvider clockProvider,
        IEscalationService escalation,
        INotificationService notifications,
        ILogger<SlaMonitorService> logger,
        Tickets.TicketSlaMonitor? tickets = null,
        EmailIntake.ReviewSlaMonitor? reviews = null,
        PauseLimitService? pauseLimits = null,
        IEscalationExecutor? executor = null)
    {
        _pauseLimits = pauseLimits;
        _executor = executor ?? new EscalationExecutor(context);
        _context = context;
        _clockProvider = clockProvider;
        _escalation = escalation;
        _notifications = notifications;
        _logger = logger;
        _tickets = tickets;
        _reviews = reviews;
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

        // Statuses with a maximum pause (Configurable Settings → Status): act on the pauses that reached it, before evaluating SLAs.
        if (_pauseLimits != null)
        {
            try { await _pauseLimits.ApplyAsync(clock, now, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _logger.LogError(ex, "Applying pause limits failed."); }
        }

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

        // Tickets still waiting past their assign-within time (the queue's own SLA), under the same lock.
        if (_tickets != null)
        {
            try
            {
                await _tickets.RunAsync(Tickets.TicketSlaClock.From(clock), now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not check ticket SLAs.");
            }
        }

        // Emails from unregistered senders still waiting for review: reminders and escalations, under the same lock.
        if (_reviews != null)
        {
            try
            {
                await _reviews.RunAsync(Tickets.TicketSlaClock.From(clock), now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not check sender review deadlines.");
            }
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

        // The next level, when its trigger is met: one level per cycle, so no level is ever skipped (a case that missed several
        // thresholds catches up a level every cycle). Who receives it follows the level's routing and fallback.
        var decision = _escalation.FindDueLevel(c, snapshot, policy, now);
        if (decision == null) return;

        var target = await _escalation.ResolveAsync(c, decision.Level, policy, null, ct);
        var single = target.Recipients.Count == 1 ? target.Recipients[0].User.Id : (Guid?)null;
        var reason = string.IsNullOrWhiteSpace(decision.Level.ActionDescription)
            ? $"Escalated automatically because {decision.Reason}."
            : $"Escalated automatically because {decision.Reason}. {decision.Level.ActionDescription.Trim()}";
        try
        {
            await _executor.ApplyAsync(c, new EscalationCommand(EscalationTypes.Automatic, target,
                NewOwnerId: target.Level.AssignOnAuto ? single : null, ReasonText: reason,
                Trigger: decision.Trigger, SlaConsumedPercent: decision.SlaConsumedPercent), ct);
            onEscalation();
        }
        catch (CaseHandleException ex)
        {
            _logger.LogInformation("Automatic escalation of {CaseNumber} skipped: {Message}", c.CaseNumber, ex.Message);
        }
    }

    private sealed record PendingNotification(Guid RecipientId, string Type, string Title, string Message, Guid CaseId, string CaseNumber, string Priority, string EventKey);
}
