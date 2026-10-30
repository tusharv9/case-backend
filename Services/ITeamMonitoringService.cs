namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;

public interface ITeamMonitoringService
{
    /// <summary>The whole monitor in one call, for the teams in the caller's scope (all of them, one team, or one department's).</summary>
    Task<TeamMonitoringOverviewDto> GetOverviewAsync(MonitoringQuery query, CancellationToken ct = default);

    /// <summary>The agents the export would cover: the monitor's agents for the team (or all in scope), narrowed by agent and state.</summary>
    Task<IReadOnlyList<AgentStatusItemDto>> GetExportAgentsAsync(MonitoringExportQuery query, CancellationToken ct = default);

    /// <summary>Team Monitoring → Export CSV → Agent summary: one row per agent, from the same figures the page shows.</summary>
    Task<ReportExport> ExportAgentSummaryAsync(MonitoringExportQuery query, CancellationToken ct = default);

    /// <summary>One agent, for the monitor's side drawer. 404 when the agent is outside the caller's scope.</summary>
    Task<AgentDetailDto> GetAgentAsync(Guid agentId, int periodDays, CancellationToken ct = default);

    /// <summary>The agent's cases the caller may see: <c>open</c> (unresolved, oldest first) or <c>resolved</c> (newest first).</summary>
    Task<PagedResponseDto<AgentCaseItemDto>> GetAgentCasesAsync(Guid agentId, string scope, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Throws <see cref="KeyNotFoundException"/> unless the caller may monitor this agent.</summary>
    Task EnsureCanMonitorAsync(Guid agentId, CancellationToken ct = default);

    Task<NudgeResultDto> NudgeAgentAsync(Guid agentId, string? reason, Guid supervisorUserId, CancellationToken ct = default);
}

public class TeamMonitoringOptions
{
    public const string SectionName = "TeamMonitoring";

    /// <summary>The occupancy range (% of capacity) the wallboard treats as healthy.</summary>
    public int OccupancyTargetMin { get; set; } = 70;
    public int OccupancyTargetMax { get; set; } = 85;

    /// <summary>How long after a nudge the same agent can be nudged again (one reminder, not a stream of them).</summary>
    public int NudgeCooldownSeconds { get; set; } = 120;
}

/// <summary>The agent was nudged moments ago; the body says when they can be nudged again (429).</summary>
public sealed class NudgeCooldownException(string message, int retryAfterSeconds) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
