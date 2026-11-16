namespace CaseManagement.Api.Services.Tickets;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>What a ticket's SLA reads as.</summary>
public static class TicketSlaStates
{
    /// <summary>No target (the priority's assign-within time is 0).</summary>
    public const string None = "None";
    public const string Healthy = "Healthy";
    /// <summary>Waiting, and past the configured share of its target (the same percent cases use).</summary>
    public const string Approaching = "Approaching";
    /// <summary>Waiting past its target, or assigned/closed after it.</summary>
    public const string Breached = "Breached";
    /// <summary>Assigned or closed within its target.</summary>
    public const string Met = "Met";
}

public static class TicketSlaOutcomes
{
    public const string Met = "Met";
    public const string Breached = "Breached";
}

/// <param name="ConsumedMinutes">Working minutes waited so far (or until it was handled).</param>
/// <param name="RemainingMinutes">Target minus consumed; negative once it is late.</param>
/// <param name="DueAt">When it must be assigned by; null when there is no target or it was handled.</param>
/// <param name="IsRunning">Still waiting (status New).</param>
/// <param name="IsOutsideHours">Waiting, but outside working hours: the clock is not counting right now.</param>
public sealed record TicketSlaSnapshot(
    int TargetMinutes, double ConsumedMinutes, double ConsumedPercent, double RemainingMinutes, DateTime? DueAt,
    string State, bool IsRunning, bool IsOutsideHours);

/// <summary>
/// THE ticket SLA: how long a ticket may wait before someone assigns it, in working time of the same calendar cases use.
/// It is the queue's clock and nothing else: the case made from the ticket starts its own SLA when it is created.
/// </summary>
/// <remarks>
/// Pure arithmetic over <see cref="BusinessCalendar"/>, so the list, the drawer, the monitor and assignment can never disagree.
/// A waiting ticket is late the moment it reaches its target (like a running case); a handled one is late only if it went past it.
/// </remarks>
public sealed class TicketSlaClock(BusinessCalendar calendar, decimal? approachingPercent)
{
    /// <summary>Wall-clock time, no "approaching" state: for callers that run without the configured calendar (tests, fallbacks).</summary>
    public static TicketSlaClock RoundTheClock { get; } = new(BusinessCalendar.RoundTheClock, null);

    /// <summary>A ticket clock on the case clock's calendar and approaching percent.</summary>
    public static TicketSlaClock From(SlaClock clock) => new(clock.Calendar, clock.ApproachingPercent);

    public DateTime? DueAt(DateTime receivedAtUtc, int targetMinutes) =>
        targetMinutes > 0 ? calendar.AddBusinessMinutes(receivedAtUtc, targetMinutes) : null;

    /// <param name="stoppedAtUtc">When the ticket was assigned or closed; null while it waits.</param>
    public TicketSlaSnapshot Evaluate(DateTime receivedAtUtc, int targetMinutes, DateTime? stoppedAtUtc, DateTime nowUtc)
    {
        var running = stoppedAtUtc == null;
        // Decisions use the exact minutes; only the numbers shown are rounded (0.97 of a 1-minute target is not yet late).
        var exact = calendar.ElapsedBusinessMinutes(receivedAtUtc, stoppedAtUtc ?? nowUtc);
        var consumed = Math.Round(exact, 1);
        var outsideHours = running && !calendar.IsWorkingTime(nowUtc);
        if (targetMinutes <= 0)
            return new TicketSlaSnapshot(0, consumed, 0, 0, null, TicketSlaStates.None, running, outsideHours);

        var percent = Math.Round(exact / targetMinutes * 100, 1);
        var breached = running ? exact >= targetMinutes : exact > targetMinutes;
        string state;
        if (!running) state = breached ? TicketSlaStates.Breached : TicketSlaStates.Met;
        else if (breached) state = TicketSlaStates.Breached;
        else if (approachingPercent.HasValue && percent >= (double)approachingPercent.Value) state = TicketSlaStates.Approaching;
        else state = TicketSlaStates.Healthy;

        return new TicketSlaSnapshot(targetMinutes, consumed, percent, Math.Round(targetMinutes - exact, 1),
            running ? DueAt(receivedAtUtc, targetMinutes) : null, state, running, outsideHours);
    }

    /// <summary>The ticket was assigned or closed: records how long it waited and whether it met its target (once).</summary>
    public void Stop(Ticket ticket, int targetMinutes, DateTime nowUtc)
    {
        ticket.AssignTargetMinutes ??= targetMinutes;
        ticket.AssignDueAt ??= DueAt(ticket.ReceivedAt, targetMinutes);
        var waited = Math.Round(calendar.ElapsedBusinessMinutes(ticket.ReceivedAt, nowUtc), 1);
        ticket.AssignedWithinMinutes = waited;
        ticket.SlaOutcome = targetMinutes > 0 ? (waited > targetMinutes ? TicketSlaOutcomes.Breached : TicketSlaOutcomes.Met) : null;
    }
}

/// <summary>
/// Where a ticket's assign-within target comes from: its priority's <see cref="PrioritySlaRule.TicketAssignMinutes"/> (matched
/// whatever the spelling), else the default on <see cref="BusinessCalendarSetting"/>, else 30 minutes. A target already taken
/// when the ticket arrived always wins, so changing the configuration never rewrites a ticket's history.
/// </summary>
public sealed class TicketSlaTargets
{
    public const int FallbackMinutes = 30;

    private readonly Dictionary<string, int> byPriority;
    private readonly int defaultMinutes;

    private TicketSlaTargets(Dictionary<string, int> byPriority, int defaultMinutes)
    {
        this.byPriority = byPriority;
        this.defaultMinutes = defaultMinutes;
    }

    public static async Task<TicketSlaTargets> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        // Every rule, active or not: a ticket keeps the priority it arrived with.
        var rules = await db.PrioritySlaRules.AsNoTracking().Select(r => new { r.Priority, r.TicketAssignMinutes }).ToListAsync(ct);
        var byPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules) byPriority[r.Priority.Trim()] = Math.Max(0, r.TicketAssignMinutes);
        var configured = await db.BusinessCalendarSettings.AsNoTracking().Select(s => (int?)s.TicketDefaultAssignMinutes).FirstOrDefaultAsync(ct);
        return new TicketSlaTargets(byPriority, Math.Max(0, configured ?? FallbackMinutes));
    }

    public int For(string? priority) =>
        !string.IsNullOrWhiteSpace(priority) && byPriority.TryGetValue(priority.Trim(), out var minutes) ? minutes : defaultMinutes;

    public int Of(Ticket ticket) => ticket.AssignTargetMinutes ?? For(ticket.Priority);
}

/// <summary>Starting a new ticket's clock, wherever a ticket is born (raised by hand, or opened by an email).</summary>
public static class TicketSla
{
    /// <summary>Takes the ticket's target from its priority now, and its due time from the calendar.</summary>
    public static async Task StartAsync(AppDbContext db, Ticket ticket, TicketSlaClock clock, CancellationToken ct)
    {
        var target = (await TicketSlaTargets.LoadAsync(db, ct)).For(ticket.Priority);
        ticket.AssignTargetMinutes = target;
        ticket.AssignDueAt = clock.DueAt(ticket.ReceivedAt, target);
    }

    /// <summary>
    /// The ticket clock on the configured calendar, or the wall-clock one when the calendar cannot be read (it is incomplete, or
    /// no provider is wired in): a ticket must never fail to open, or a list fail to load, because of the SLA configuration.
    /// </summary>
    public static async Task<TicketSlaClock> ClockAsync(ISlaClockProvider? clocks, ILogger logger, CancellationToken ct)
    {
        if (clocks == null) return TicketSlaClock.RoundTheClock;
        try
        {
            return TicketSlaClock.From(await clocks.GetAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "The business calendar could not be read; ticket SLAs use wall-clock time until it is fixed.");
            return TicketSlaClock.RoundTheClock;
        }
    }
}
