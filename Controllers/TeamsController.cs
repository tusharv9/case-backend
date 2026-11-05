namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

[ApiController]
[Route("api/[controller]")]
[RequirePermission(null, Permissions.TeamsManage)]
public class TeamsController : BaseApiController
{
    private readonly ITeamService _teamService;

    public TeamsController(ITeamService teamService)
    {
        _teamService = teamService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTeams(CancellationToken ct)
    {
        var teams = await _teamService.GetTeamsAsync(ct);
        return Ok(teams);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTeam(Guid id, CancellationToken ct)
    {
        var team = await _teamService.GetTeamByIdAsync(id, ct);
        if (team == null) return NotFound($"Team with ID '{id}' was not found.");
        return Ok(team);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTeam([FromBody] CreateTeamDto dto, CancellationToken ct)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { error = "Team name is required." });
        }

        var team = await _teamService.CreateTeamAsync(dto, CurrentUserId, ct);
        return CreatedAtAction(nameof(GetTeam), new { id = team.Id }, team);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTeam(Guid id, [FromBody] UpdateTeamDto dto, CancellationToken ct)
    {
        if (dto == null)
        {
            return BadRequest(new { error = "Update payload is required." });
        }

        var team = await _teamService.UpdateTeamAsync(id, dto, CurrentUserId, ct);
        return Ok(team);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTeam(Guid id, CancellationToken ct)
    {
        await _teamService.DeleteTeamAsync(id, CurrentUserId, ct);
        return Ok(new { message = "Team deleted successfully." });
    }

    [HttpPost("{id:guid}/members")]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddTeamMemberDto dto, CancellationToken ct)
    {
        if (dto == null || dto.UserId == Guid.Empty)
        {
            return BadRequest(new { error = "UserId is required." });
        }

        await _teamService.AddMemberAsync(id, dto, CurrentUserId, ct);
        return Ok(new { message = "Member added to team successfully." });
    }

    /// <summary>Takes a member off the team. The reason (body or <c>?reason=</c>) is required and kept in the audit history.</summary>
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, [FromQuery] string? reason, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TeamRemovalDto? dto, CancellationToken ct)
    {
        var why = (dto?.Reason ?? reason)?.Trim();
        if (string.IsNullOrWhiteSpace(why)) return BadRequest(new { error = "Give a reason for removing this member." });
        await _teamService.RemoveMemberAsync(id, userId, CurrentUserId, ct, why);
        return Ok(new { message = "Member removed from team successfully." });
    }

    /// <summary>Removes the team's lead (they stay on the team only if they were also chosen as a member). A reason is required.</summary>
    [HttpDelete("{id:guid}/lead")]
    public async Task<IActionResult> RemoveLead(Guid id, [FromQuery] string? reason, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TeamRemovalDto? dto, CancellationToken ct)
    {
        var why = (dto?.Reason ?? reason)?.Trim();
        if (string.IsNullOrWhiteSpace(why)) return BadRequest(new { error = "Give a reason for removing the team lead." });
        return Ok(await _teamService.RemoveLeadAsync(id, why, CurrentUserId, ct));
    }

    /// <summary>Activates or deactivates the team. When activating, the body may name the departments it is to handle.</summary>
    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> ToggleStatus(Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ToggleTeamDto? dto, CancellationToken ct)
    {
        var team = await _teamService.ToggleTeamStatusAsync(id, CurrentUserId, ct, dto?.DepartmentIds);
        return Ok(team);
    }
}
