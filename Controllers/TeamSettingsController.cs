namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Case Settings → Teams → Team Rules: the team policies. Reading needs any Case Management access (the team form follows
/// these rules); saving needs config.manage. The form itself is configured through the field-configuration API.
/// </summary>
[ApiController]
[Route("api/team-settings")]
[RequirePermission(null, Permissions.ConfigManage)]
public class TeamSettingsController : BaseApiController
{
    private readonly ITeamSettingsService _settings;

    public TeamSettingsController(ITeamSettingsService settings) => _settings = settings;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _settings.GetAsync(ct));

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] UpdateTeamSettingsDto dto, CancellationToken ct) =>
        Ok(await _settings.SaveAsync(dto ?? new UpdateTeamSettingsDto(), CurrentUserId, ct));
}
