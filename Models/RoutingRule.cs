namespace CaseManagement.Api.Models;

public class RoutingRule : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int EvaluationOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    
    // JSON criteria for matching:
    // { "matchType": "ALL", "caseType": "...", "channel": "...", "priority": "...", "customerSegment": "...", "keywords": [...] }
    public string ConditionsJson { get; set; } = "{}";
    
    public Guid TargetTeamId { get; set; }
    public Team TargetTeam { get; set; } = null!;
    
    public string ActionDescription { get; set; } = string.Empty;
}
