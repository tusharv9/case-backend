namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Supervisor bulk operations: Bulk Reassign and Bulk Resolve. Every endpoint needs <c>cases:Supervise</c> (Host
/// administrators hold it implicitly); each selected case is still checked on its own (<see cref="BulkCaseService"/>).
/// </summary>
[ApiController]
[Route("api/cases/bulk")]
[RequirePermission(Permissions.CasesSupervise)]
public class BulkCasesController(IBulkCaseService bulk) : BaseApiController
{
    /// <summary>The cases the caller may reassign or resolve, one page at a time.</summary>
    [HttpGet("cases")]
    public async Task<IActionResult> Search([FromQuery] BulkCaseQueryDto query, CancellationToken ct)
        => Ok(await bulk.SearchAsync(query, ct));

    /// <summary>The Team Leads and agents of the team a department is assigned to.</summary>
    [HttpGet("assignees")]
    public async Task<IActionResult> Assignees([FromQuery] Guid departmentId, CancellationToken ct)
        => Ok(await bulk.AssigneesAsync(departmentId, ct));

    [HttpPost("reassign")]
    public async Task<IActionResult> Reassign([FromBody] BulkReassignRequestDto request, CancellationToken ct)
        => Ok(await bulk.ReassignAsync(request, CurrentUserId, ct));

    [HttpPost("resolve")]
    public async Task<IActionResult> Resolve([FromBody] BulkResolveRequestDto request, CancellationToken ct)
        => Ok(await bulk.ResolveAsync(request, CurrentUserId, ct));
}
