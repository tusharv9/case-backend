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

    // Notification (automatic escalation only notifies; it never reassigns). Who is told when the level is reached:
    /// <summary>The people holding <see cref="TargetRole"/> — those of the case's team when there are any, otherwise all of them.</summary>
    public bool NotifyRoleHolders { get; set; } = true;
    /// <summary>The person holding the case.</summary>
    public bool NotifyCaseOwner { get; set; }
    /// <summary>The lead of the team handling the case.</summary>
    public bool NotifyTeamLead { get; set; }
    /// <summary>Priority of the notification: Medium, High or Critical.</summary>
    public string NotificationPriority { get; set; } = "High";

    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}
