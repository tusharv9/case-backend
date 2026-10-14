namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>Configurable Settings → Case Management → Assign &amp; Reassign. Reading needs a signed-in user; saving needs config.manage.</summary>
[ApiController]
[Route("api/assignment-settings")]
[RequirePermission(null, Permissions.ConfigManage)]
public class AssignmentSettingsController : BaseApiController
{
    private readonly IAssignmentSettingsService _settings;

    public AssignmentSettingsController(IAssignmentSettingsService settings) => _settings = settings;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _settings.GetAsync(ct));

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] UpdateAssignmentSettingsDto dto, CancellationToken ct) =>
        Ok(await _settings.SaveAsync(dto, CurrentUserId, ct));
}
