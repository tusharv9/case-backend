namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The departments and their team assignment. Listing needs any Case Management access (Create Case and filters use it);
/// changes need config.manage.
/// </summary>
[ApiController]
[Route("api/departments")]
[RequirePermission(null, Permissions.ConfigManage)]
public class DepartmentsController : BaseApiController
{
    private readonly IDepartmentService _departments;

    public DepartmentsController(IDepartmentService departments) => _departments = departments;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        Ok(await _departments.ListAsync(includeInactive, ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveDepartmentDto dto, CancellationToken ct) =>
        Ok(await _departments.CreateAsync(dto ?? new SaveDepartmentDto(), CurrentUserId, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveDepartmentDto dto, CancellationToken ct) =>
        Ok(await _departments.UpdateAsync(id, dto ?? new SaveDepartmentDto(), CurrentUserId, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _departments.DeleteAsync(id, CurrentUserId, ct);
        return Ok(new { message = "Department deleted." });
    }

    [HttpPut("order")]
    public async Task<IActionResult> Reorder([FromBody] List<Guid> ids, CancellationToken ct) =>
        Ok(await _departments.ReorderAsync(ids ?? new List<Guid>(), CurrentUserId, ct));

    /// <summary>Assigns the department to a team (or, with a null team, removes the assignment).</summary>
    [HttpPut("{id:guid}/team")]
    public async Task<IActionResult> AssignTeam(Guid id, [FromBody] AssignDepartmentTeamDto dto, CancellationToken ct) =>
        Ok(await _departments.AssignTeamAsync(id, dto?.TeamId, CurrentUserId, ct));
}
