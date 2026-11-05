namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public sealed record DashboardQuery(
    Guid? TeamId, string? CaseType, string? Status, string? Severity,
    string? DateRange, string? CustomStartDate, string? CustomEndDate, Guid? OwnerId, Guid? DepartmentId = null);

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(DashboardQuery query, CancellationToken ct = default);
    Task<DashboardFiltersDto> GetFiltersAsync(CancellationToken ct = default);

    /// <summary>
    /// Every case the report's filters select (not one page of them), as CSV — within what the caller may see, with the
    /// customer's name masked by the same rules as on screen. <paramref name="slaStatus"/> and <paramref name="search"/>
    /// are the Reports page's SLA and search filters.
    /// </summary>
    Task<ReportExport> ExportAsync(DashboardQuery query, string? slaStatus, string? search, CancellationToken ct = default);
}

/// <param name="GeneratedOn">Today in the business time zone, for the file name.</param>
public sealed record ReportExport(string Csv, int RowCount, bool Truncated, DateTime GeneratedOn);

/// <summary>
/// The dashboard. All counts are database aggregates; SLA health is the SLA clock's verdict (the same one the board, the worker and
/// the notifications use); and "today", "this week"… are measured in the BUSINESS time zone, not the server's.
/// </summary>
public class DashboardService : IDashboardService
{
    /// <summary>The date ranges the summary can actually compute. A configured range outside this set is not offered.</summary>
    public static readonly IReadOnlyList<string> SupportedDateRanges = new[]
    {
        "today", "this_week", "last_week", "this_month", "last_month", "this_quarter", "this_year", "custom", "all"
    };

    private readonly AppDbContext _context;
    private readonly ISlaClockProvider _slaClock;
    private readonly IPiiMaskingService? _piiMasking;

    private readonly HostIntegration.ICaseAccessScope? _caseAccess;
    private readonly IDbContextFactory<AppDbContext>? _contexts;
    private readonly Customer360.ICaseCustomerNames? _customerNames;

    /// <summary>The most rows one export holds; past it the file says it was cut short (narrow the filters instead).</summary>
    public const int MaxExportRows = 50_000;

    /// <param name="caseAccess">Every figure and list covers only the cases the caller may see.</param>
    public DashboardService(AppDbContext context, ISlaClockProvider slaClock, IPiiMaskingService? piiMasking = null, HostIntegration.ICaseAccessScope? caseAccess = null, IDbContextFactory<AppDbContext>? contexts = null, Customer360.ICaseCustomerNames? customerNames = null)
    {
        _caseAccess = caseAccess;
        _contexts = contexts;
        _customerNames = customerNames;
        _context = context;
        _slaClock = slaClock;
        _piiMasking = piiMasking;
    }

    /// <summary>The caller sees every team's cases (no scope = background work or a unit test, which is unrestricted).</summary>
    private bool SeesAllTeams => _caseAccess?.SeesAll ?? true;

    // ------------------------------------------------------------------------------------------------ filters

    public async Task<DashboardFiltersDto> GetFiltersAsync(CancellationToken ct = default)
    {
        static Task<List<FilterOptionDto>> Lookup(AppDbContext db, string code, CancellationToken ct) =>
            db.LookupValues.AsNoTracking()
                .Where(v => v.LookupType!.Code == code && v.IsActive)
                .OrderBy(v => v.DisplayOrder).ThenBy(v => v.Value)
                .Select(v => new FilterOptionDto { Value = v.Value, Label = string.IsNullOrEmpty(v.Label) ? v.Value : v.Label })
                .ToListAsync(ct);

        var seesAll = SeesAllTeams;
        var result = new DashboardFiltersDto { CanFilterByTeam = seesAll, SeesAllTeams = seesAll };
        List<FilterOptionDto> ranges = new();

        // Seven small, independent reads: side by side, not one after another (each is a round trip to the database).
        await ReadAllAsync(ct,
            async db => result.Teams = !seesAll ? new List<NamedOptionDto>() : await db.Teams.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
                .Select(d => new NamedOptionDto { Id = d.Id, Name = d.Name }).ToListAsync(ct),
            async db => result.Departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name)
                .Select(d => new NamedOptionDto { Id = d.Id, Name = d.Name }).ToListAsync(ct),
            async db => result.CaseTypes = await db.CaseTypeConfigs.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.DisplayOrder)
                .Select(t => new FilterOptionDto { Value = t.Name, Label = t.Name }).ToListAsync(ct),
            async db => result.Statuses = await Lookup(db, "CASE_STATUS", ct),
            async db => result.Priorities = await db.PrioritySlaRules.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.DisplayOrder).Select(r => r.Priority).ToListAsync(ct),
            async db => result.SlaStatuses = await Lookup(db, "SLA_STATUS", ct),
            async db => ranges = await Lookup(db, "DASHBOARD_DATE_RANGE", ct),
            async db => result.QuickActions = await Lookup(db, "DASHBOARD_QUICK_ACTION", ct));

        result.DateRanges = ranges.Where(r => SupportedDateRanges.Contains(r.Value, StringComparer.OrdinalIgnoreCase)).ToList();
        return result;
    }

    // ------------------------------------------------------------------------------------------------ summary

    /// <summary>
    /// The dashboard in two rounds of reads instead of a long chain of them. Every figure here is its own small query, and
    /// each costs one round trip to the database however small it is, so they run side by side — each on its own pooled
    /// context — and the page waits for the slowest one rather than the sum of all of them. Without a context factory
    /// (a test that builds the service by hand) the same reads simply run one after another on the request's context.
    /// </summary>
    private static CaseStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Enum.TryParse<CaseStatus>(value.Replace(" ", "").Replace("_", ""), true, out var status))
            throw new ArgumentException($"'{value}' is not a case status.");
        return status;
    }

    /// <summary>
    /// The cases one set of report filters selects, always within what the caller may see. Filtering by team is reserved
    /// for people who see every team (see <see cref="DashboardFiltersDto.CanFilterByTeam"/>); anyone else's team
    /// filter is ignored rather than allowed to suggest a cross-team view they do not have.
    /// </summary>
    private IQueryable<Case> ApplyFilters(AppDbContext db, DashboardQuery q, CaseStatus? statusFilter, DateTime? start, DateTime? end)
    {
        IQueryable<Case> query = _caseAccess?.Apply(db.Cases.AsNoTracking(), db) ?? db.Cases.AsNoTracking();
        if (q.TeamId.HasValue && SeesAllTeams) query = query.Where(c => c.TeamId == q.TeamId.Value);
        if (q.DepartmentId.HasValue) query = query.Where(c => c.DepartmentId == q.DepartmentId.Value);
        if (!string.IsNullOrWhiteSpace(q.CaseType)) query = query.Where(c => c.CaseType == q.CaseType);
        if (!string.IsNullOrWhiteSpace(q.Severity)) query = query.Where(c => c.Severity == q.Severity);
        if (q.OwnerId.HasValue) query = query.Where(c => c.OwnerId == q.OwnerId.Value);
        if (statusFilter.HasValue) query = query.Where(c => c.Status == statusFilter.Value);
        if (start.HasValue) query = query.Where(c => c.CreatedAt >= start.Value);
        if (end.HasValue) query = query.Where(c => c.CreatedAt < end.Value);
        return query;
    }

    public async Task<DashboardSummaryDto> GetSummaryAsync(DashboardQuery q, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var clock = await _slaClock.GetAsync(ct);
        var zone = clock.Calendar.TimeZone;

        var statusFilter = ParseStatus(q.Status);
        var (start, end) = DateRange(q.DateRange, q.CustomStartDate, q.CustomEndDate, now, zone);

        // The filtered cases, built on whichever context runs the read (a query cannot mix context instances).
        IQueryable<Case> Filtered(AppDbContext db) => ApplyFilters(db, q, statusFilter, start, end);

        var emptyGuid = Guid.Empty;
        var finished = new[] { CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled };
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(now, zone).Date;
        var thisMonday = localToday.AddDays(-(((int)localToday.DayOfWeek + 6) % 7));
        var horizon = ToUtc(new DateTime(localToday.Year, localToday.Month, 1).AddMonths(-5), zone);

        // ---- round 1: every independent read --------------------------------------------------------------------
        var total = 0;
        Dictionary<CaseStatus, int> byStatus = new();
        List<(string Severity, int Count)> bySeverity = new();
        var unassigned = 0;
        List<(string Priority, int DisplayOrder)> order = new();
        List<TeamCaseCount> byTeam = new();
        List<CaseTypeCaseCount> byType = new();
        List<(Guid Id, CaseStatus Status, string Severity, SlaInputs Inputs)> inputs = new();
        var finishedBreached = 0;
        List<(int Year, int Month, int Day, int Hour, int Quarter, int N)> buckets = new();
        List<Guid> recentIds = new();
        List<RecentActivityItem> activities = new();

        await ReadAllAsync(ct,
            async db => total = await Filtered(db).CountAsync(ct),
            async db => byStatus = await Filtered(db).GroupBy(c => c.Status).Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Status, x => x.Count, ct),
            async db => bySeverity = (await Filtered(db).GroupBy(c => c.Severity).Select(g => new { Severity = g.Key, Count = g.Count() }).ToListAsync(ct))
                .Select(x => (x.Severity, x.Count)).ToList(),
            async db => unassigned = await Filtered(db).CountAsync(c => c.OwnerId == emptyGuid, ct),
            async db => order = (await db.PrioritySlaRules.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.DisplayOrder)
                .Select(r => new { r.Priority, r.DisplayOrder }).ToListAsync(ct)).Select(x => (x.Priority, x.DisplayOrder)).ToList(),
            async db => byTeam = await Filtered(db)
                .GroupBy(c => new { c.TeamId, c.Team.Name })
                .Select(g => new TeamCaseCount { TeamId = g.Key.TeamId, TeamName = g.Key.Name, Count = g.Count() })
                .OrderByDescending(d => d.Count).ToListAsync(ct),
            async db => byType = await Filtered(db).GroupBy(c => c.CaseType)
                .Select(g => new CaseTypeCaseCount { CaseType = g.Key, Count = g.Count() }).OrderByDescending(t => t.Count).ToListAsync(ct),
            // Open cases are judged live by the SLA clock (there are only as many as the workload). Finished cases already carry
            // their outcome, so they are COUNTED in the database instead of being loaded and re-evaluated one by one.
            async db => inputs = (await Filtered(db).Where(c => !finished.Contains(c.Status)).Select(c => new
            {
                c.Id, c.Status, c.Severity,
                Inputs = new SlaInputs
                {
                    Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                    FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                    ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                }
            }).ToListAsync(ct)).Select(x => (x.Id, x.Status, x.Severity, x.Inputs)).ToList(),
            async db => finishedBreached = await Filtered(db).CountAsync(c => finished.Contains(c.Status) && c.SlaOutcome == "Breached", ct),
            // Resolutions per 15-minute UTC bucket (every real time-zone offset is a multiple of 15 minutes, so a bucket never
            // straddles a local midnight); a few thousand rows at most. They are placed on local days/months below.
            async db => buckets = (await Filtered(db).Where(c => c.ResolvedAt != null && c.ResolvedAt >= horizon)
                .GroupBy(c => new { c.ResolvedAt!.Value.Year, c.ResolvedAt!.Value.Month, c.ResolvedAt!.Value.Day, c.ResolvedAt!.Value.Hour, Quarter = c.ResolvedAt!.Value.Minute / 15 })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.Quarter, N = g.Count() })
                .ToListAsync(ct)).Select(b => (b.Year, b.Month, b.Day, b.Hour, b.Quarter, b.N)).ToList(),
            async db => recentIds = await Filtered(db).OrderByDescending(c => c.CreatedAt).Take(15).Select(c => c.Id).ToListAsync(ct),
            // Activity follows the same filters as everything else, and only reports events that belong to a case.
            async db =>
            {
                var caseIds = Filtered(db).Select(c => c.Id);   // a subquery, not a call inside the expression
                activities = await db.CaseEvents.AsNoTracking()
                .Where(e => e.CaseId != null && caseIds.Contains(e.CaseId.Value))
                .OrderByDescending(e => e.CreatedAt).Take(15)
                .Select(e => new RecentActivityItem
                {
                    Id = "act-" + e.Id,
                    Type = e.EventType.ToString().ToLower(),
                    Title = "Case " + e.Case!.CaseNumber + " " + e.EventType,
                    Sub = e.Message + " · " + (e.User != null ? e.User.Name : "System"),
                    CaseId = e.CaseId!.Value,
                    Timestamp = e.CreatedAt
                })
                .ToListAsync(ct);
            });

        var result = new DashboardSummaryDto
        {
            TotalCases = total,
            OpenCases = byStatus.GetValueOrDefault(CaseStatus.Open),
            InProgressCases = byStatus.GetValueOrDefault(CaseStatus.InProgress),
            WaitingOnCustomerCases = byStatus.GetValueOrDefault(CaseStatus.WaitingOnCustomer),
            EscalatedCases = byStatus.GetValueOrDefault(CaseStatus.Escalated),
            ResolvedCases = byStatus.GetValueOrDefault(CaseStatus.Resolved),
            UnassignedCases = unassigned,
            CasesByTeam = byTeam,
            CasesByType = byType,
            RecentActivities = activities,
        };

        // Priorities in the configured order (every configured one listed, even with 0), then any removed priority that still has cases.
        var counts = bySeverity.ToDictionary(c => c.Severity.ToLowerInvariant(), c => c.Count);
        foreach (var p in order)
            result.CasesBySeverity.Add(new SeverityCaseCount { Severity = p.Priority, DisplayOrder = p.DisplayOrder, Count = counts.GetValueOrDefault(p.Priority.ToLowerInvariant()) });
        var known = order.Select(p => p.Priority.ToLowerInvariant()).ToHashSet();
        var next = (order.Count == 0 ? 0 : order.Max(p => p.DisplayOrder)) + 1;
        foreach (var c in bySeverity.Where(c => !known.Contains(c.Severity.ToLowerInvariant())).OrderBy(c => c.Severity))
            result.CasesBySeverity.Add(new SeverityCaseCount { Severity = c.Severity, Count = c.Count, DisplayOrder = next++ });

        // ---- SLA ---------------------------------------------------------------------------------------------
        var verdicts = inputs.ToDictionary(c => c.Id, c => clock.Evaluate(c.Inputs, now));
        var breached = verdicts.Values.Count(v => v.Health == SlaHealth.Breached) + finishedBreached;
        var atRisk = verdicts.Values.Count(v => v.Health == SlaHealth.Approaching);
        result.SlaBreachedCases = breached;
        result.SlaAtRiskCases = atRisk;
        result.SlaHealthyCases = Math.Max(0, total - breached - atRisk);
        result.SlaAdherencePercent = total > 0 ? Math.Round((decimal)(total - breached) / total * 100, 1) : 100;

        // ---- resolved over time, in the business time zone ----------------------------------------------------
        var perDay = new Dictionary<DateTime, int>();
        var perMonth = new Dictionary<DateTime, int>();
        foreach (var b in buckets)
        {
            var utc = new DateTime(b.Year, b.Month, b.Day, b.Hour, b.Quarter * 15, 0, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            perDay[local.Date] = perDay.GetValueOrDefault(local.Date) + b.N;
            var month = new DateTime(local.Year, local.Month, 1);
            perMonth[month] = perMonth.GetValueOrDefault(month) + b.N;
        }

        int Days(DateTime from, DateTime toExclusive) => perDay.Where(kv => kv.Key >= from && kv.Key < toExclusive).Sum(kv => kv.Value);

        for (var i = 6; i >= 0; i--)
        {
            var day = localToday.AddDays(-i);
            result.ResolvedDaily.Add(new ResolvedTimePoint { Label = day.ToString("MMM d"), Count = perDay.GetValueOrDefault(day) });
        }
        for (var i = 3; i >= 0; i--)
        {
            var from = thisMonday.AddDays(-i * 7);
            result.ResolvedWeekly.Add(new ResolvedTimePoint { Label = $"Wk {4 - i}", Count = Days(from, from.AddDays(7)) });
        }
        for (var i = 5; i >= 0; i--)
        {
            var from = new DateTime(localToday.Year, localToday.Month, 1).AddMonths(-i);
            result.ResolvedMonthly.Add(new ResolvedTimePoint { Label = from.ToString("MMM"), Count = perMonth.GetValueOrDefault(from) });
        }

        // ---- lists ------------------------------------------------------------------------------------------------
        var priorityRank = order.Select((p, i) => (p.Priority.ToLowerInvariant(), i)).ToDictionary(x => x.Item1, x => x.i);
        int Rank(string severity) => priorityRank.GetValueOrDefault(severity.ToLowerInvariant(), int.MaxValue);

        // Needs attention: open cases, worst SLA first (breached, then approaching by how much of the target is used), then priority.
        var attentionIds = inputs
            .OrderByDescending(c => verdicts[c.Id].Health == SlaHealth.Breached)
            .ThenByDescending(c => verdicts[c.Id].Health == SlaHealth.Approaching)
            .ThenByDescending(c => verdicts[c.Id].External.ConsumedPercent)
            .ThenBy(c => Rank(c.Severity))
            .Take(20).Select(c => c.Id).ToList();

        // ---- round 2: the listed rows, and SLA inputs for listed cases that were not in the open set ------------------
        var wanted = attentionIds.Concat(recentIds).Distinct().ToList();
        var missing = wanted.Where(w => !verdicts.ContainsKey(w)).ToList();
        Dictionary<Guid, AttentionCaseSummary> rows = new();
        Dictionary<Guid, SlaInputs> extra = new();

        await ReadAllAsync(ct,
            async db => rows = await db.Cases.AsNoTracking().Where(c => wanted.Contains(c.Id)).Select(c => new AttentionCaseSummary
            {
                Id = c.Id, CaseNumber = c.CaseNumber, Title = c.Title, Status = c.Status.ToString(), Severity = c.Severity,
                OwnerName = c.Owner != null ? c.Owner.Name : string.Empty, OwnerId = c.OwnerId,
                TeamName = c.Team != null ? c.Team.Name : string.Empty,
                SlaStartTime = c.SlaStartTime, SlaTargetHours = c.SlaTargetHours, SlaBreachedAt = c.SlaBreachedAt, SlaPausedAt = c.SlaPausedAt,
                SlaTotalPausedMinutes = c.SlaTotalPausedMinutes, CustomerName = c.Customer != null ? c.Customer.FullName : string.Empty,
                CustomerSource = c.CustomerSource, CustomerType = c.CustomerType, CreatedAt = c.CreatedAt
            }).ToDictionaryAsync(c => c.Id, ct),
            async db => extra = missing.Count == 0 ? new() : (await db.Cases.AsNoTracking().Where(c => missing.Contains(c.Id)).Select(c => new
            {
                c.Id,
                Inputs = new SlaInputs
                {
                    Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                    FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                    ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                }
            }).ToListAsync(ct)).ToDictionary(c => c.Id, c => c.Inputs));

        var holiday = clock.Calendar.HolidayOn(now);
        AttentionCaseSummary Shape(Guid id)
        {
            var row = rows[id];
            // A finished case (recent list) or one that appeared between the two rounds: judge it directly from its row.
            var verdict = verdicts.TryGetValue(id, out var v) ? v : clock.Evaluate(extra[id], now);
            row.Sla = CaseSlaDto.From(verdict);
            row.IsHolidayToday = holiday != null;
            row.HolidayName = holiday;
            return row;
        }
        result.AttentionCases = attentionIds.Where(rows.ContainsKey).Select(Shape).ToList();
        result.RecentCases = recentIds.Where(id => rows.ContainsKey(id) && (verdicts.ContainsKey(id) || extra.ContainsKey(id)))
            .Select(Shape).Select(Clone).ToList();
        if (_piiMasking != null)
        {
            // The dashboard lists customers' names: masked like everywhere else.
            await _piiMasking.MaskCasesAsync(result.AttentionCases, ct);
            await _piiMasking.MaskCasesAsync(result.RecentCases, ct);
        }

        return result;
    }

    /// <summary>Independent reads side by side, each on its own pooled context (see <see cref="ParallelReads"/>).</summary>
    private Task ReadAllAsync(CancellationToken ct, params Func<AppDbContext, Task>[] reads) => ParallelReads.RunAsync(_contexts, _context, ct, reads);

    private static AttentionCaseSummary Clone(AttentionCaseSummary c) => new()
    {
        Id = c.Id, CaseNumber = c.CaseNumber, Title = c.Title, Status = c.Status, Severity = c.Severity, OwnerName = c.OwnerName, OwnerId = c.OwnerId,
        TeamName = c.TeamName, SlaStartTime = c.SlaStartTime, SlaTargetHours = c.SlaTargetHours, SlaBreachedAt = c.SlaBreachedAt,
        SlaPausedAt = c.SlaPausedAt, SlaTotalPausedMinutes = c.SlaTotalPausedMinutes, CustomerName = c.CustomerName,
        CustomerSource = c.CustomerSource, CustomerType = c.CustomerType, CreatedAt = c.CreatedAt,
        Sla = c.Sla, IsHolidayToday = c.IsHolidayToday, HolidayName = c.HolidayName
    };

    // ------------------------------------------------------------------------------------------------ export

    /// <summary>One exported case. Public and with a settable <see cref="CustomerName"/> so the masking service treats it as a case.</summary>
    public sealed class ReportRow
    {
        public Guid Id { get; init; }
        public string CaseNumber { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string CaseType { get; init; } = string.Empty;
        public string? Subcategory { get; init; }
        public CaseStatus Status { get; init; }
        public string Severity { get; init; } = string.Empty;
        public string TeamName { get; init; } = string.Empty;
        public string OwnerName { get; init; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerSource { get; init; } = CustomerSources.Local;
        public string? CustomerType { get; init; }
        public string? Channel { get; init; }
        public int EscalationLevel { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ResolvedAt { get; init; }
        public string? SlaOutcome { get; init; }
        public SlaInputs Inputs { get; init; } = null!;
    }

    private static readonly string[] ExportHeader =
    {
        "Case Number", "Title", "Case Type", "Sub-category", "Status", "Priority", "Team", "Owner",
        "Customer", "Customer Source", "Channel", "SLA Status", "Escalation Level", "Created", "Resolved",
    };

    public async Task<ReportExport> ExportAsync(DashboardQuery q, string? slaStatus, string? search, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var clock = await _slaClock.GetAsync(ct);
        var zone = clock.Calendar.TimeZone;
        var (start, end) = DateRange(q.DateRange, q.CustomStartDate, q.CustomEndDate, now, zone);

        var query = ApplyFilters(_context, q, ParseStatus(q.Status), start, end);
        if (!string.IsNullOrWhiteSpace(search))
        {
            // The same fields the Reports page's search box looks at.
            var pattern = Repositories.SqlSearchPattern.Contains(search.Trim());
            query = query.Where(c => EF.Functions.ILike(c.CaseNumber, pattern) || EF.Functions.ILike(c.Title, pattern)
                || (c.Customer != null && EF.Functions.ILike(c.Customer.FullName, pattern))
                || (c.Owner != null && EF.Functions.ILike(c.Owner.Name, pattern)));
        }

        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .Take(MaxExportRows + 1)
            .Select(c => new ReportRow
            {
                Id = c.Id, CaseNumber = c.CaseNumber, Title = c.Title, CaseType = c.CaseType, Subcategory = c.Subcategory,
                Status = c.Status, Severity = c.Severity,
                TeamName = c.Team != null ? c.Team.Name : string.Empty,
                OwnerName = c.Owner != null ? c.Owner.Name : string.Empty,
                CustomerName = c.Customer != null ? c.Customer.FullName : string.Empty,
                CustomerSource = c.CustomerSource, CustomerType = c.CustomerType,
                Channel = c.SourceChannel ?? c.CommunicationChannel,
                EscalationLevel = c.EscalationLevel, CreatedAt = c.CreatedAt, ResolvedAt = c.ResolvedAt, SlaOutcome = c.SlaOutcome,
                Inputs = new SlaInputs
                {
                    Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                    FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                    ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                },
            })
            .ToListAsync(ct);

        var truncated = rows.Count > MaxExportRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        // A finished case keeps the outcome it was resolved with; an open one is judged now by the one SLA clock.
        var finished = new[] { CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled };
        SlaHealth Health(ReportRow r) => finished.Contains(r.Status)
            ? (r.SlaOutcome == "Breached" ? SlaHealth.Breached : SlaHealth.Met)
            : clock.Evaluate(r.Inputs, now).Health;
        var health = rows.ToDictionary(r => r.Id, Health);

        if (!string.IsNullOrWhiteSpace(slaStatus) && !slaStatus.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            // As the page reads its SLA filter: "healthy" is anything not approaching or breached.
            var wanted = slaStatus.Trim().ToLowerInvariant();
            rows = rows.Where(r => health[r.Id] switch
            {
                SlaHealth.Breached => wanted == "breached",
                SlaHealth.Approaching => wanted == "approaching",
                _ => wanted == "healthy",
            }).ToList();
        }

        // Masked like every other list; then Customer 360 customers are named live (that service masks what it fills).
        if (_piiMasking != null) await _piiMasking.MaskCasesAsync(rows, ct);
        if (_customerNames != null)
            await _customerNames.FillAsync(rows, r => r.Id, r => r.CustomerSource, r => r.CustomerName, (r, n) => r.CustomerName = n, ct);

        string Local(DateTime? utc) => utc.HasValue
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm")
            : string.Empty;
        static string StatusLabel(CaseStatus s) => s switch
        {
            CaseStatus.InProgress => "In Progress",
            CaseStatus.WaitingOnCustomer => "Waiting on Customer",
            _ => s.ToString(),
        };
        static string SlaLabel(SlaHealth h) => h switch
        {
            SlaHealth.Healthy => "Within SLA",
            SlaHealth.Approaching => "Approaching SLA",
            SlaHealth.Breached => "SLA Breached",
            SlaHealth.Paused => "Paused",
            _ => "Met",
        };
        string CustomerLabel(ReportRow r) =>
            !string.IsNullOrWhiteSpace(r.CustomerName) ? r.CustomerName
            : r.CustomerSource == CustomerSources.Customer360 ? $"Customer 360 · {(r.CustomerType == CustomerTypes.NonIndividual ? "Non-Individual" : "Individual")}"
            : string.Empty;

        var csv = ReportCsv.Build(ExportHeader, rows.Select(r => (IReadOnlyList<string?>)new[]
        {
            r.CaseNumber, r.Title, r.CaseType, r.Subcategory, StatusLabel(r.Status), r.Severity, r.TeamName, r.OwnerName,
            CustomerLabel(r), r.CustomerSource == CustomerSources.Customer360 ? "Customer 360" : "Case Management", r.Channel,
            SlaLabel(health[r.Id]), r.EscalationLevel.ToString(), Local(r.CreatedAt), Local(r.ResolvedAt),
        }));

        return new ReportExport(csv, rows.Count, truncated, TimeZoneInfo.ConvertTimeFromUtc(now, zone).Date);
    }

    // ------------------------------------------------------------------------------------------------ date ranges

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);

    /// <summary>The UTC window [start, end) a named range covers, with days counted in the business time zone.</summary>
    public static (DateTime? Start, DateTime? End) DateRange(string? range, string? customStart, string? customEnd, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Equals("all", StringComparison.OrdinalIgnoreCase)) return (null, null);

        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var monthStart = new DateTime(today.Year, today.Month, 1);

        DateTime? from, to;
        switch (range.ToLowerInvariant())
        {
            case "today": from = today; to = today.AddDays(1); break;
            case "this_week": from = monday; to = monday.AddDays(7); break;
            case "last_week": from = monday.AddDays(-7); to = monday; break;
            case "this_month": from = monthStart; to = monthStart.AddMonths(1); break;
            case "last_month": from = monthStart.AddMonths(-1); to = monthStart; break;
            case "this_quarter":
                from = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1); to = from.Value.AddMonths(3); break;
            case "this_year": from = new DateTime(today.Year, 1, 1); to = from.Value.AddYears(1); break;
            case "custom":
                from = DateTime.TryParse(customStart, out var s) ? s.Date : null;
                to = DateTime.TryParse(customEnd, out var e) ? e.Date.AddDays(1) : null;   // the end date is inclusive
                break;
            default:
                throw new ArgumentException($"'{range}' is not a date range. Supported: {string.Join(", ", SupportedDateRanges)}.");
        }
        return (from.HasValue ? ToUtc(from.Value, zone) : null, to.HasValue ? ToUtc(to.Value, zone) : null);
    }
}
