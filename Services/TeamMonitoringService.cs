namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// The Team Monitor, computed from live data only. Who counts as an agent comes from team membership; whether a case is
/// breached is the SLA clock's verdict (the same one the dashboard and the worker use); and where there is no data the answer
/// is "none", not a default.
/// </summary>
/// <remarks>
/// <para>Scope: someone who sees every team (cases:ViewAll, administrators) monitors every active team; everyone else monitors
/// the teams they lead or belong to. The drawer's agent, cases and activity are held to the same scope — and cases to the
/// caller's own case visibility — so the monitor never shows more than the rest of the application would.</para>
/// <para>The independent reads run side by side (<see cref="ParallelReads"/>): the hosted database costs a round trip per query.</para>
/// </remarks>
public class TeamMonitoringService : ITeamMonitoringService
{
    private static readonly int[] PeriodChoices = { 1, 7, 30, 90 };

    private readonly AppDbContext _context;
    private readonly ISlaClockProvider _slaClock;
    private readonly IRoutingEngineService _routing;
    private readonly TeamMonitoringOptions _options;
    private readonly ICaseAccessScope? _access;
    private readonly ICurrentUserAccessor? _currentUser;
    private readonly IDbContextFactory<AppDbContext>? _contexts;

    public TeamMonitoringService(AppDbContext context, ISlaClockProvider slaClock, IRoutingEngineService routing, IOptions<TeamMonitoringOptions> options,
        ICaseAccessScope? access = null, ICurrentUserAccessor? currentUser = null, IDbContextFactory<AppDbContext>? contexts = null)
    {
        _context = context;
        _slaClock = slaClock;
        _routing = routing;
        _options = options.Value;
        _access = access;
        _currentUser = currentUser;
        _contexts = contexts;
    }

    private static readonly CaseStatus[] Finished = { CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled };

    private sealed record OpenCase(Guid Id, string CaseNumber, Guid TeamId, string? Channel, Guid OwnerId, DateTime CreatedAt,
        DateTime WaitingSince, CaseStatus Status, SlaInputs Inputs);   // WaitingSince = when its SLA clock started

    private sealed record MemberRow(Guid TeamId, string TeamName, bool IsTeamLead, bool IsAssignable, Guid UserId, string Name, string Email,
        string Role, UserStatus Status, DateTime? StatusUpdatedAt);

    private sealed record ResolvedRow(Guid OwnerId, DateTime ResolvedAt, double Minutes);

    // ------------------------------------------------------------------------------------------------ scope

    /// <summary>True when the caller monitors every team (no caller = background work or a unit test, unrestricted).</summary>
    private bool SeesAll => _access?.SeesAll ?? true;
    private Guid? Me => _currentUser?.LocalUserId;

    /// <summary>The teams the caller may monitor: null = every team.</summary>
    private async Task<HashSet<Guid>?> MonitoredTeamIdsAsync(CancellationToken ct)
    {
        if (SeesAll || Me == null) return null;
        var me = Me.Value;
        return (await _context.Departments.AsNoTracking()
            .Where(d => d.OwnerId == me || d.Members.Any(m => m.UserId == me && m.IsActive))
            .Select(d => d.Id).ToListAsync(ct)).ToHashSet();
    }

    public async Task EnsureCanMonitorAsync(Guid agentId, CancellationToken ct = default)
    {
        var scope = await MonitoredTeamIdsAsync(ct);
        var visible = scope == null
            ? await _context.Users.AsNoTracking().AnyAsync(u => u.Id == agentId, ct)
            : await _context.Departments.AsNoTracking().AnyAsync(d => scope.Contains(d.Id)
                && (d.OwnerId == agentId || d.Members.Any(m => m.UserId == agentId && m.IsActive)), ct);
        // The same answer as for someone who does not exist: an id cannot be used to learn about people outside one's teams.
        if (!visible) throw new KeyNotFoundException("Agent not found.");
    }

    // ------------------------------------------------------------------------------------------------ overview

    public async Task<TeamMonitoringOverviewDto> GetOverviewAsync(MonitoringQuery query, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var periodDays = PeriodChoices.Contains(query.PeriodDays) ? query.PeriodDays : 7;
        var scope = await MonitoredTeamIdsAsync(ct);

        // ---- the teams in scope, and what was asked for (one team, or all of them) --------------------------------------------
        var teams = await _context.Departments.AsNoTracking()
            .Where(d => d.IsActive && (scope == null || scope.Contains(d.Id)))
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.OwnerId })
            .ToListAsync(ct);

        string? teamName = null;
        var selected = teams;
        if (query.TeamId.HasValue)
        {
            // An inactive team can still be looked at directly by someone who may see it.
            var team = teams.FirstOrDefault(t => t.Id == query.TeamId);
            if (team == null)
            {
                var inactive = await _context.Departments.AsNoTracking()
                    .Where(d => d.Id == query.TeamId && (scope == null || scope.Contains(d.Id)))
                    .Select(d => new { d.Id, d.Name, d.OwnerId })
                    .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Team not found.");
                team = inactive;
            }
            teamName = team.Name;
            selected = new[] { team }.ToList();
        }
        var teamIds = selected.Select(t => t.Id).ToList();
        var leadOf = selected.Where(t => t.OwnerId.HasValue).Select(t => (t.Id, Lead: t.OwnerId!.Value)).ToHashSet();

        var clock = await _slaClock.GetAsync(ct);
        var (todayStart, tomorrowStart) = LocalDayBounds(clock.Calendar, now, 0);
        var (yesterdayStart, _) = LocalDayBounds(clock.Calendar, now, -1);
        var (periodStart, _) = LocalDayBounds(clock.Calendar, now, -(periodDays - 1));
        var globalCapacity = (await _routing.GetAssignmentConfigAsync(null, ct)).MaxConcurrentCapacity;

        List<MemberRow> memberRows = new();
        Dictionary<Guid, int> teamCapacity = new();
        List<OpenCase> open = new();
        List<ResolvedRow> resolved = new();
        List<double> yesterdayMinutes = new();
        Dictionary<Guid, DateTime> lastActivity = new();

        await ParallelReads.RunAsync(_contexts, _context, ct,
            async db => memberRows = await db.TeamMembers.AsNoTracking()
                .Where(m => m.IsActive && m.User.IsActive && teamIds.Contains(m.DepartmentId))
                .Select(m => new MemberRow(m.DepartmentId, m.Department.Name, m.Department.OwnerId == m.UserId, m.IsAssignable, m.UserId, m.User.Name, m.User.Email,
                    m.User.Role, m.User.Status, m.User.StatusUpdatedAt))
                .ToListAsync(ct),
            async db => teamCapacity = await db.AssignmentConfigurations.AsNoTracking()
                .Where(c => c.DepartmentId != null && c.IsActive).ToDictionaryAsync(c => c.DepartmentId!.Value, c => c.MaxConcurrentCapacity, ct),
            async db => open = (await db.Cases.AsNoTracking()
                    .Where(c => teamIds.Contains(c.DepartmentId) && !Finished.Contains(c.Status))
                    .Select(c => new
                    {
                        c.Id, c.CaseNumber, c.DepartmentId, c.SourceChannel, c.CommunicationChannel, c.OwnerId, c.CreatedAt, WaitingSince = c.SlaStartTime, c.Status,
                        Inputs = new SlaInputs
                        {
                            Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                            FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                            ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                        }
                    }).ToListAsync(ct))
                .Select(c => new OpenCase(c.Id, c.CaseNumber, c.DepartmentId, string.IsNullOrWhiteSpace(c.SourceChannel) ? c.CommunicationChannel : c.SourceChannel,
                    c.OwnerId, c.CreatedAt, c.WaitingSince, c.Status, c.Inputs))
                .ToList(),
            async db => resolved = (await db.Cases.AsNoTracking()
                    .Where(c => teamIds.Contains(c.DepartmentId) && c.ResolvedAt != null && c.ResolvedAt >= periodStart && c.ResolvedAt < tomorrowStart)
                    .Select(c => new { c.OwnerId, ResolvedAt = c.ResolvedAt!.Value, c.CreatedAt }).ToListAsync(ct))
                .Select(r => new ResolvedRow(r.OwnerId, r.ResolvedAt, (r.ResolvedAt - r.CreatedAt).TotalMinutes)).ToList(),
            async db => yesterdayMinutes = (await db.Cases.AsNoTracking()
                    .Where(c => teamIds.Contains(c.DepartmentId) && c.ResolvedAt >= yesterdayStart && c.ResolvedAt < todayStart)
                    .Select(c => new { ResolvedAt = c.ResolvedAt!.Value, c.CreatedAt }).ToListAsync(ct))
                .Select(r => (r.ResolvedAt - r.CreatedAt).TotalMinutes).ToList(),
            async db => lastActivity = await db.CaseEvents.AsNoTracking()
                .Where(e => db.TeamMembers.Any(m => m.UserId == e.UserId && m.IsActive && teamIds.Contains(m.DepartmentId)))
                .Where(AutomaticEvents.IsPersonal)
                .Where(e => e.UserId != null)
                .GroupBy(e => e.UserId!.Value).Select(g => new { g.Key, At = g.Max(e => e.CreatedAt) })
                .ToDictionaryAsync(x => x.Key, x => x.At, ct));

        var verdicts = open.ToDictionary(c => c.Id, c => clock.Evaluate(c.Inputs, now));
        var resolvedToday = resolved.Where(r => r.ResolvedAt >= todayStart).ToList();

        // ---- agents ----------------------------------------------------------------------------------------------------------
        var agents = memberRows.GroupBy(m => m.UserId).Select(g => new
        {
            Row = g.First(),
            Teams = g.OrderBy(x => x.TeamName).Select(x => new AgentTeamDto { Id = x.TeamId, Name = x.TeamName, IsLead = x.IsTeamLead || leadOf.Contains((x.TeamId, x.UserId)), IsAssignable = x.IsAssignable }).ToList(),
            IsAssignable = g.Any(x => x.IsAssignable),
            Capacity = g.Max(x => teamCapacity.GetValueOrDefault(x.TeamId, globalCapacity)),
        }).ToList();

        var openByOwner = open.GroupBy(c => c.OwnerId).ToDictionary(g => g.Key, g => g.ToList());
        var resolvedByOwner = resolved.GroupBy(r => r.OwnerId).ToDictionary(g => g.Key, g => g.ToList());

        var agentItems = agents.Select(a =>
        {
            var mine = openByOwner.GetValueOrDefault(a.Row.UserId) ?? new List<OpenCase>();
            var done = resolvedByOwner.GetValueOrDefault(a.Row.UserId) ?? new List<ResolvedRow>();
            return new AgentStatusItemDto
            {
                UserId = a.Row.UserId,
                Name = a.Row.Name,
                Email = a.Row.Email,
                Role = string.IsNullOrWhiteSpace(a.Row.Role) ? "Service Agent" : a.Row.Role,
                TeamName = string.Join(", ", a.Teams.Select(t => t.Name)),
                Teams = a.Teams,
                State = StateOf(a.Row.Status),
                StatusSetAt = a.Row.StatusUpdatedAt,
                IsAssignable = a.IsAssignable,
                OpenCasesCount = mine.Count,
                BreachedCasesCount = mine.Count(c => verdicts[c.Id].Health == SlaHealth.Breached),
                Capacity = a.Capacity,
                HandledTodayCount = done.Count(r => r.ResolvedAt >= todayStart),
                ResolvedInPeriodCount = done.Count,
                LastActivityAt = lastActivity.TryGetValue(a.Row.UserId, out var at) ? at : null,
            };
        }).OrderBy(a => a.Name).ToList();

        var online = agents.Where(a => a.Row.Status is UserStatus.Available or UserStatus.Busy).ToList();
        var onlineCapacity = online.Sum(a => a.Capacity);
        var onlineOpen = online.Sum(a => (openByOwner.GetValueOrDefault(a.Row.UserId)?.Count) ?? 0);

        // The longest wait is the oldest case still waiting for its first answer.
        var waiting = open.Where(c => c.Status == CaseStatus.Open && c.Inputs.FirstResponseActualAt == null).OrderBy(c => c.WaitingSince).ToList();
        var longest = waiting.FirstOrDefault();

        var summary = new TeamMonitoringSummaryDto
        {
            TeamsCount = selected.Count,
            OnlineAgentsCount = online.Count,
            TotalAgentsCount = agents.Count,
            AgentsBreakCount = agents.Count(a => a.Row.Status == UserStatus.Away),
            AgentsStatusNotSetCount = agents.Count(a => a.Row.StatusUpdatedAt == null),
            LongestQueueWaitMinutes = longest == null ? null : (int)Math.Max(0, (now - longest.WaitingSince).TotalMinutes),
            LongestQueueCaseNumber = longest?.CaseNumber,
            LongestQueueChannel = longest?.Channel,
            LongestQueueWaitOverTarget = longest != null && verdicts[longest.Id].FirstResponse.IsBreached,
            ResolvedTodayCount = resolvedToday.Count,
            AvgResolutionMinutes = resolvedToday.Count > 0 ? Math.Round(resolvedToday.Average(r => r.Minutes), 1) : null,
            AvgResolutionYesterdayMinutes = yesterdayMinutes.Count > 0 ? Math.Round(yesterdayMinutes.Average(), 1) : null,
            OccupancyPercent = onlineCapacity > 0 ? Math.Round((decimal)onlineOpen / onlineCapacity * 100m, 0) : null,
            OccupancyTargetMin = _options.OccupancyTargetMin,
            OccupancyTargetMax = _options.OccupancyTargetMax,
            OpenCasesCount = open.Count,
        };

        // ---- agent performance: open now vs resolved in the period (people with neither add nothing to a comparison) ------
        var performance = agentItems
            .Where(a => a.OpenCasesCount > 0 || a.ResolvedInPeriodCount > 0)
            .Select(a =>
            {
                var done = resolvedByOwner.GetValueOrDefault(a.UserId);
                return new AgentPerformanceItemDto
                {
                    UserId = a.UserId, Name = a.Name, OpenCount = a.OpenCasesCount, ResolvedCount = a.ResolvedInPeriodCount,
                    AvgResolutionMinutes = done is { Count: > 0 } ? Math.Round(done.Average(r => r.Minutes), 1) : null,
                };
            })
            .OrderByDescending(p => p.OpenCount + p.ResolvedCount).ThenBy(p => p.Name)
            .ToList();

        // ---- queue health: the same open cases by queue, by team and by source channel -----------------------------------------
        var queueOf = open.ToDictionary(c => c.Id, QueueOf);
        var byStatus = Queues.Select(q => new QueueStatusDto
        {
            Key = q.Key, Label = q.Label,
            Count = open.Count(c => queueOf[c.Id] == q.Key),
            Breached = open.Count(c => queueOf[c.Id] == q.Key && verdicts[c.Id].Health == SlaHealth.Breached),
        }).Where(q => q.Count > 0).ToList();

        QueueGroupDto Group(string key, string name, IEnumerable<OpenCase> cases, bool unlisted = false)
        {
            var row = new QueueGroupDto { Key = key, Name = name, IsUnlisted = unlisted };
            foreach (var c in cases)
            {
                row.Total++;
                if (verdicts[c.Id].Health == SlaHealth.Breached) row.Breached++;
                switch (queueOf[c.Id])
                {
                    case "awaitingFirstResponse": row.AwaitingFirstResponse++; break;
                    case "open": row.Open++; break;
                    case "inProgress": row.InProgress++; break;
                    case "onHold": row.OnHold++; break;
                    case "escalated": row.Escalated++; break;
                    default: row.Other++; break;
                }
            }
            return row;
        }

        var byTeam = selected.Select(t => Group(t.Id.ToString(), t.Name, open.Where(c => c.TeamId == t.Id)))
            .OrderByDescending(r => r.Total).ThenBy(r => r.Name).ToList();

        // Channels are labelled from the Source Channel list. A value no longer in it is still counted, marked as unlisted, so the
        // three views always add up to the same total.
        var channelLabels = await _context.LookupValues.AsNoTracking().Where(v => v.TypeCode == "SOURCE_CHANNEL")
            .Select(v => new { v.Value, v.Label, v.IsActive }).ToListAsync(ct);
        var byChannel = open
            .GroupBy(c => (c.Channel ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                if (g.Key.Length == 0) return Group(string.Empty, "Not recorded", g, unlisted: true);
                var listed = channelLabels.FirstOrDefault(v => string.Equals(v.Value, g.Key, StringComparison.OrdinalIgnoreCase));
                return listed == null
                    ? Group(g.Key, $"{g.Key} (not in the channel list)", g, unlisted: true)
                    : Group(listed.Value, string.IsNullOrWhiteSpace(listed.Label) ? listed.Value : listed.Label + (listed.IsActive ? "" : " (inactive)"), g, unlisted: !listed.IsActive);
            })
            .OrderByDescending(r => r.Total).ThenBy(r => r.Name).ToList();

        return new TeamMonitoringOverviewDto
        {
            TeamId = query.TeamId, TeamName = teamName,
            PeriodDays = periodDays, GeneratedAt = now, SeesAllTeams = scope == null,
            Filters = new MonitoringFiltersDto
            {
                Teams = teams.Select(t => new NamedOptionDto { Id = t.Id, Name = t.Name }).ToList(),
            },
            Summary = summary,
            Agents = agentItems,
            AgentPerformance = performance,
            QueueHealth = new QueueHealthDto { TotalOpen = open.Count, ByStatus = byStatus, ByTeam = byTeam, ByChannel = byChannel },
        };
    }

    public async Task<IReadOnlyList<AgentStatusItemDto>> GetExportAgentsAsync(MonitoringExportQuery query, CancellationToken ct = default)
    {
        var overview = await GetOverviewAsync(new MonitoringQuery(query.TeamId, query.PeriodDays), ct);
        IEnumerable<AgentStatusItemDto> agents = overview.Agents;
        if (query.AgentId.HasValue)
        {
            agents = agents.Where(a => a.UserId == query.AgentId.Value);
            if (!agents.Any()) throw new KeyNotFoundException("Agent not found.");
        }
        if (!string.IsNullOrWhiteSpace(query.State) && !string.Equals(query.State, "all", StringComparison.OrdinalIgnoreCase))
            agents = query.State.Equals("notset", StringComparison.OrdinalIgnoreCase)
                ? agents.Where(a => a.StatusSetAt == null)
                : agents.Where(a => string.Equals(a.State, query.State, StringComparison.OrdinalIgnoreCase));
        return agents.OrderBy(a => a.Name).ToList();
    }

    public async Task<ReportExport> ExportAgentSummaryAsync(MonitoringExportQuery query, CancellationToken ct = default)
    {
        var agents = await GetExportAgentsAsync(query, ct);
        var zone = (await _slaClock.GetAsync(ct)).Calendar.TimeZone;
        string Local(DateTime? utc) => utc is { } u ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;
        string N(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var periodDays = PeriodChoices.Contains(query.PeriodDays) ? query.PeriodDays : 7;

        // The same figures the page shows, nothing derived beyond them. No customer data is in an agent row.
        var header = new[] { "Agent", "Email", "Role", "Teams", "Team lead of", "Status", "Status set at", "Open cases", "Past customer SLA",
            "Capacity", "Handled today", $"Resolved (last {periodDays} {(periodDays == 1 ? "day" : "days")})", "Receives cases automatically", "Last activity" };
        var csv = ReportCsv.Build(header, agents.Select(a => (IReadOnlyList<string?>)new[]
        {
            a.Name, a.Email, a.Role,
            string.Join("; ", a.Teams.Select(t => t.Name)),
            string.Join("; ", a.Teams.Where(t => t.IsLead).Select(t => t.Name)),
            a.StatusSetAt == null ? "Not set by agent" : a.State,
            Local(a.StatusSetAt),
            N(a.OpenCasesCount), N(a.BreachedCasesCount), N(a.Capacity), N(a.HandledTodayCount), N(a.ResolvedInPeriodCount),
            a.IsAssignable ? "Yes" : "No",
            Local(a.LastActivityAt),
        }));
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        return new ReportExport(csv, agents.Count, false, today);
    }

    /// <summary>The queues open work is split into, in the order every queue-health view shows them.</summary>
    private static readonly (string Key, string Label)[] Queues =
    {
        ("awaitingFirstResponse", "Awaiting first response"), ("open", "Open"), ("inProgress", "In progress"),
        ("onHold", "On hold"), ("escalated", "Escalated"), ("other", "Other"),
    };

    private static string QueueOf(OpenCase c) => c.Status switch
    {
        CaseStatus.Open when c.Inputs.FirstResponseActualAt == null => "awaitingFirstResponse",
        CaseStatus.Open => "open",
        CaseStatus.InProgress => "inProgress",
        CaseStatus.WaitingOnCustomer or CaseStatus.Pending => "onHold",
        CaseStatus.Escalated => "escalated",
        _ => "other",
    };

    private static string StateOf(UserStatus status) => status switch
    {
        UserStatus.Available => "Available", UserStatus.Busy => "On interaction", UserStatus.Away => "Break", _ => "Offline",
    };

    /// <summary>The UTC bounds of a calendar day in the business time zone (offset by whole days from today).</summary>
    private static (DateTime Start, DateTime End) LocalDayBounds(BusinessCalendar calendar, DateTime nowUtc, int dayOffset)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, calendar.TimeZone).Date.AddDays(dayOffset);
        var start = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), calendar.TimeZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.AddDays(1), DateTimeKind.Unspecified), calendar.TimeZone);
        return (start, end);
    }

    // ------------------------------------------------------------------------------------------------ agent drawer

    /// <summary>The caller's visible cases (all of them for background work).</summary>
    private IQueryable<Case> Visible(IQueryable<Case> cases, AppDbContext db) => _access?.Apply(cases, db) ?? cases;

    public async Task<AgentDetailDto> GetAgentAsync(Guid agentId, int periodDays, CancellationToken ct = default)
    {
        await EnsureCanMonitorAsync(agentId, ct);
        var now = DateTime.UtcNow;
        periodDays = PeriodChoices.Contains(periodDays) ? periodDays : 30;
        var scope = await MonitoredTeamIdsAsync(ct);
        var clock = await _slaClock.GetAsync(ct);
        var (periodStart, _) = LocalDayBounds(clock.Calendar, now, -(periodDays - 1));
        var globalCapacity = (await _routing.GetAssignmentConfigAsync(null, ct)).MaxConcurrentCapacity;

        User user = null!;
        List<AgentTeamDto> teams = new();
        List<(Guid TeamId, bool Assignable)> memberships = new();
        Dictionary<Guid, int> teamCapacity = new();
        List<SlaInputs> openInputs = new();
        List<double> resolvedMinutes = new();
        DateTime? lastActivity = null;

        await ParallelReads.RunAsync(_contexts, _context, ct,
            async db => user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == agentId, ct),
            async db =>
            {
                var rows = await db.Departments.AsNoTracking()
                    .Where(d => (scope == null || scope.Contains(d.Id)) && (d.OwnerId == agentId || d.Members.Any(m => m.UserId == agentId && m.IsActive)))
                    .OrderBy(d => d.Name)
                    .Select(d => new { d.Id, d.Name, IsLead = d.OwnerId == agentId, Assignable = d.Members.Any(m => m.UserId == agentId && m.IsActive && m.IsAssignable), IsMember = d.Members.Any(m => m.UserId == agentId && m.IsActive) })
                    .ToListAsync(ct);
                teams = rows.Select(r => new AgentTeamDto { Id = r.Id, Name = r.Name, IsLead = r.IsLead, IsAssignable = r.Assignable }).ToList();
                memberships = rows.Where(r => r.IsMember).Select(r => (r.Id, r.Assignable)).ToList();
            },
            async db => teamCapacity = await db.AssignmentConfigurations.AsNoTracking()
                .Where(c => c.DepartmentId != null && c.IsActive).ToDictionaryAsync(c => c.DepartmentId!.Value, c => c.MaxConcurrentCapacity, ct),
            async db => openInputs = await Visible(db.Cases.AsNoTracking(), db)
                .Where(c => c.OwnerId == agentId && !Finished.Contains(c.Status))
                .Select(c => new SlaInputs
                {
                    Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                    FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                    ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                }).ToListAsync(ct),
            async db => resolvedMinutes = (await Visible(db.Cases.AsNoTracking(), db)
                    .Where(c => c.OwnerId == agentId && c.ResolvedAt != null && c.ResolvedAt >= periodStart)
                    .Select(c => new { ResolvedAt = c.ResolvedAt!.Value, c.CreatedAt }).ToListAsync(ct))
                .Select(r => (r.ResolvedAt - r.CreatedAt).TotalMinutes).ToList(),
            async db => lastActivity = await db.CaseEvents.AsNoTracking().Where(e => e.UserId == agentId).Where(AutomaticEvents.IsPersonal).MaxAsync(e => (DateTime?)e.CreatedAt, ct));

        return new AgentDetailDto
        {
            UserId = user.Id,
            Name = user.Name,
            Email = user.Email,
            Role = string.IsNullOrWhiteSpace(user.Role) ? "Service Agent" : user.Role,
            IsActive = user.IsActive,
            State = StateOf(user.Status),
            StatusSetAt = user.StatusUpdatedAt,
            Teams = teams,
            OpenCasesCount = openInputs.Count,
            BreachedCasesCount = openInputs.Count(i => clock.Evaluate(i, now).Health == SlaHealth.Breached),
            Capacity = memberships.Count == 0 ? globalCapacity : memberships.Max(m => teamCapacity.GetValueOrDefault(m.TeamId, globalCapacity)),
            PeriodDays = periodDays,
            ResolvedInPeriodCount = resolvedMinutes.Count,
            AvgResolutionMinutes = resolvedMinutes.Count > 0 ? Math.Round(resolvedMinutes.Average(), 1) : null,
            LastActivityAt = lastActivity,
        };
    }

    public async Task<PagedResponseDto<AgentCaseItemDto>> GetAgentCasesAsync(Guid agentId, string scope, int page, int pageSize, CancellationToken ct = default)
    {
        await EnsureCanMonitorAsync(agentId, ct);
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 50);
        var resolvedOnly = string.Equals(scope, "resolved", StringComparison.OrdinalIgnoreCase);

        var query = Visible(_context.Cases.AsNoTracking(), _context).Where(c => c.OwnerId == agentId);
        query = resolvedOnly ? query.Where(c => c.ResolvedAt != null) : query.Where(c => !Finished.Contains(c.Status));
        var total = await query.CountAsync(ct);

        var ordered = resolvedOnly ? query.OrderByDescending(c => c.ResolvedAt) : query.OrderBy(c => c.CreatedAt);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new
            {
                c.Id, c.CaseNumber, c.Title, c.Status, c.Severity, TeamName = c.Department.Name, c.CreatedAt, c.ResolvedAt, c.SlaOutcome,
                Inputs = new SlaInputs
                {
                    Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                    FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                    ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
                },
            }).ToListAsync(ct);

        var now = DateTime.UtcNow;
        var clock = await _slaClock.GetAsync(ct);
        var items = rows.Select(r =>
        {
            // A finished case keeps the outcome it was resolved with; an open one is judged now by the one SLA clock.
            var finished = Finished.Contains(r.Status);
            var verdict = finished ? null : clock.Evaluate(r.Inputs, now);
            return new AgentCaseItemDto
            {
                Id = r.Id, CaseNumber = r.CaseNumber, Title = r.Title, Status = r.Status.ToString(), Severity = r.Severity, TeamName = r.TeamName,
                CreatedAt = r.CreatedAt, ResolvedAt = r.ResolvedAt,
                SlaHealth = finished ? (r.SlaOutcome == "Breached" ? nameof(SlaHealth.Breached) : nameof(SlaHealth.Met)) : verdict!.Health.ToString(),
                SlaDueAt = verdict?.External.DueAt,
            };
        }).ToList();

        return new PagedResponseDto<AgentCaseItemDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    // ------------------------------------------------------------------------------------------------ nudge

    /// <summary>
    /// Sends the agent a "please check your queue" notification (their bell shows it on its next poll) and records it in the
    /// audit trail, together or not at all. Only an agent the caller monitors can be nudged, and an agent nudged moments ago is
    /// not nudged again: one reminder, not a stream of them.
    /// </summary>
    public async Task<NudgeResultDto> NudgeAgentAsync(Guid agentId, string? reason, Guid supervisorUserId, CancellationToken ct = default)
    {
        await EnsureCanMonitorAsync(agentId, ct);
        var agent = await _context.Users.FindAsync(new object[] { agentId }, ct) ?? throw new KeyNotFoundException("Agent not found.");
        if (!agent.IsActive) throw new InvalidOperationException($"{agent.Name} is deactivated.");
        if (agentId == supervisorUserId) throw new InvalidOperationException("You cannot nudge yourself.");

        var now = DateTime.UtcNow;
        var cooldown = Math.Max(0, _options.NudgeCooldownSeconds);
        var last = await _context.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == agentId && n.Type == "Nudge")
            .MaxAsync(n => (DateTime?)n.CreatedAt, ct);
        if (last.HasValue && cooldown > 0 && (now - last.Value).TotalSeconds < cooldown)
        {
            var wait = (int)Math.Ceiling(cooldown - (now - last.Value).TotalSeconds);
            var ago = (int)(now - last.Value).TotalSeconds;
            static string Seconds(int n) => n == 1 ? "1 second" : $"{n} seconds";
            throw new NudgeCooldownException($"{agent.Name} was nudged {(ago < 1 ? "just now" : Seconds(ago) + " ago")}. They can be nudged again in {Seconds(wait)}.", wait);
        }

        var supervisor = await _context.Users.AsNoTracking().Where(u => u.Id == supervisorUserId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Your supervisor";
        var text = string.IsNullOrWhiteSpace(reason) ? "Please check your active case queue and pick up the cases closest to their SLA." : reason.Trim();
        if (text.Length > 500) throw new ArgumentException("The nudge message can be at most 500 characters.");

        _context.Notifications.Add(new NotificationItem
        {
            Id = Guid.NewGuid(), RecipientUserId = agentId, Type = "Nudge", Title = $"Nudge from {supervisor}",
            Message = text, Priority = "High", IsRead = false, CreatedAt = now
        });
        _context.CaseEvents.Add(new CaseEvent
        {
            Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, Message = $"{supervisor} nudged {agent.Name}: {text}",
            CreatedAt = now, UserId = supervisorUserId, Module = "Team Monitoring", EntityName = agent.Name, ActionType = "NUDGE", NewValue = text
        });
        await _context.SaveChangesAsync(ct);

        return new NudgeResultDto { Message = $"Nudge sent to {agent.Name}.", NudgedAt = now, CooldownSeconds = cooldown };
    }
}
