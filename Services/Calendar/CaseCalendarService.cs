namespace CaseManagement.Api.Services.Calendar;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Statuses;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The calendar's event kinds, each read from real data — never invented: a case's creation; its first-response and resolution
/// deadlines (the SLA engine's); a resolution deadline already passed (SLA breach) and every escalation recorded on the case;
/// meetings; customer follow-ups (emails the customer sent on the case after it was opened); SLA reminders the monitor sent;
/// a status's pause limit; and resolutions.
/// </summary>
public static class CalendarEventTypes
{
    public const string Created = "created";
    public const string FirstResponseDue = "firstResponseDue";
    /// <summary>A resolution deadline still ahead (once passed on an open case it is a <see cref="Breach"/>, never both).</summary>
    public const string SlaDue = "slaDue";
    /// <summary>A passed resolution deadline of an open case, and each escalation (automatic or manual) recorded on a case.</summary>
    public const string Breach = "breach";
    public const string Meeting = "meeting";
    /// <summary>An email the customer sent on an open case (a reply on its thread).</summary>
    public const string FollowUp = "followUp";
    /// <summary>An SLA reminder the monitor sent the case's holder about a breached case.</summary>
    public const string Reminder = "reminder";
    public const string PauseEnds = "pauseEnds";
    public const string Resolved = "resolved";
    public static readonly string[] All = [Created, FirstResponseDue, SlaDue, Breach, Meeting, FollowUp, Reminder, PauseEnds, Resolved];
}

public sealed class CalendarEventDto
{
    /// <summary>Stable per event (type + case or meeting), so the page can keep a selection across refreshes.</summary>
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public Guid CaseId { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string CaseTitle { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    /// <summary>An SLA deadline already passed (shown in red).</summary>
    public bool IsBreached { get; set; }
    public Guid? MeetingId { get; set; }
    public string? MeetingStatus { get; set; }
    public string? JoinUrl { get; set; }
    public string? OrganizerName { get; set; }
    public string? AttendeeEmail { get; set; }
    public string? Detail { get; set; }
}

public sealed class CalendarResultDto
{
    public List<CalendarEventDto> Events { get; set; } = new();
    public string TimeZone { get; set; } = "UTC";
    /// <summary>More open cases matched than one view reads at once: deadlines of the rest are not shown (narrow the filters).</summary>
    public bool Truncated { get; set; }
    /// <summary>Cases whose SLA is paused have no deadline; how many matched.</summary>
    public int PausedCases { get; set; }
    public bool CanSeeOthers { get; set; }
}

public sealed record CalendarQuery(DateTime From, DateTime To, string[]? Types, Guid? TeamId, Guid? OwnerId, string? Status, string? Priority, bool Mine);

public interface ICaseCalendarService
{
    Task<CalendarResultDto> GetAsync(CalendarQuery q, CancellationToken ct = default);
}

/// <summary>
/// The Case Management Calendar: the dated things about the cases the caller may see, in one range — when cases were created, when
/// their SLA falls due, meetings booked with customers, and when a status's pause limit runs out.
/// </summary>
/// <remarks>
/// <para><b>Nothing is stored for the calendar.</b> Every event is read from where it lives (cases, meetings, pauses), and SLA
/// deadlines come from the same SLA engine as everywhere else (<see cref="SlaClock.Evaluate"/>: business hours, holidays and
/// pauses included). A paused case has no deadline while paused, so it shows none — the result says how many were paused.</para>
/// <para><b>Visibility is the case rule</b> (<see cref="ICaseAccessScope"/>): an agent sees their own and collaboration cases'
/// events only. Deadlines are worked out for at most <see cref="MaxOpenCases"/> open cases per request.</para>
/// </remarks>
public sealed class CaseCalendarService(AppDbContext db, ISlaClockProvider clocks, ICaseAccessScope access, ICurrentUserAccessor currentUser,
    ICaseStatusCatalog statuses) : ICaseCalendarService
{
    public const int MaxOpenCases = 3000;
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(62);

    public async Task<CalendarResultDto> GetAsync(CalendarQuery q, CancellationToken ct = default)
    {
        if (q.To <= q.From) throw new ArgumentException("The end of the range must be after its start.");
        if (q.To - q.From > MaxRange) throw new ArgumentException("Show at most two months at a time.");
        var types = (q.Types is { Length: > 0 } ? q.Types : CalendarEventTypes.All).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clock = await clocks.GetAsync(ct);
        var catalog = await statuses.GetAsync(ct);
        var now = DateTime.UtcNow;
        var result = new CalendarResultDto { TimeZone = clock.Calendar.TimeZone.Id, CanSeeOthers = access.SeesAll || currentUser.Has(Permissions.CasesViewTeam) };

        var cases = Filtered(q);

        if (types.Contains(CalendarEventTypes.Created))
        {
            var created = await Project(cases.Where(c => c.CreatedAt >= q.From && c.CreatedAt < q.To).OrderBy(c => c.CreatedAt).Take(2000)).ToListAsync(ct);
            result.Events.AddRange(created.Select(c => Event(c, CalendarEventTypes.Created, $"{c.CaseNumber} created", c.CreatedAt, catalog, detail: c.CustomerName)));
        }

        if (types.Contains(CalendarEventTypes.SlaDue) || types.Contains(CalendarEventTypes.Breach) || types.Contains(CalendarEventTypes.FirstResponseDue))
        {
            // Ordered before the projection: Postgres cannot sort by a member of the projected row.
            var open = await Project(cases.Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
                .OrderBy(c => c.CreatedAt).Take(MaxOpenCases + 1)).ToListAsync(ct);
            result.Truncated = open.Count > MaxOpenCases;
            foreach (var c in open.Take(MaxOpenCases))
            {
                var sla = clock.Evaluate(new SlaInputs
                {
                    Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                    FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                    ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                }, now);
                if (sla.IsPaused) { result.PausedCases++; continue; }
                if (sla.Internal.DueAt is { } due && due >= q.From && due < q.To)
                {
                    // One event per deadline: ahead it is "resolution due", passed it is the breach.
                    var passed = sla.Internal.IsBreached || due < now;
                    var kind = passed ? CalendarEventTypes.Breach : CalendarEventTypes.SlaDue;
                    if (types.Contains(kind))
                        result.Events.Add(Event(c, kind, passed ? $"{c.CaseNumber} SLA breached" : $"{c.CaseNumber} resolution due", due, catalog,
                            breached: passed, detail: passed ? "The resolution deadline passed while the case was still open." : null));
                }
                if (types.Contains(CalendarEventTypes.FirstResponseDue) && sla.FirstResponse.DueAt is { } fr && fr >= q.From && fr < q.To)
                    result.Events.Add(Event(c, CalendarEventTypes.FirstResponseDue, $"{c.CaseNumber} first response due", fr, catalog, breached: sla.FirstResponse.IsBreached || fr < now));
            }
        }

        if (types.Contains(CalendarEventTypes.Meeting))
        {
            var visible = cases.Select(c => c.Id);
            var meetings = await db.CaseMeetings.AsNoTracking()
                .Where(m => visible.Contains(m.CaseId) && m.StartUtc < q.To && m.EndUtc > q.From)
                .Select(m => new { Meeting = m, Organizer = m.Organizer!.Name }).Take(2000).ToListAsync(ct);
            var caseRows = await Project(db.Cases.AsNoTracking().Where(c => meetings.Select(x => x.Meeting.CaseId).Contains(c.Id))).ToListAsync(ct);
            foreach (var row in meetings)
            {
                var c = caseRows.FirstOrDefault(x => x.Id == row.Meeting.CaseId);
                if (c == null) continue;
                var e = Event(c, CalendarEventTypes.Meeting, row.Meeting.Title, row.Meeting.StartUtc, catalog, detail: row.Meeting.Agenda);
                e.Id = $"meeting:{row.Meeting.Id}";
                e.End = DateTime.SpecifyKind(row.Meeting.EndUtc, DateTimeKind.Utc);
                e.MeetingId = row.Meeting.Id;
                e.MeetingStatus = row.Meeting.Status;
                e.JoinUrl = row.Meeting.Status == CaseMeetingStatuses.Cancelled ? null : row.Meeting.JoinUrl;
                e.OrganizerName = row.Organizer;
                e.AttendeeEmail = row.Meeting.AttendeeEmail;
                result.Events.Add(e);
            }
        }

        if (types.Contains(CalendarEventTypes.PauseEnds))
        {
            var limited = catalog.Definitions.Where(d => d.SlaBehaviour == SlaBehaviours.Paused && d.MaxPauseMinutes > 0).ToList();
            if (limited.Count > 0)
            {
                var paused = await Project(cases.Where(c => c.SlaPausedAt != null).OrderBy(c => c.SlaPausedAt).Take(MaxOpenCases)).ToListAsync(ct);
                foreach (var c in paused)
                {
                    var key = c.StatusKey ?? c.Status.ToString();
                    var d = limited.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (d == null) continue;
                    var ends = clock.Calendar.AddBusinessMinutes(c.StatusEnteredAt ?? c.SlaPausedAt!.Value, d.MaxPauseMinutes!.Value);
                    if (ends >= q.From && ends < q.To)
                        result.Events.Add(Event(c, CalendarEventTypes.PauseEnds, $"{c.CaseNumber} pause limit ({d.Label})", ends, catalog,
                            detail: $"After {PauseLimitService.Describe(d.MaxPauseMinutes!.Value)} in {d.Label}: {d.OnPauseExpiry switch { PauseExpiryActions.MoveToStatus => "moves status", PauseExpiryActions.NotifyOnly => "holder notified", _ => "SLA resumes" }}."));
                }
            }
        }

        // What happened on the cases in the range, from their history: escalations, customer follow-ups, resolutions.
        var activityKinds = new List<string>();
        if (types.Contains(CalendarEventTypes.Breach)) activityKinds.Add("escalate");
        if (types.Contains(CalendarEventTypes.FollowUp)) activityKinds.Add("followUp");
        if (types.Contains(CalendarEventTypes.Resolved)) activityKinds.Add("resolve");
        if (activityKinds.Count > 0)
        {
            var visible = cases.Select(c => c.Id);
            var wantEscalate = activityKinds.Contains("escalate");
            var wantFollowUp = activityKinds.Contains("followUp");
            var wantResolve = activityKinds.Contains("resolve");
            var history = await db.CaseEvents.AsNoTracking()
                .Where(e => e.CaseId != null && visible.Contains(e.CaseId.Value) && e.CreatedAt >= q.From && e.CreatedAt < q.To
                    && ((wantEscalate && e.EventType == EventType.Escalate)
                        || (wantFollowUp && e.ActionType == "EMAIL_REPLY_RECEIVED")
                        || (wantResolve && e.EventType == EventType.Resolve)))
                .OrderBy(e => e.CreatedAt).Take(2000)
                .Select(e => new { e.Id, CaseId = e.CaseId!.Value, e.EventType, e.ActionType, e.Message, e.NewValue, e.CreatedAt })
                .ToListAsync(ct);
            var rows = await RowsAsync(history.Select(h => h.CaseId), ct);
            foreach (var h in history)
            {
                if (!rows.TryGetValue(h.CaseId, out var c)) continue;
                var (kind, title, detail) = h.EventType == EventType.Escalate
                    ? (CalendarEventTypes.Breach, $"{c.CaseNumber} escalated{(string.IsNullOrWhiteSpace(h.NewValue) ? "" : $" ({h.NewValue.Split(" · ")[^1]})")}", Shorten(h.Message))
                    : h.EventType == EventType.Resolve
                        ? (CalendarEventTypes.Resolved, $"{c.CaseNumber} resolved", Shorten(h.Message))
                        : (CalendarEventTypes.FollowUp, $"{c.CaseNumber} customer follow-up", Shorten(h.Message));
                var e = Event(c, kind, title, h.CreatedAt, catalog, detail: detail);
                e.Id = $"{kind}:{h.Id}";
                result.Events.Add(e);
            }
        }

        // SLA reminders the monitor sent the holder of a breached case (the latest one per breach).
        if (types.Contains(CalendarEventTypes.Reminder))
        {
            var visible = cases.Select(c => c.Id);
            var alerts = await db.CaseSlaAlerts.AsNoTracking()
                .Where(a => visible.Contains(a.CaseId) && a.LastReminderAt != null && a.LastReminderAt >= q.From && a.LastReminderAt < q.To)
                .Select(a => new { a.CaseId, a.LastReminderAt, a.Reminders }).Take(2000).ToListAsync(ct);
            var rows = await RowsAsync(alerts.Select(a => a.CaseId), ct);
            foreach (var a in alerts)
            {
                if (!rows.TryGetValue(a.CaseId, out var c)) continue;
                var e = Event(c, CalendarEventTypes.Reminder, $"{c.CaseNumber} SLA reminder", a.LastReminderAt!.Value, catalog,
                    detail: $"Reminder {a.Reminders} sent to {c.OwnerName}: the case is past its SLA.");
                e.Id = $"reminder:{a.CaseId}:{a.Reminders}";
                result.Events.Add(e);
            }
        }

        result.Events = result.Events.OrderBy(e => e.Start).ThenBy(e => e.CaseNumber).ToList();
        return result;
    }

    private IQueryable<Case> Filtered(CalendarQuery q)
    {
        var cases = access.Apply(db.Cases.AsNoTracking());
        var me = currentUser.LocalUserId;
        if (q.Mine && me != null) cases = cases.Where(c => c.OwnerId == me.Value);
        if (q.TeamId is { } team) cases = cases.Where(c => c.TeamId == team);
        if (q.OwnerId is { } owner) cases = cases.Where(c => c.OwnerId == owner);
        if (!string.IsNullOrWhiteSpace(q.Priority)) cases = cases.Where(c => c.Severity == q.Priority);
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var text = q.Status.Replace(" ", "").Replace("_", "");
            if (Enum.TryParse<CaseStatus>(text, true, out var system) && Enum.IsDefined(system)) cases = cases.Where(c => c.Status == system && c.StatusKey == null);
            else { var key = q.Status.Trim(); cases = cases.Where(c => c.StatusKey == key); }
        }
        return cases;
    }

    private async Task<Dictionary<Guid, Row>> RowsAsync(IEnumerable<Guid> caseIds, CancellationToken ct)
    {
        var ids = caseIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        return (await Project(db.Cases.AsNoTracking().Where(c => ids.Contains(c.Id))).ToListAsync(ct)).ToDictionary(r => r.Id);
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        return t.Length > 400 ? t[..399] + "…" : t;
    }

    private sealed record Row(Guid Id, string CaseNumber, string Title, CaseStatus Status, string? StatusKey, string Severity, string TeamName, Guid OwnerId,
        string OwnerName, string? CustomerName, DateTime CreatedAt, DateTime SlaStartTime, DateTime? ResolvedAt, DateTime? SlaPausedAt, int SlaTotalPausedMinutes,
        int FirstResponseTargetMinutes, int InternalResolutionTargetMinutes, int ExternalResolutionTargetMinutes, DateTime? FirstResponseActualAt,
        string FirstResponseStatus, DateTime? StatusEnteredAt);

    private static IQueryable<Row> Project(IQueryable<Case> cases) => cases.Select(c => new Row(
        c.Id, c.CaseNumber, c.Title, c.Status, c.StatusKey, c.Severity, c.Team.Name, c.OwnerId, c.Owner.Name,
        c.Customer != null ? c.Customer.FullName : null, c.CreatedAt, c.SlaStartTime, c.ResolvedAt, c.SlaPausedAt, c.SlaTotalPausedMinutes,
        c.FirstResponseTargetMinutes, c.InternalResolutionTargetMinutes, c.ExternalResolutionTargetMinutes, c.FirstResponseActualAt,
        c.FirstResponseStatus, c.StatusEnteredAt));

    private static CalendarEventDto Event(Row c, string type, string title, DateTime at, CaseStatusSnapshot catalog, bool breached = false, string? detail = null)
    {
        var key = c.StatusKey ?? c.Status.ToString();
        return new CalendarEventDto
        {
            Id = $"{type}:{c.Id}", Type = type, Title = title, Start = DateTime.SpecifyKind(at, DateTimeKind.Utc), CaseId = c.Id, CaseNumber = c.CaseNumber,
            CaseTitle = c.Title, StatusKey = key, StatusLabel = catalog.LabelOf(key), Priority = c.Severity, TeamName = c.TeamName, OwnerId = c.OwnerId,
            OwnerName = c.OwnerName, IsBreached = breached, Detail = detail,
        };
    }
}
