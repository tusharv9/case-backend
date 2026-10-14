namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

[ApiController]
[Route("api/sla-routing")]
[RequirePermission(null, Permissions.ConfigManage)]
public class CasesSlaRoutingController : BaseApiController
{
    private readonly ISlaRoutingService _slaRoutingService;

    public CasesSlaRoutingController(ISlaRoutingService slaRoutingService)
    {
        _slaRoutingService = slaRoutingService;
    }

    [HttpGet("configuration")]
    public async Task<IActionResult> GetConfiguration(CancellationToken ct = default)
    {
        var config = await _slaRoutingService.GetFullConfigurationAsync(ct);
        return Ok(config);
    }

    /// <summary>
    /// The priority a new case in this department/sub-category would get (and its SLA hours), so the Create
    /// Case form can show it without downloading the whole SLA configuration.
    /// </summary>
    [HttpGet("resolve-priority")]
    public async Task<IActionResult> ResolvePriority([FromQuery] Guid? departmentId, [FromQuery] string? subcategory, CancellationToken ct = default)
    {
        var resolution = await _slaRoutingService.PreviewPriorityAsync(departmentId, subcategory, ct);
        return Ok(resolution);
    }

    [HttpPut("configuration")]
    public async Task<IActionResult> UpdateConfiguration([FromBody] UpdateSlaRoutingConfigRequestDto request, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actingUserId = CurrentUserId;
        await _slaRoutingService.UpdateConfigurationAsync(request, actingUserId, ct);
        var updated = await _slaRoutingService.GetFullConfigurationAsync(ct);
        return Ok(updated);
    }

    [HttpPost("holidays")]
    public async Task<IActionResult> AddHoliday([FromBody] CreatePublicHolidayDto dto, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actingUserId = CurrentUserId;
        var created = await _slaRoutingService.AddPublicHolidayAsync(dto, actingUserId, ct);
        return CreatedAtAction(nameof(GetConfiguration), new { id = created.Id }, created);
    }

    [HttpPut("holidays/{id:guid}")]
    public async Task<IActionResult> UpdateHoliday(Guid id, [FromBody] UpdatePublicHolidayDto dto, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actingUserId = CurrentUserId;
        var updated = await _slaRoutingService.UpdatePublicHolidayAsync(id, dto, actingUserId, ct);
        if (updated == null) return NotFound(new { error = "Public holiday not found." });
        return Ok(updated);
    }

    [HttpDelete("holidays/{id:guid}")]
    public async Task<IActionResult> DeleteHoliday(Guid id, CancellationToken ct = default)
    {
        var actingUserId = CurrentUserId;
        var success = await _slaRoutingService.DeletePublicHolidayAsync(id, actingUserId, ct);
        if (!success) return NotFound(new { error = "Public holiday not found." });
        return Ok(new { message = "Public holiday deleted successfully." });
    }

    [HttpPost("escalation-levels")]
    public async Task<IActionResult> AddEscalationLevel([FromBody] CreateEscalationLevelDto dto, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actingUserId = CurrentUserId;
        var created = await _slaRoutingService.AddEscalationLevelAsync(dto, actingUserId, ct);
        return CreatedAtAction(nameof(GetConfiguration), new { id = created.Id }, created);
    }

    [HttpPut("escalation-levels/{id:guid}")]
    public async Task<IActionResult> UpdateEscalationLevel(Guid id, [FromBody] UpdateEscalationLevelDto dto, CancellationToken ct = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actingUserId = CurrentUserId;
        var updated = await _slaRoutingService.UpdateEscalationLevelAsync(id, dto, actingUserId, ct);
        if (updated == null) return NotFound(new { error = "Escalation level not found." });
        return Ok(updated);
    }

    [HttpDelete("escalation-levels/{id:guid}")]
    public async Task<IActionResult> DeleteEscalationLevel(Guid id, CancellationToken ct = default)
    {
        var actingUserId = CurrentUserId;
        var success = await _slaRoutingService.DeleteEscalationLevelAsync(id, actingUserId, ct);
        if (!success) return NotFound(new { error = "Escalation level not found." });
        return Ok(new { message = "Escalation level deleted successfully." });
    }

    [HttpGet("cases/{caseId:guid}/escalation-status")]
    public async Task<IActionResult> GetCaseEscalationStatus(Guid caseId, CancellationToken ct = default)
    {
        var status = await _slaRoutingService.GetCaseEscalationStatusAsync(caseId, ct);
        if (status == null) return NotFound(new { error = "Case not found." });
        return Ok(status);
    }
}
