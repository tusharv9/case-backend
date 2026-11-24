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

    // ---- Organisational hierarchy (Escalation Maintenance revamp) ----

    /// <summary>
    /// The Host roles that sit at this level. A role belongs to one level only. Empty on a matrix saved before the revamp:
    /// then <see cref="TargetRole"/> (one role, by name) is the level's role.
    /// </summary>
    public List<EscalationLevelRole> Roles { get; set; } = new();

    /// <summary>
    /// The initial handlers' level (e.g. Agents): where a case starts, never a target, and without a trigger. Only the lowest
    /// level may be it. A matrix without one starts every case below its Level 1.
    /// </summary>
    public bool IsInitial { get; set; }

    /// <summary><see cref="EscalationRouting"/>: who at this level receives a case escalated to it.</summary>
    public string RoutingStrategy { get; set; } = EscalationRouting.Team;

    /// <summary><see cref="EscalationFallbacks"/>: what happens when <see cref="RoutingStrategy"/> finds nobody.</summary>
    public string FallbackPolicy { get; set; } = EscalationFallbacks.NextLevel;

    /// <summary>Automatic escalation hands the case to the person chosen when exactly one is (otherwise it only notifies).</summary>
    public bool AssignOnAuto { get; set; } = true;

    /// <summary>People may escalate to this level by hand.</summary>
    public bool ManualAllowed { get; set; } = true;

    /// <summary>Automatic escalation applies only to cases of these priorities (comma separated); empty = every priority.</summary>
    public string? TriggerPriorities { get; set; }
}

/// <summary>One Host role placed at an escalation level. The role is kept by its Host id, so renaming it never empties the level.</summary>
public class EscalationLevelRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LevelId { get; set; }
    public EscalationLevelConfig? Level { get; set; }
    /// <summary>The Host role's id (its name when Case Management runs without the Host).</summary>
    public string RoleKey { get; set; } = string.Empty;
    /// <summary>The role's name when it was saved; the Host's current name is shown when it can be read.</summary>
    public string RoleName { get; set; } = string.Empty;
}

/// <summary>Who at a level receives a case escalated to it.</summary>
public static class EscalationRouting
{
    /// <summary>The level's role holders who lead or belong to the case's team, else to its department's teams.</summary>
    public const string Team = "Team";
    /// <summary>Every active holder of the level's roles.</summary>
    public const string AllHolders = "AllHolders";
    /// <summary>One holder: the one holding the fewest open cases, chosen from the case's team first.</summary>
    public const string OneHolder = "OneHolder";
    /// <summary>One named person, who must hold one of the level's roles.</summary>
    public const string SpecificUser = "SpecificUser";

    public static readonly IReadOnlyList<string> All = [Team, AllHolders, OneHolder, SpecificUser];
    public static string? Canonical(string? v) => All.FirstOrDefault(x => string.Equals(x, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>What happens when a level's routing finds nobody. Never an unrelated person.</summary>
public static class EscalationFallbacks
{
    /// <summary>Try the level above (and so on).</summary>
    public const string NextLevel = "NextLevel";
    /// <summary>The OmniConnect administrators.</summary>
    public const string Administrators = "Administrators";
    /// <summary>Reach the level without a recipient: the case stays with its holder, and the timeline says why.</summary>
    public const string Hold = "Hold";

    public static readonly IReadOnlyList<string> All = [NextLevel, Administrators, Hold];
    public static string? Canonical(string? v) => All.FirstOrDefault(x => string.Equals(x, v?.Trim(), StringComparison.OrdinalIgnoreCase));
}
