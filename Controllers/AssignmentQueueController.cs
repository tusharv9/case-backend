namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Agent Assignment → Needs an agent: the open cases held by a team lead or left with an agent who is no longer in the team.
/// Read only — a case is given its agent through the case's own Assign (PUT /api/cases/{id}/assign), which checks who may take
/// it. Reading the queue is for supervisors (Supervise cases), and lists only the cases the caller may see.
/// </summary>
[ApiController]
[Route("api/assignment/queue")]
[RequirePermission(Permissions.CasesSupervise)]
public class AssignmentQueueController : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> Get([FromServices] AppDbContext db, [FromServices] ICaseAccessScope access,
        [FromQuery] Guid? teamId, [FromQuery] string? reason, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await AssignmentQueue.ReadAsync(db, access, new AssignmentQueueQuery(teamId, reason, search, page, pageSize), ct));
}
