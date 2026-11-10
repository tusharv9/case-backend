namespace CaseManagement.Api.Models;

/// <summary>
/// What has been said about one case's SLA breach in its current SLA cycle: when its owner was first told, and how many
/// reminders followed. Notifications themselves live in OmniConnect's central store; this row is only the memory the SLA
/// monitor needs to remind on a cooldown and stop after the configured number of reminders.
/// </summary>
public class CaseSlaAlert
{
    public Guid CaseId { get; set; }
    public Case? Case { get; set; }

    /// <summary>The SLA cycle this row is about (the case's SLA start time). A new cycle starts the count again.</summary>
    public DateTime CycleStart { get; set; }

    public DateTime NotifiedAt { get; set; }
    public int Reminders { get; set; }
    public DateTime? LastReminderAt { get; set; }
}
