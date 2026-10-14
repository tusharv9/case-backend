namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(LevelNumber), IsUnique = true)]
public class EscalationLevelConfig : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int LevelNumber { get; set; } = 1; // 1, 2, 3, 4, ...
    public string Name { get; set; } = string.Empty; // "Level 1", "Level 2", etc.

    // Assignment
    public string AssignmentType { get; set; } = "Role"; // "Role" | "User" | "DepartmentOwner"
    public string TargetRole { get; set; } = "Assigned Agent";
    public Guid? TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    // Trigger
    public string TriggerType { get; set; } = "SlaPercentage"; // "SlaPercentage" | "SlaBreached" | "SlaPostBreachHours" | "ManualOnly"
    public decimal? TriggerValue { get; set; } // e.g. 70, 90, 100, 12
    public string TriggerDescription { get; set; } = string.Empty; // e.g. "SLA 70% consumed", "SLA Breached"

    // Action
    public string ActionDescription { get; set; } = string.Empty; // e.g. "Reminder to assigned agent", "Reassign to team lead"
    public bool ReassignOwner { get; set; } = false;

    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
