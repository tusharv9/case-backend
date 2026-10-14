namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>Skills used by skill-based assignment: which skills a case needs (rules) and which skills each agent has.</summary>
[ApiController]
[Route("api/skills")]
[RequirePermission(null, Permissions.TeamsManage)]
public class SkillsController : BaseApiController
{
    private readonly ISkillService _skills;

    public SkillsController(ISkillService skills) => _skills = skills;

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules(CancellationToken ct) => Ok(new
    {
        rules = await _skills.GetRulesAsync(ct),
        matchFields = SkillService.MatchFields,
        matchTypes = SkillService.MatchTypes,
        knownSkills = await _skills.KnownSkillsAsync(ct),
    });

    /// <summary>Replaces the whole rule set.</summary>
    [HttpPut("rules")]
    public async Task<IActionResult> ReplaceRules([FromBody] List<SkillRuleDto> rules, CancellationToken ct) =>
        Ok(await _skills.ReplaceRulesAsync(rules ?? new(), CurrentUserId, ct));

    [HttpGet("agents/{userId:guid}")]
    public async Task<IActionResult> GetAgentSkills(Guid userId, CancellationToken ct) => Ok(await _skills.GetAgentSkillsAsync(userId, ct));

    /// <summary>Replaces one agent's skills.</summary>
    [HttpPut("agents/{userId:guid}")]
    public async Task<IActionResult> ReplaceAgentSkills(Guid userId, [FromBody] List<AgentSkillDto> skills, CancellationToken ct) =>
        Ok(await _skills.ReplaceAgentSkillsAsync(userId, skills ?? new(), CurrentUserId, ct));
}
