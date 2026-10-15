namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Host users for team membership. The Host owns identities; Case Management lists them from the Host's directory and
/// keeps only a reference for those who join a team.
/// </summary>
[ApiController]
[Route("api/host-users")]
[RequirePermission(Permissions.TeamsManage)]
public class HostUsersController(HostUserService users) : BaseApiController
{
    /// <summary>Active Host users matching <paramref name="search"/>, each with the teams they already belong to.</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        => Ok(await users.SearchAsync(search, page, pageSize, ct));

    /// <summary>Case Management's reference for each chosen Host user (created when missing), to add them to a team.</summary>
    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] LinkHostUsersDto dto, CancellationToken ct)
        => Ok(await users.LinkAsync(dto.HostUserIds, ct));
}
