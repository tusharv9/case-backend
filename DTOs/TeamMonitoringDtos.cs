namespace CaseManagement.Api.DTOs;

/// <summary>Everything the Team Monitor shows, computed from live data in one response. Null means "no data", never a made-up number.</summary>
public class TeamMonitoringOverviewDto
{
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public DateTime GeneratedAt { get; set; }

    public TeamMonitoringSummaryDto Summary { get; set; } = new();
    public List<AgentStatusItemDto> Agents { get; set; } = new();
    public List<QueueHealthItemDto> Queues { get; set; } = new();
    public List<SlaAtRiskCaseDto> SlaAtRisk { get; set; } = new();
}

public class TeamMonitoringSummaryDto
{
    public int OnlineAgentsCount { get; set; }
    public int TotalAgentsCount { get; set; }
    public int AgentsBreakCount { get; set; }

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
}

public class AgentStatusItemDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string State { get; set; } = "Available"; // "Available", "On interaction", "Break", "Offline"
    public bool IsAssignable { get; set; } = true;
    public int OpenCasesCount { get; set; }
    public int BreachedCasesCount { get; set; }
    public int Capacity { get; set; }
    public int HandledTodayCount { get; set; }
}

public class QueueHealthItemDto
{
    public string Channel { get; set; } = string.Empty;

    /// <summary>Open cases not yet answered.</summary>
    public int WaitingCount { get; set; }
    public int OpenCount { get; set; }
    public int BreachedCount { get; set; }
    public int? OldestWaitMinutes { get; set; }
}

public class SlaAtRiskCaseDto
{
    public Guid CaseId { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Health { get; set; } = string.Empty;
    public double ElapsedPercent { get; set; }
    public int TimeRemainingMinutes { get; set; }
    public DateTime? DueAt { get; set; }
    public string OwnerName { get; set; } = string.Empty;
}

public class NudgeAgentDto
{
    public string? Reason { get; set; }
}
