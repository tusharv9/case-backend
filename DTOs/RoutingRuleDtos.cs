namespace CaseManagement.Api.DTOs;

public class RoutingRuleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int EvaluationOrder { get; set; }
    public bool IsActive { get; set; }
    public string ConditionsJson { get; set; } = "{}";
    public RuleConditionsDto Conditions { get; set; } = new();
    public Guid TargetDepartmentId { get; set; }
    public string TargetDepartmentName { get; set; } = string.Empty;
    public string ActionDescription { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class RuleConditionsDto
{
    public string MatchType { get; set; } = "ALL"; // "ALL" or "ANY"
    public string? Department { get; set; } // Department name or code
    public string? Category { get; set; } // Sub-category
    public string? CaseType { get; set; } // "Complaint", "Inquiry", "Service"
    public string? Priority { get; set; } // "Critical", "High", "Medium", "Low"
    public string? CustomerSegment { get; set; } // "Premier", "Mass Retail", "Gold", "SME"
    public string? Channel { get; set; } // "Voice", "Email", "WhatsApp"
    public List<string>? Keywords { get; set; } // Ignored: kept only so old stored rules still deserialize
}

public class CreateRoutingRuleDto
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? EvaluationOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public RuleConditionsDto? Conditions { get; set; }
    public Guid TargetDepartmentId { get; set; }
    public string? ActionDescription { get; set; }
}

public class UpdateRoutingRuleDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? EvaluationOrder { get; set; }
    public bool? IsActive { get; set; }
    public RuleConditionsDto? Conditions { get; set; }
    public Guid? TargetDepartmentId { get; set; }
    public string? ActionDescription { get; set; }
}

public class AssignmentConfigDto
{
    public Guid? DepartmentId { get; set; }                // null = the global default
    public string Algorithm { get; set; } = string.Empty;  // RoundRobin | SkillBased | LeastOccupancy
    public int MaxConcurrentCapacity { get; set; }

    /// <summary>True when this team has its own settings; false when it follows the global default.</summary>
    public bool IsTeamOverride { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class UpdateAssignmentConfigDto
{
    public string Algorithm { get; set; } = string.Empty;
    public int? MaxConcurrentCapacity { get; set; }
}

public class ReorderRulesDto
{
    public List<Guid> RuleIds { get; set; } = new();
}

public class RoutingDecisionResult
{
    public Guid TargetDepartmentId { get; set; }
    public string TargetDepartmentName { get; set; } = string.Empty;
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
    public Guid? MatchedRuleId { get; set; }
    public string? MatchedRuleName { get; set; }
    public string AlgorithmUsed { get; set; } = string.Empty;
    public string RoutingLogMessage { get; set; } = string.Empty;

    /// <summary>Set when no agent could take the case: why, and who is holding it meanwhile (the team lead, if the team has one).</summary>
    public string? HeldReason { get; set; }
    public Guid? HeldByUserId { get; set; }
}

public class SkillRuleDto
{
    public Guid? Id { get; set; }
    public string SkillName { get; set; } = string.Empty;
    public string MatchField { get; set; } = "Title";
    public string MatchType { get; set; } = "Contains";
    public string MatchValue { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class AgentSkillDto
{
    public string SkillName { get; set; } = string.Empty;
    public int ProficiencyLevel { get; set; } = 1;
}

/// <summary>What a routing rule can look at, and the values that are valid for each — so editors never hard-code option lists.</summary>
public class RoutingVocabularyDto
{
    public List<string> MatchTypes { get; set; } = new() { "ALL", "ANY" };
    public List<NamedOptionDto> Departments { get; set; } = new();
    public List<string> SubCategories { get; set; } = new();
    public List<string> CaseTypes { get; set; } = new();
    public List<string> Priorities { get; set; } = new();
    public List<string> Channels { get; set; } = new();
    public List<string> CustomerSegments { get; set; } = new();
    public List<string> Algorithms { get; set; } = new();
}

public class NamedOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
