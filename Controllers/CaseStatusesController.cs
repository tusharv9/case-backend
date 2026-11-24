using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services.Statuses;
using Microsoft.AspNetCore.Mvc;

namespace CaseManagement.Api.Controllers;

/// <summary>
/// The case statuses. Everyone who works cases reads them (badges, board columns, menus, filters); only people who manage case
/// configuration change them (Configurable Settings → Status).
/// </summary>
[ApiController]
[Route("api/case-statuses")]
[RequirePermission(null, Permissions.ConfigManage)]
public class CaseStatusesController(ICaseStatusAdminService statuses) : BaseApiController
{
    /// <summary>Statuses in display order and the allowed manual moves; <c>?counts=true</c> adds how many cases are in each.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool counts, [FromServices] ICurrentUserAccessor user, CancellationToken ct) =>
        Ok(await statuses.GetAsync(counts && user.Has(Permissions.ConfigManage), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveCaseStatusDto dto, CancellationToken ct) => Ok(await statuses.CreateAsync(dto, ct));

    [HttpPut("{key}")]
    public async Task<IActionResult> Update(string key, [FromBody] SaveCaseStatusDto dto, CancellationToken ct) => Ok(await statuses.UpdateAsync(key, dto, ct));

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        await statuses.DeleteAsync(key, ct);
        return NoContent();
    }

    [HttpPut("transitions")]
    public async Task<IActionResult> SaveTransitions([FromBody] SaveCaseStatusTransitionsDto dto, CancellationToken ct) =>
        Ok(await statuses.SaveTransitionsAsync(dto, ct));
}
