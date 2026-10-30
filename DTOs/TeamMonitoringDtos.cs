namespace CaseManagement.Api.DTOs;

/// <summary>What the Team Monitor is asked for: one team, or every team in the caller's scope ("All teams").</summary>
public sealed record MonitoringQuery(Guid? TeamId = null, int PeriodDays = 7);

/// <summary>Team Monitoring → Export CSV: which agents (team, one agent, a state) and, for activity, which actions and when.</summary>
public sealed record MonitoringExportQuery(
    Guid? TeamId = null, Guid? AgentId = null, string? State = null, int PeriodDays = 7,
    string? ActionType = null, DateTime? From = null, DateTime? To = null);

/// <summary>Everything the Team Monitor shows, computed from live data in one response. Null means "no data", never a made-up number.</summary>
public class TeamMonitoringOverviewDto
{
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    /// <summary>The days the performance chart covers (today included), in the business time zone.</summary>
    public int PeriodDays { get; set; }
    public DateTime GeneratedAt { get; set; }

    /// <summary>True when the caller monitors every team; false when only the teams they lead or belong to.</summary>
    public bool SeesAllTeams { get; set; }
    /// <summary>The teams the caller may filter by (only those in their scope).</summary>
    public MonitoringFiltersDto Filters { get; set; } = new();

    public TeamMonitoringSummaryDto Summary { get; set; } = new();
    public List<AgentStatusItemDto> Agents { get; set; } = new();
    /// <summary>Per agent: open cases now, and cases resolved in the period. Agents with neither are left out of the chart.</summary>
    public List<AgentPerformanceItemDto> AgentPerformance { get; set; } = new();
    public QueueHealthDto QueueHealth { get; set; } = new();
}

public class MonitoringFiltersDto
{
    public List<NamedOptionDto> Teams { get; set; } = new();
}

public class TeamMonitoringSummaryDto
{
    /// <summary>Active teams in scope.</summary>
    public int TeamsCount { get; set; }
    public int OnlineAgentsCount { get; set; }
    public int TotalAgentsCount { get; set; }
    public int AgentsBreakCount { get; set; }
    /// <summary>Agents whose status is the default given when their record was created — they never set it themselves.</summary>
    public int AgentsStatusNotSetCount { get; set; }

    // The longest-waiting case nobody has answered yet (null when there is none).
    public int? LongestQueueWaitMinutes { get; set; }
    public string? LongestQueueCaseNumber { get; set; }
    public string? LongestQueueChannel { get; set; }
    public bool LongestQueueWaitOverTarget { get; set; }

    // Average time from opening to resolution for cases resolved today, and the same for yesterday (null when none resolved).
    public int ResolvedTodayCount { get; set; }
    public double? AvgResolutionMinutes { get; set; }
    public double? AvgResolutionYesterdayMinutes { get; set; }

    // Open cases held by available agents / what those agents can hold (null when nobody is available).
    public decimal? OccupancyPercent { get; set; }
    public int OccupancyTargetMin { get; set; }
    public int OccupancyTargetMax { get; set; }

    /// <summary>Open cases in scope.</summary>
    public int OpenCasesCount { get; set; }
}

public class AgentStatusItemDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Role { get; set; } = string.Empty;
    /// <summary>The teams in scope this person belongs to, comma-separated (kept for compact displays).</summary>
    public string TeamName { get; set; } = string.Empty;
    public List<AgentTeamDto> Teams { get; set; } = new();
    public string State { get; set; } = "Available"; // "Available", "On interaction", "Break", "Offline"
    /// <summary>When the person last set their status themselves; null = never (the state is the default, not confirmed).</summary>
    public DateTime? StatusSetAt { get; set; }
    public bool IsAssignable { get; set; } = true;
    public int OpenCasesCount { get; set; }
    public int BreachedCasesCount { get; set; }
    public int Capacity { get; set; }
    public int HandledTodayCount { get; set; }
    public int ResolvedInPeriodCount { get; set; }
    /// <summary>Their latest recorded action on any case or setting (null when the audit trail has none).</summary>
    public DateTime? LastActivityAt { get; set; }
}

public class AgentTeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsLead { get; set; }
    public bool IsAssignable { get; set; }
}

public class AgentPerformanceItemDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int OpenCount { get; set; }
    public int ResolvedCount { get; set; }
    /// <summary>Average opening-to-resolution time of the cases they resolved in the period (null when none).</summary>
    public double? AvgResolutionMinutes { get; set; }
}

/// <summary>
/// Open work, measured three ways over the same open cases: by queue (status), by team, and by source channel. Each view's
/// totals add up to the same number of open cases. No score or threshold is invented.
/// </summary>
public class QueueHealthDto
{
    public int TotalOpen { get; set; }
    public List<QueueStatusDto> ByStatus { get; set; } = new();
    public List<QueueGroupDto> ByTeam { get; set; } = new();
    public List<QueueGroupDto> ByChannel { get; set; } = new();
}

/// <summary>One queue (status group) across the teams shown.</summary>
public class QueueStatusDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Breached { get; set; }
}

/// <summary>Open cases of one team or one source channel, split by queue.</summary>
public class QueueGroupDto
{
    /// <summary>The team id, or the channel value as stored on the cases ("" when none was recorded).</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>A channel no longer in (or never in) the Source Channel list, kept so its cases are still counted.</summary>
    public bool IsUnlisted { get; set; }
    /// <summary>Open and nobody has answered the customer yet.</summary>
    public int AwaitingFirstResponse { get; set; }
    /// <summary>Open, already answered.</summary>
    public int Open { get; set; }
    public int InProgress { get; set; }
    /// <summary>Waiting on the customer, or pending.</summary>
    public int OnHold { get; set; }
    public int Escalated { get; set; }
    /// <summary>Any other unresolved status (accepted, rejected…).</summary>
    public int Other { get; set; }
    public int Total { get; set; }
    public int Breached { get; set; }
}

// ------------------------------------------------------------------------------------------------ agent drawer

public class AgentDetailDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTime? StatusSetAt { get; set; }
    public List<AgentTeamDto> Teams { get; set; } = new();
    public int OpenCasesCount { get; set; }
    public int BreachedCasesCount { get; set; }
    public int Capacity { get; set; }
    public int PeriodDays { get; set; }
    public int ResolvedInPeriodCount { get; set; }
    public double? AvgResolutionMinutes { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

public class AgentCaseItemDto
{
    public Guid Id { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    /// <summary>The SLA clock's verdict: Healthy, Approaching, Breached, Paused or Met.</summary>
    public string SlaHealth { get; set; } = string.Empty;
    public DateTime? SlaDueAt { get; set; }
}

public class AgentActivityItemDto
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public string? Module { get; set; }
}

public class NudgeAgentDto
{
    public string? Reason { get; set; }
}

public class NudgeResultDto
{
    public string Message { get; set; } = string.Empty;
    public DateTime NudgedAt { get; set; }
    /// <summary>How long until the same person can nudge this agent again.</summary>
    public int CooldownSeconds { get; set; }
}
