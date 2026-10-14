namespace CaseManagement.Api.Models;

/// <summary>
/// "When a case looks like THIS, it needs skill X." Skill-based assignment uses these rules to decide which skills a case
/// requires, instead of keywords baked into the code. Agents are given skills separately (<see cref="AgentSkill"/>).
/// </summary>
public class SkillRule : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The skill a matching case needs (matched, case-insensitively, against <see cref="AgentSkill.SkillName"/>).</summary>
    public string SkillName { get; set; } = string.Empty;

    /// <summary>Which part of the case is examined: Title | Description | CaseType | Channel | Subcategory | Priority | CustomerSegment.</summary>
    public string MatchField { get; set; } = "Title";

    /// <summary>Contains | Equals</summary>
    public string MatchType { get; set; } = "Contains";

    public string MatchValue { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
