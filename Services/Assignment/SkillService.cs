namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public interface ISkillService
{
    /// <summary>The skills a case needs, from the active skill rules.</summary>
    Task<IReadOnlyList<string>> RequiredSkillsAsync(Case c, Customer? customer, CancellationToken ct = default);

    Task<IReadOnlyList<SkillRuleDto>> GetRulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SkillRuleDto>> ReplaceRulesAsync(IReadOnlyList<SkillRuleDto> rules, Guid actingUserId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentSkillDto>> GetAgentSkillsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentSkillDto>> ReplaceAgentSkillsAsync(Guid userId, IReadOnlyList<AgentSkillDto> skills, Guid actingUserId, CancellationToken ct = default);

    /// <summary>Every skill name in use (rules and agents), so editors can offer them.</summary>
    Task<IReadOnlyList<string>> KnownSkillsAsync(CancellationToken ct = default);
}

public class SkillService : ISkillService
{
    public static readonly IReadOnlyList<string> MatchFields = new[] { "Title", "Description", "CaseType", "Channel", "Subcategory", "Priority", "CustomerSegment" };
    public static readonly IReadOnlyList<string> MatchTypes = new[] { "Contains", "Equals" };

    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;

    public SkillService(AppDbContext context, IConfigCache cache)
    {
        _context = context;
        _cache = cache;
    }

    private Task<List<SkillRule>> ActiveRulesAsync(CancellationToken ct) =>
        _cache.GetOrCreateAsync("skill-rules", () => _context.SkillRules.AsNoTracking().Where(r => r.IsActive).ToListAsync(ct));

    private static string? FieldValue(string field, Case c, Customer? customer) => field switch
    {
        "Title" => c.Title,
        "Description" => c.Description,
        "CaseType" => c.CaseType,
        "Channel" => string.IsNullOrWhiteSpace(c.SourceChannel) ? c.CommunicationChannel : c.SourceChannel,
        "Subcategory" => c.Subcategory,
        "Priority" => c.Severity,
        "CustomerSegment" => customer?.CustomerSegment,
        _ => null,
    };

    public async Task<IReadOnlyList<string>> RequiredSkillsAsync(Case c, Customer? customer, CancellationToken ct = default)
    {
        var required = new List<string>();
        foreach (var rule in await ActiveRulesAsync(ct))
        {
            var value = FieldValue(rule.MatchField, c, customer);
            if (string.IsNullOrWhiteSpace(value)) continue;

            var hit = string.Equals(rule.MatchType, "Equals", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(value.Trim(), rule.MatchValue.Trim(), StringComparison.OrdinalIgnoreCase)
                : value.Contains(rule.MatchValue.Trim(), StringComparison.OrdinalIgnoreCase);

            if (hit && !required.Contains(rule.SkillName, StringComparer.OrdinalIgnoreCase)) required.Add(rule.SkillName);
        }
        return required;
    }

    public async Task<IReadOnlyList<SkillRuleDto>> GetRulesAsync(CancellationToken ct = default) =>
        await _context.SkillRules.AsNoTracking().OrderBy(r => r.SkillName).ThenBy(r => r.MatchField).ThenBy(r => r.MatchValue)
            .Select(r => new SkillRuleDto { Id = r.Id, SkillName = r.SkillName, MatchField = r.MatchField, MatchType = r.MatchType, MatchValue = r.MatchValue, IsActive = r.IsActive })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SkillRuleDto>> ReplaceRulesAsync(IReadOnlyList<SkillRuleDto> rules, Guid actingUserId, CancellationToken ct = default)
    {
        foreach (var r in rules)
        {
            if (string.IsNullOrWhiteSpace(r.SkillName)) throw new InvalidOperationException("Every skill rule needs a skill name.");
            if (string.IsNullOrWhiteSpace(r.MatchValue)) throw new InvalidOperationException($"The rule for '{r.SkillName}' needs a value to look for.");
            if (!MatchFields.Contains(r.MatchField, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{r.MatchField}' is not something a skill rule can look at. Choose one of: {string.Join(", ", MatchFields)}.");
            if (!MatchTypes.Contains(r.MatchType, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{r.MatchType}' is not a match type. Choose Contains or Equals.");
        }

        var existing = await _context.SkillRules.ToListAsync(ct);
        _context.SkillRules.RemoveRange(existing);
        var now = DateTime.UtcNow;
        _context.SkillRules.AddRange(rules.Select(r => new SkillRule
        {
            Id = Guid.NewGuid(),
            SkillName = r.SkillName.Trim(),
            MatchField = MatchFields.First(f => string.Equals(f, r.MatchField, StringComparison.OrdinalIgnoreCase)),
            MatchType = MatchTypes.First(t => string.Equals(t, r.MatchType, StringComparison.OrdinalIgnoreCase)),
            MatchValue = r.MatchValue.Trim(),
            IsActive = r.IsActive,
            CreatedAt = now,
        }));
        _context.CaseEvents.Add(Audit("SKILL_RULES_UPDATED", "Skill rules", $"Replaced skill rules ({rules.Count} rules).", actingUserId));
        await _context.SaveChangesAsync(ct);
        return await GetRulesAsync(ct);
    }

    public async Task<IReadOnlyList<AgentSkillDto>> GetAgentSkillsAsync(Guid userId, CancellationToken ct = default) =>
        await _context.AgentSkills.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.SkillName)
            .Select(s => new AgentSkillDto { SkillName = s.SkillName, ProficiencyLevel = s.ProficiencyLevel })
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AgentSkillDto>> ReplaceAgentSkillsAsync(Guid userId, IReadOnlyList<AgentSkillDto> skills, Guid actingUserId, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new KeyNotFoundException("User not found.");
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.SkillName)) throw new InvalidOperationException("A skill needs a name.");
            if (s.ProficiencyLevel is < 1 or > 5) throw new InvalidOperationException($"Proficiency for '{s.SkillName}' must be between 1 and 5.");
        }
        var distinct = skills.GroupBy(s => s.SkillName.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();

        var existing = await _context.AgentSkills.Where(s => s.UserId == userId).ToListAsync(ct);
        _context.AgentSkills.RemoveRange(existing);
        await _context.SaveChangesAsync(ct);   // unique (user, skill): clear before re-adding

        var now = DateTime.UtcNow;
        _context.AgentSkills.AddRange(distinct.Select(s => new AgentSkill
        {
            Id = Guid.NewGuid(), UserId = userId, SkillName = s.SkillName.Trim(), ProficiencyLevel = s.ProficiencyLevel, CreatedAt = now,
        }));
        _context.CaseEvents.Add(Audit("AGENT_SKILLS_UPDATED", user.Name, $"Updated skills of '{user.Name}': {string.Join(", ", distinct.Select(s => s.SkillName))}.", actingUserId));
        await _context.SaveChangesAsync(ct);
        return await GetAgentSkillsAsync(userId, ct);
    }

    public async Task<IReadOnlyList<string>> KnownSkillsAsync(CancellationToken ct = default)
    {
        var fromAgents = await _context.AgentSkills.AsNoTracking().Select(s => s.SkillName).Distinct().ToListAsync(ct);
        var fromRules = await _context.SkillRules.AsNoTracking().Select(r => r.SkillName).Distinct().ToListAsync(ct);
        return fromAgents.Concat(fromRules).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
    }

    private static CaseEvent Audit(string action, string entity, string message, Guid userId) => new()
    {
        Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, Message = message, CreatedAt = DateTime.UtcNow,
        UserId = userId, Module = "RoutingEngine", EntityName = entity, ActionType = action,
    };
}
