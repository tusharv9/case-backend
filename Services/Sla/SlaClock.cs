namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

/// <summary>
/// THE definition of how a case's SLA clock behaves. Everything that reasons about SLA time — the monitor that escalates,
/// the dashboard, the notifications, the case cards — evaluates through this class, so a case cannot be "breached" in one
/// place and "healthy" in another.
///
/// The rules:
///  * Targets are BUSINESS minutes (working hours of the configured calendar; weekends/holidays do not count).
///  * The clock starts when the case is created and is NOT reset by reassignment.
///  * Waiting on Customer pauses the clock. The paused time is measured in business minutes and added to every due date,
///    so a pause postpones breach and escalation exactly as much as it should.
///  * Resolving stops the clock; the case then reads as Met or Breached for good.
///  * Health is judged on the external (customer-facing) target. The internal target is the stricter early warning that
///    drives escalation, so breaching it alone reads as Approaching.
/// </summary>
public sealed class SlaClock
{
    public BusinessCalendar Calendar { get; }

    /// <summary>Consumption (%) at which a case starts reading as Approaching, or null when none is configured.</summary>
    public decimal? ApproachingPercent { get; }

    public SlaClock(BusinessCalendar calendar, decimal? approachingPercent)
    {
        Calendar = calendar;
        ApproachingPercent = approachingPercent;
    }

    private static bool IsStopped(SlaInputs i) =>
        i.ResolvedAt.HasValue || i.Status is CaseStatus.Resolved or CaseStatus.Closed or CaseStatus.Cancelled;

    // --------------------------------------------------------------------------------------------- API enrichment

    /// <summary>Fills in the server's SLA verdict (and the calendar flags) on case DTOs read from the database.</summary>
    public void Enrich(IEnumerable<CaseSummaryDto?>? cases, DateTime? nowUtc = null)
    {
        if (cases == null) return;
        var now = nowUtc ?? DateTime.UtcNow;
        var holiday = Calendar.HolidayOn(now);
        var working = Calendar.IsWorkingTime(now);

        foreach (var c in cases)
        {
            if (c == null) continue;
            Enum.TryParse<CaseStatus>(c.Status, out var status);
            c.Sla = CaseSlaDto.From(Evaluate(new SlaInputs
            {
                Status = status,
                StartUtc = c.SlaStartTime,
                ResolvedAt = c.ResolvedAt,
                PausedAt = c.SlaPausedAt,
                PausedMinutes = c.SlaTotalPausedMinutes,
                FirstResponseTargetMinutes = c.FirstResponseTargetMinutes,
                InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                ExternalTargetMinutes = c.ExternalResolutionTargetMinutes,
                FirstResponseActualAt = c.FirstResponseActualAt,
                FirstResponseStatus = c.FirstResponseStatus,
            }, now));
            c.IsBusinessHoursActive = working;
            c.IsHolidayToday = holiday != null;
            c.HolidayName = holiday;
        }
    }

    // ---------------------------------------------------------------------------------------------- evaluation

    public SlaSnapshot Evaluate(SlaInputs i, DateTime nowUtc)
    {
        var stopped = IsStopped(i);
        var paused = !stopped && i.PausedAt.HasValue;

        // The moment the clock stopped counting: resolution, the start of the current pause, or now.
        var end = stopped ? (i.ResolvedAt ?? nowUtc) : (i.PausedAt ?? nowUtc);
        var elapsed = Calendar.ElapsedBusinessMinutes(i.StartUtc, end);

        SlaTargetState Measure(int targetMinutes, double consumedElapsed, bool countBreachAtEquality)
        {
            var consumed = Math.Max(0, consumedElapsed - i.PausedMinutes);
            var breached = targetMinutes > 0 && (countBreachAtEquality ? consumed >= targetMinutes : consumed > targetMinutes);
            var due = stopped || paused || targetMinutes <= 0
                ? (DateTime?)null
                : Calendar.AddBusinessMinutes(i.StartUtc, targetMinutes + i.PausedMinutes);
            return new SlaTargetState(
                targetMinutes,
                Math.Round(consumed, 1),
                targetMinutes > 0 ? Math.Round(targetMinutes - consumed, 1) : 0,
                targetMinutes > 0 ? Math.Round(consumed / targetMinutes * 100, 1) : 0,
                due,
                breached);
        }

        var running = !stopped;
        var internalState = Measure(i.InternalTargetMinutes, elapsed, running);
        var externalState = Measure(i.ExternalTargetMinutes, elapsed, running);

        // First response is measured up to the moment it happened (or up to now while still pending).
        var frEnd = i.FirstResponseActualAt.HasValue && i.FirstResponseActualAt.Value < end ? i.FirstResponseActualAt.Value : end;
        var frElapsed = Calendar.ElapsedBusinessMinutes(i.StartUtc, frEnd);
        var frState = Measure(i.FirstResponseTargetMinutes, frElapsed, !i.FirstResponseActualAt.HasValue && running);
        // Once answered, the verdict recorded at that moment is final.
        var frStatus = i.FirstResponseActualAt.HasValue ? i.FirstResponseStatus
            : frState.IsBreached ? "Breached" : "Pending";
        if (i.FirstResponseActualAt.HasValue)
            frState = frState with { IsBreached = string.Equals(i.FirstResponseStatus, "Breached", StringComparison.OrdinalIgnoreCase), DueAt = null };

        SlaHealth health;
        if (stopped) health = externalState.IsBreached ? SlaHealth.Breached : SlaHealth.Met;
        else if (externalState.IsBreached) health = SlaHealth.Breached;
        else if (paused) health = SlaHealth.Paused;
        else if (internalState.IsBreached || (ApproachingPercent.HasValue && externalState.ConsumedPercent >= (double)ApproachingPercent.Value)) health = SlaHealth.Approaching;
        else health = SlaHealth.Healthy;

        return new SlaSnapshot(health, paused, stopped, !stopped && !paused && Calendar.IsWorkingTime(nowUtc),
            internalState, externalState, frState, frStatus, nowUtc);
    }

    // ----------------------------------------------------------------------------------------------- lifecycle
    // These are the only places that change a case's SLA fields. Callers decide WHEN (status changes, assignment…);
    // this class decides WHAT that means for the clock.

    /// <summary>Starts a case's clock from a priority's rule, snapshotting the targets so later configuration changes never rewrite history.</summary>
    public void Start(Case c, PrioritySlaRule rule, DateTime nowUtc)
    {
        c.SlaStartTime = nowUtc;
        c.SlaPausedAt = null;
        c.SlaTotalPausedMinutes = 0;
        c.SlaBreachedAt = null;
        c.SlaReminderSent = false;
        c.EscalationLevel = 1;
        c.FirstResponseActualAt = null;
        c.FirstResponseStatus = "Pending";
        SnapshotRule(c, rule);
        RecomputeDueDates(c);
    }

    /// <summary>The case's priority changed: take the new priority's targets, keeping the original start and any time already paused.</summary>
    public void ApplyRule(Case c, PrioritySlaRule rule)
    {
        SnapshotRule(c, rule);
        RecomputeDueDates(c);
    }

    private static void SnapshotRule(Case c, PrioritySlaRule rule)
    {
        c.FirstResponseTargetMinutes = rule.FirstResponseMinutes;
        c.InternalResolutionTargetMinutes = rule.InternalResolutionMinutes;
        c.ExternalResolutionTargetMinutes = rule.ExternalResolutionMinutes;
        c.SlaTargetHours = (int)Math.Ceiling(rule.ExternalResolutionMinutes / 60.0);
        c.SlaConfigVersion = rule.Version;
    }

    public void Pause(Case c, DateTime nowUtc)
    {
        c.SlaPausedAt ??= nowUtc;
    }

    /// <summary>Ends a pause: the working time that passed is added to the paused total and to every due date.</summary>
    public void Resume(Case c, DateTime nowUtc)
    {
        if (!c.SlaPausedAt.HasValue) return;

        c.SlaTotalPausedMinutes += (int)Math.Round(Calendar.ElapsedBusinessMinutes(c.SlaPausedAt.Value, nowUtc));
        c.SlaPausedAt = null;
        RecomputeDueDates(c);
    }

    /// <summary>Records the first customer-facing response (once) and whether it met its target.</summary>
    public void RecordFirstResponse(Case c, DateTime nowUtc)
    {
        if (c.FirstResponseActualAt.HasValue) return;

        var end = c.SlaPausedAt ?? nowUtc;
        var consumed = Math.Max(0, Calendar.ElapsedBusinessMinutes(c.SlaStartTime, end) - c.SlaTotalPausedMinutes);
        c.FirstResponseActualAt = nowUtc;
        c.FirstResponseStatus = c.FirstResponseTargetMinutes > 0 && consumed > c.FirstResponseTargetMinutes ? "Breached" : "Met";
    }

    /// <summary>Stops the clock for good (resolution): ends any pause and settles the first-response verdict.</summary>
    public void Stop(Case c, DateTime nowUtc)
    {
        Resume(c, nowUtc);
        RecordFirstResponse(c, nowUtc);
        c.SlaOutcome = OutcomeOf(c, c.ResolvedAt ?? nowUtc);
    }

    /// <summary>"Breached" if the (paused-adjusted) business time up to <paramref name="endUtc"/> exceeded the external target; otherwise "Met".</summary>
    public string OutcomeOf(Case c, DateTime endUtc)
    {
        var consumed = Math.Max(0, Calendar.ElapsedBusinessMinutes(c.SlaStartTime, endUtc) - c.SlaTotalPausedMinutes);
        return c.ExternalResolutionTargetMinutes > 0 && consumed > c.ExternalResolutionTargetMinutes ? "Breached" : "Met";
    }

    /// <summary>Re-derives the stored due dates (used by SQL pre-filters and exports) from start + target + time paused.</summary>
    public void RecomputeDueDates(Case c)
    {
        DateTime? Due(int target) => target > 0 ? Calendar.AddBusinessMinutes(c.SlaStartTime, target + c.SlaTotalPausedMinutes) : null;

        c.InternalResolutionDueAt = Due(c.InternalResolutionTargetMinutes);
        c.ExternalResolutionDueAt = Due(c.ExternalResolutionTargetMinutes);
        if (!c.FirstResponseActualAt.HasValue) c.FirstResponseDueAt = Due(c.FirstResponseTargetMinutes);
    }
}
