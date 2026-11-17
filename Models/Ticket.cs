namespace CaseManagement.Api.Models;

/// <summary>
/// A request waiting to be triaged: an email that arrived in a Tickets-mode mailbox, or one raised by hand. A person who may
/// assign tickets chooses the team, the agent and whatever case details the request does not supply, and the ticket becomes
/// a normal <see cref="Case"/> (<see cref="CaseId"/>). Until then it holds only what is known; nothing is guessed.
/// </summary>
/// <remarks>
/// The mailbox is the ticket's queue: only the teams linked to it (<see cref="EmailMailboxTeam"/>) see it. A ticket is never
/// deleted; it ends <see cref="TicketStatuses.Converted"/> (with its case) or <see cref="TicketStatuses.Closed"/> (with a reason).
/// </remarks>
public class Ticket : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Unique, increasing; the number people see is <see cref="TicketNumber"/>.</summary>
    public int Sequence { get; set; }
    /// <summary>T-00001. Unique.</summary>
    public string TicketNumber { get; set; } = string.Empty;
    /// <summary><see cref="TicketSources"/>.</summary>
    public string Source { get; set; } = TicketSources.Email;

    public Guid MailboxId { get; set; }
    public EmailMailbox? Mailbox { get; set; }
    /// <summary>The email that opened it (Source = Email).</summary>
    public Guid? InboundEmailId { get; set; }

    public string RequesterName { get; set; } = string.Empty;
    public string RequesterEmail { get; set; } = string.Empty;
    public string? RequesterPhone { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // Optional classification, suggested by whoever raised it (or the mailbox defaults); the case takes what the assigner confirms.
    public string? Priority { get; set; }
    public string? CaseType { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? SubCategory { get; set; }

    /// <summary>The registered sender who wrote it, and their organisation: the case made from it is for that organisation.</summary>
    public Guid? OrganisationId { get; set; }
    public EmailOrganisation? Organisation { get; set; }
    public Guid? VerifiedSenderId { get; set; }
    public EmailVerifiedSender? VerifiedSender { get; set; }

    /// <summary><see cref="SenderVerification"/> of the email's sender, shown as a badge; it never blocks a ticket.</summary>
    public string SenderVerification { get; set; } = Models.SenderVerification.Pending;
    public string? SenderNote { get; set; }

    /// <summary><see cref="TicketStatuses"/>.</summary>
    public string Status { get; set; } = TicketStatuses.New;
    public string? CloseReason { get; set; }
    public Guid? CaseId { get; set; }
    public Case? Case { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedById { get; set; }
    public Guid? HandledById { get; set; }
    public DateTime? HandledAt { get; set; }

    /// <summary>Bumped on every change, so two people can never convert (or close) the same ticket.</summary>
    public int Version { get; set; }

    // The ticket's own SLA: how long it may wait to be assigned (Services/Tickets/TicketSla.cs). It is the queue's clock; the
    // case made from the ticket starts its own SLA when it is created, so time spent waiting here is never charged to a team.

    /// <summary>
    /// Working minutes allowed between arrival and assignment, taken from the priority (or the default) when the ticket arrived,
    /// so a later configuration change never rewrites it. 0 = no target. Null only on tickets from before ticket SLAs, until the
    /// monitor fills it in.
    /// </summary>
    public int? AssignTargetMinutes { get; set; }
    /// <summary>When the ticket must be assigned by (working time from arrival). Null when there is no target.</summary>
    public DateTime? AssignDueAt { get; set; }
    /// <summary>When the target was missed (the due time), recorded by the monitor.</summary>
    public DateTime? SlaBreachedAt { get; set; }
    /// <summary>True once the "not assigned in time" alert has been sent, so it is sent once.</summary>
    public bool SlaBreachNotified { get; set; }
    /// <summary>Working minutes the ticket waited before it was assigned or closed.</summary>
    public double? AssignedWithinMinutes { get; set; }
    /// <summary>"Met" or "Breached", set when the ticket is assigned or closed (null without a target).</summary>
    public string? SlaOutcome { get; set; }

    public List<TicketAttachment> Attachments { get; set; } = new();
}

/// <summary>A file that came with a ticket (from its email, or uploaded when it was raised). Copied to the case on conversion.</summary>
public class TicketAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    /// <summary>The file in the attachment store.</summary>
    public string StorageKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class TicketStatuses
{
    public const string New = "New";
    public const string Converted = "Converted";
    public const string Closed = "Closed";
}

public static class TicketSources
{
    public const string Email = "Email";
    public const string Manual = "Manual";
}
