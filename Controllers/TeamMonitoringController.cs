namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/team-monitoring")]
[RequirePermission(Permissions.MonitoringView)]
public class TeamMonitoringController : BaseApiController
{
    private readonly ITeamMonitoringService _monitoringService;

    public TeamMonitoringController(ITeamMonitoringService monitoringService)
    {
        _monitoringService = monitoringService;
    }

    /// <summary>The whole monitor in one request, for every team or one (<c>?teamId=</c>).</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview([FromQuery] Guid? teamId, CancellationToken ct)
    {
        try
        {
            return Ok(await _monitoringService.GetOverviewAsync(teamId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("nudge/{agentId:guid}")]
    [RequirePermission(Permissions.MonitoringNudge)]
    public async Task<IActionResult> NudgeAgent(Guid agentId, [FromBody] NudgeAgentDto? dto, CancellationToken ct)
    {
        await _monitoringService.NudgeAgentAsync(agentId, dto?.Reason, CurrentUserId, ct);
        return Ok(new { message = "Agent has been nudged successfully." });
    }
}
