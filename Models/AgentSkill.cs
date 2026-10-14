namespace CaseManagement.Api.Models;

public class AgentSkill : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    
    public string SkillName { get; set; } = string.Empty; // e.g. "Fraud", "Social", "Loans", "Priority Support"
    public int ProficiencyLevel { get; set; } = 1; // 1 = Standard, 2 = Senior, 3 = Specialist
}
