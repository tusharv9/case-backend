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

/// <summary>The business departments teams are linked to. Listing needs any Case Management access (filters use it); changes need config.manage.</summary>
[ApiController]
[Route("api/org-departments")]
[RequirePermission(null, Permissions.ConfigManage)]
public class OrgDepartmentsController : BaseApiController
{
    private readonly IOrgDepartmentService _departments;

    public OrgDepartmentsController(IOrgDepartmentService departments) => _departments = departments;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        Ok(await _departments.ListAsync(includeInactive, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveOrgDepartmentDto dto, CancellationToken ct) =>
        Ok(await _departments.CreateAsync(dto ?? new SaveOrgDepartmentDto(), CurrentUserId, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveOrgDepartmentDto dto, CancellationToken ct) =>
        Ok(await _departments.UpdateAsync(id, dto ?? new SaveOrgDepartmentDto(), CurrentUserId, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _departments.DeleteAsync(id, CurrentUserId, ct);
        return Ok(new { message = "Department deleted." });
    }

    [HttpPut("order")]
    public async Task<IActionResult> Reorder([FromBody] List<Guid> ids, CancellationToken ct) =>
        Ok(await _departments.ReorderAsync(ids ?? new List<Guid>(), CurrentUserId, ct));

    [HttpPut("{id:guid}/default-team")]
    public async Task<IActionResult> SetDefaultTeam(Guid id, [FromBody] SetDefaultTeamDto dto, CancellationToken ct) =>
        Ok(await _departments.SetDefaultTeamAsync(id, dto?.TeamId, CurrentUserId, ct));
}
