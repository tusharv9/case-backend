namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/routing-rules")]
[RequirePermission(Permissions.ConfigManage)]
public class RoutingRulesController : BaseApiController
{
    private readonly IRoutingEngineService _routingEngine;

    public RoutingRulesController(IRoutingEngineService routingEngine)
    {
        _routingEngine = routingEngine;
    }

    [HttpGet]
    public async Task<IActionResult> GetRules(CancellationToken ct)
    {
        var rules = await _routingEngine.GetRulesAsync(ct);
        return Ok(rules);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct)
    {
        var rule = await _routingEngine.GetRuleByIdAsync(id, ct);
        if (rule == null)
        {
            return NotFound(new { error = $"Routing rule '{id}' not found." });
        }
        return Ok(rule);
    }

    [HttpPost]
    public async Task<IActionResult> CreateRule([FromBody] CreateRoutingRuleDto dto, CancellationToken ct)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { error = "Rule name is required." });
        }

        if (dto.TargetDepartmentId == Guid.Empty)
        {
            return BadRequest(new { error = "A valid destination team (TargetDepartmentId) is required." });
        }

        var created = await _routingEngine.CreateRuleAsync(dto, CurrentUserId, ct);
        return CreatedAtAction(nameof(GetRule), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] UpdateRoutingRuleDto dto, CancellationToken ct)
    {
        if (dto == null)
        {
            return BadRequest(new { error = "Payload cannot be empty." });
        }

        var updated = await _routingEngine.UpdateRuleAsync(id, dto, CurrentUserId, ct);
        return Ok(updated);
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> ToggleRule(Guid id, CancellationToken ct)
    {
        var toggled = await _routingEngine.ToggleRuleAsync(id, CurrentUserId, ct);
        return Ok(toggled);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        await _routingEngine.DeleteRuleAsync(id, CurrentUserId, ct);
        return NoContent();
    }

    [HttpPut("reorder")]
    public async Task<IActionResult> ReorderRules([FromBody] ReorderRulesDto dto, CancellationToken ct)
    {
        if (dto == null || dto.RuleIds == null || dto.RuleIds.Count == 0)
        {
            return BadRequest(new { error = "RuleIds list is required for reordering." });
        }

        await _routingEngine.ReorderRulesAsync(dto, CurrentUserId, ct);
        return Ok(new { success = true, message = "Rules reordered successfully." });
    }

    /// <summary>The global assignment settings, or those in force for one team (<c>?departmentId=</c>).</summary>
    [HttpGet("assignment-config")]
    public async Task<IActionResult> GetAssignmentConfig([FromQuery] Guid? departmentId, CancellationToken ct)
    {
        var config = await _routingEngine.GetAssignmentConfigAsync(departmentId, ct);
        return Ok(config);
    }

    [HttpPut("assignment-config")]
    public async Task<IActionResult> UpdateAssignmentConfig([FromBody] UpdateAssignmentConfigDto dto, [FromQuery] Guid? departmentId, CancellationToken ct)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Algorithm))
        {
            return BadRequest(new { error = "Assignment algorithm is required." });
        }

        var updated = await _routingEngine.UpdateAssignmentConfigAsync(dto, CurrentUserId, departmentId, ct);
        return Ok(updated);
    }

    /// <summary>What a routing rule can look at and the valid values for each (so editors never hard-code option lists).</summary>
    [HttpGet("vocabulary")]
    public async Task<IActionResult> GetVocabulary(CancellationToken ct) => Ok(await _routingEngine.GetVocabularyAsync(ct));
}
