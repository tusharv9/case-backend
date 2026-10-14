namespace CaseManagement.Api.Configuration;

/// <summary>How notifications behave. Defaults match what the application did before these were configurable.</summary>
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Minutes before an unresolved breached case is reminded about again.</summary>
    public int BreachReminderCooldownMinutes { get; set; } = 60;

    /// <summary>How many reminders a breached case gets after the first notification.</summary>
    public int BreachMaxReminders { get; set; } = 3;

    /// <summary>When an owner has this many NEWLY breached cases at once, send one summary instead of one each. 0 turns grouping off.</summary>
    public int BreachGroupingThreshold { get; set; } = 3;

    public string BreachPriority { get; set; } = "High";

    /// <summary>Read notifications older than this are deleted.</summary>
    public int ReadRetentionDays { get; set; } = 30;

    /// <summary>Any notification (read or not) older than this is deleted.</summary>
    public int MaxAgeDays { get; set; } = 180;

    /// <summary>An identical un-keyed notification inside this window is treated as a duplicate.</summary>
    public int DuplicateWindowMinutes { get; set; } = 5;
}
