namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

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

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId, CancellationToken ct)
    {
        await _teamService.RemoveMemberAsync(id, userId, CurrentUserId, ct);
        return Ok(new { message = "Member removed from team successfully." });
    }

    [HttpPost("{id:guid}/toggle")]
    public async Task<IActionResult> ToggleStatus(Guid id, CancellationToken ct)
    {
        var team = await _teamService.ToggleTeamStatusAsync(id, CurrentUserId, ct);
        return Ok(team);
    }
}
