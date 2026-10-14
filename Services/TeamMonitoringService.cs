namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// The Team Monitor, computed from live data only. Who counts as an agent comes from team membership; whether a case is
/// breached or at risk is the SLA clock's verdict (the same one the dashboard and the worker use); and where there is no
/// data the answer is "none", not a default.
/// </summary>
public class TeamMonitoringService : ITeamMonitoringService
{
    private readonly AppDbContext _context;
    private readonly ISlaClockProvider _slaClock;
    private readonly IRoutingEngineService _routing;
    private readonly TeamMonitoringOptions _options;

    public TeamMonitoringService(AppDbContext context, ISlaClockProvider slaClock, IRoutingEngineService routing, IOptions<TeamMonitoringOptions> options)
    {
        _context = context;
        _slaClock = slaClock;
        _routing = routing;
        _options = options.Value;
    }

    private sealed record OpenCase(Guid Id, string CaseNumber, string Title, string Severity, string? Channel, Guid OwnerId, string OwnerName,
        DateTime WaitingSince, CaseStatus Status, SlaInputs Inputs);   // WaitingSince = when its SLA clock started

    public async Task<TeamMonitoringOverviewDto> GetOverviewAsync(Guid? teamId = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        string? teamName = null;
        if (teamId.HasValue)
        {
            teamName = await _context.Departments.AsNoTracking().Where(d => d.Id == teamId).Select(d => d.Name).FirstOrDefaultAsync(ct)
                       ?? throw new KeyNotFoundException("Team not found.");
        }

        var clock = await _slaClock.GetAsync(ct);
        var (todayStart, tomorrowStart) = LocalDayBounds(clock.Calendar, now, 0);
        var (yesterdayStart, _) = LocalDayBounds(clock.Calendar, now, -1);

        // ---- people: active, assignable-or-not members of (the chosen / all active) teams ----------------------------
        var memberRows = await _context.TeamMembers.AsNoTracking()
            .Where(m => m.IsActive && m.User.IsActive && (teamId == null ? m.Department.IsActive : m.DepartmentId == teamId))
            .Select(m => new { m.DepartmentId, TeamName = m.Department.Name, m.IsAssignable, m.MemberRole, User = m.User })
            .ToListAsync(ct);

        var globalCapacity = (await _routing.GetAssignmentConfigAsync(null, ct)).MaxConcurrentCapacity;
        var teamCapacity = await _context.AssignmentConfigurations.AsNoTracking()
            .Where(c => c.DepartmentId != null && c.IsActive).ToDictionaryAsync(c => c.DepartmentId!.Value, c => c.MaxConcurrentCapacity, ct);

        var agents = memberRows.GroupBy(m => m.User.Id).Select(g => new
        {
            User = g.First().User,
            Role = g.First().User.Role,
            Teams = g.Select(x => x.TeamName).Distinct().OrderBy(n => n).ToList(),
            IsAssignable = g.Any(x => x.IsAssignable),
            Capacity = g.Max(x => teamCapacity.GetValueOrDefault(x.DepartmentId, globalCapacity)),
        }).ToList();

        // ---- open cases in scope, each judged once by the SLA clock --------------------------------------------------
        var openQuery = _context.Cases.AsNoTracking()
            .Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled);
        if (teamId.HasValue) openQuery = openQuery.Where(c => c.DepartmentId == teamId);

        var open = (await openQuery.Select(c => new
        {
            c.Id, c.CaseNumber, c.Title, c.Severity, c.SourceChannel, c.CommunicationChannel, c.OwnerId,
            OwnerName = c.Owner.Name, WaitingSince = c.SlaStartTime, c.Status,
            Inputs = new SlaInputs
            {
                Status = c.Status, StartUtc = c.SlaStartTime, ResolvedAt = c.ResolvedAt, PausedAt = c.SlaPausedAt, PausedMinutes = c.SlaTotalPausedMinutes,
                FirstResponseTargetMinutes = c.FirstResponseTargetMinutes, InternalTargetMinutes = c.InternalResolutionTargetMinutes,
                ExternalTargetMinutes = c.ExternalResolutionTargetMinutes, FirstResponseActualAt = c.FirstResponseActualAt, FirstResponseStatus = c.FirstResponseStatus,
            }
        }).ToListAsync(ct))
            .Select(c => new OpenCase(c.Id, c.CaseNumber, c.Title, c.Severity, string.IsNullOrWhiteSpace(c.SourceChannel) ? c.CommunicationChannel : c.SourceChannel,
                c.OwnerId, c.OwnerName, c.WaitingSince, c.Status, c.Inputs))
            .ToList();
        var verdicts = open.ToDictionary(c => c.Id, c => clock.Evaluate(c.Inputs, now));

        // ---- resolved today / yesterday ------------------------------------------------------------------------------
        var resolvedQuery = _context.Cases.AsNoTracking().Where(c => c.ResolvedAt != null);
        if (teamId.HasValue) resolvedQuery = resolvedQuery.Where(c => c.DepartmentId == teamId);

        var resolvedToday = await resolvedQuery.Where(c => c.ResolvedAt >= todayStart && c.ResolvedAt < tomorrowStart)
            .Select(c => new { c.OwnerId, Minutes = (c.ResolvedAt!.Value - c.CreatedAt).TotalMinutes }).ToListAsync(ct);
        var yesterdayMinutes = await resolvedQuery.Where(c => c.ResolvedAt >= yesterdayStart && c.ResolvedAt < todayStart)
            .Select(c => (c.ResolvedAt!.Value - c.CreatedAt).TotalMinutes).ToListAsync(ct);
        var handledByAgent = resolvedToday.GroupBy(r => r.OwnerId).ToDictionary(g => g.Key, g => g.Count());

        // ---- assemble ------------------------------------------------------------------------------------------------
        var openByOwner = open.GroupBy(c => c.OwnerId).ToDictionary(g => g.Key, g => g.ToList());
        var agentItems = agents.Select(a =>
        {
            var mine = openByOwner.GetValueOrDefault(a.User.Id) ?? new List<OpenCase>();
            return new AgentStatusItemDto
            {
                UserId = a.User.Id,
                Name = a.User.Name,
                Role = string.IsNullOrWhiteSpace(a.Role) ? "Service Agent" : a.Role,
                TeamName = string.Join(", ", a.Teams),
                State = a.User.Status switch
                {
                    UserStatus.Available => "Available", UserStatus.Busy => "On interaction", UserStatus.Away => "Break", _ => "Offline",
                },
                IsAssignable = a.IsAssignable,
                OpenCasesCount = mine.Count,
                BreachedCasesCount = mine.Count(c => verdicts[c.Id].Health == SlaHealth.Breached),
                Capacity = a.Capacity,
                HandledTodayCount = handledByAgent.GetValueOrDefault(a.User.Id),
            };
        }).OrderBy(a => a.Name).ToList();

        var online = agents.Where(a => a.User.Status is UserStatus.Available or UserStatus.Busy).ToList();
        var onlineCapacity = online.Sum(a => a.Capacity);
        var onlineOpen = online.Sum(a => (openByOwner.GetValueOrDefault(a.User.Id)?.Count) ?? 0);

        // The longest wait is the oldest case still waiting for its first answer.
        var waiting = open.Where(c => c.Status == CaseStatus.Open && c.Inputs.FirstResponseActualAt == null).OrderBy(c => c.WaitingSince).ToList();
        var longest = waiting.FirstOrDefault();

        var summary = new TeamMonitoringSummaryDto
        {
            OnlineAgentsCount = online.Count,
            TotalAgentsCount = agents.Count,
            AgentsBreakCount = agents.Count(a => a.User.Status == UserStatus.Away),
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
        };

        // Queue health per configured source channel, plus any channel cases actually use that is not in the list (nothing hidden).
        var configured = await _context.LookupValues.AsNoTracking()
            .Where(v => v.LookupType!.Code == "SOURCE_CHANNEL" && v.IsActive).OrderBy(v => v.DisplayOrder).Select(v => v.Value).ToListAsync(ct);
        var channels = configured.Concat(open.Select(c => c.Channel).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var queues = channels.Select(ch =>
        {
            var inChannel = open.Where(c => string.Equals(c.Channel, ch, StringComparison.OrdinalIgnoreCase)).ToList();
            var waitingHere = waiting.Where(c => string.Equals(c.Channel, ch, StringComparison.OrdinalIgnoreCase)).ToList();
            return new QueueHealthItemDto
            {
                Channel = ch,
                OpenCount = inChannel.Count,
                WaitingCount = waitingHere.Count,
                BreachedCount = inChannel.Count(c => verdicts[c.Id].Health == SlaHealth.Breached),
                OldestWaitMinutes = waitingHere.Count == 0 ? null : (int)Math.Max(0, (now - waitingHere.Min(c => c.WaitingSince)).TotalMinutes),
            };
        }).ToList();

        var atRisk = open
            .Where(c => verdicts[c.Id].Health is SlaHealth.Approaching or SlaHealth.Breached)
            .OrderByDescending(c => verdicts[c.Id].External.ConsumedPercent)
            .Take(5)
            .Select(c =>
            {
                var v = verdicts[c.Id];
                return new SlaAtRiskCaseDto
                {
                    CaseId = c.Id, CaseNumber = c.CaseNumber, Title = c.Title, Severity = c.Severity, Health = v.Health.ToString(),
                    ElapsedPercent = v.External.ConsumedPercent,
                    TimeRemainingMinutes = (int)Math.Round(v.External.RemainingMinutes),
                    DueAt = v.External.DueAt, OwnerName = c.OwnerName,
                };
            }).ToList();

        return new TeamMonitoringOverviewDto
        {
            TeamId = teamId, TeamName = teamName, GeneratedAt = now,
            Summary = summary, Agents = agentItems, Queues = queues, SlaAtRisk = atRisk,
        };
    }

    /// <summary>The UTC bounds of a calendar day in the business time zone (offset by whole days from today).</summary>
    private static (DateTime Start, DateTime End) LocalDayBounds(BusinessCalendar calendar, DateTime nowUtc, int dayOffset)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, calendar.TimeZone).Date.AddDays(dayOffset);
        var start = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), calendar.TimeZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.AddDays(1), DateTimeKind.Unspecified), calendar.TimeZone);
        return (start, end);
    }

    public async Task NudgeAgentAsync(Guid agentId, string? reason, Guid supervisorUserId, CancellationToken ct = default)
    {
        var agent = await _context.Users.FindAsync(new object[] { agentId }, ct);
        if (agent == null) throw new KeyNotFoundException("Agent not found.");
        if (!agent.IsActive) throw new InvalidOperationException($"{agent.Name} is deactivated.");

        var message = !string.IsNullOrWhiteSpace(reason)
            ? reason
            : "Team Lead nudge: Please check your active case queue and address high SLA priority items.";

        _context.Notifications.Add(new NotificationItem
        {
            Id = Guid.NewGuid(), RecipientUserId = agentId, Type = "Nudge", Title = "Supervisor Operational Nudge",
            Message = message, Priority = "High", IsRead = false, CreatedAt = DateTime.UtcNow
        });

        _context.CaseEvents.Add(new CaseEvent
        {
            Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, Message = $"Supervisor nudged agent '{agent.Name}'",
            CreatedAt = DateTime.UtcNow, UserId = supervisorUserId, Module = "Team Monitoring", EntityName = "Agent", ActionType = "NUDGE", NewValue = message
        });

        await _context.SaveChangesAsync(ct);
    }
}
