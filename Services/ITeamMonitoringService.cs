namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;

public interface ITeamMonitoringService
{
    /// <summary>The whole monitor in one call, for every team or just one.</summary>
    Task<TeamMonitoringOverviewDto> GetOverviewAsync(Guid? teamId = null, CancellationToken ct = default);
    Task NudgeAgentAsync(Guid agentId, string? reason, Guid supervisorUserId, CancellationToken ct = default);
}

public class TeamMonitoringOptions
{
    public const string SectionName = "TeamMonitoring";

    /// <summary>The occupancy range (% of capacity) the wallboard treats as healthy.</summary>
    public int OccupancyTargetMin { get; set; } = 70;
    public int OccupancyTargetMax { get; set; } = 85;
}
