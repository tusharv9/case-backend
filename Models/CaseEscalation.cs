namespace CaseManagement.Api.Models;

/// <summary>Whether a person or the SLA monitor escalated the case.</summary>
public static class EscalationTypes
{
    public const string Automatic = "Automatic";
    public const string Manual = "Manual";
}

/// <summary>
/// One escalation of a case: immutable history, written in the same transaction as the case's change. The timeline entry
/// (<see cref="CaseEventId"/>) is what people read; this row is what reports and the escalation history read.
/// Unique per case, SLA period (<see cref="Cycle"/>) and level: the same level is never reached twice in one period.
/// </summary>
public class CaseEscalation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Case? Case { get; set; }
    public int Cycle { get; set; }
    public int FromLevel { get; set; }
    public int ToLevel { get; set; }
    public string ToLevelName { get; set; } = string.Empty;
    /// <summary><see cref="EscalationTypes"/>.</summary>
    public string Type { get; set; } = EscalationTypes.Manual;
    /// <summary>What fired an automatic escalation ("SLA 80% consumed"); null for a manual one.</summary>
    public string? Trigger { get; set; }
    public decimal? SlaConsumedPercent { get; set; }
    public string ReasonText { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public Guid? FromOwnerId { get; set; }
    /// <summary>Who holds the case afterwards when it was handed over; null when the escalation only notified.</summary>
    public Guid? ToOwnerId { get; set; }
    /// <summary>The people it was escalated to (comma-separated local user ids), who are also the people notified.</summary>
    public string RecipientIds { get; set; } = string.Empty;
    public string RecipientNames { get; set; } = string.Empty;
    /// <summary>Set when the level's routing found nobody and its fallback decided instead.</summary>
    public string? FallbackNote { get; set; }
    /// <summary>The person who escalated; null for the SLA monitor.</summary>
    public Guid? ActorUserId { get; set; }
    public Guid? CaseEventId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
