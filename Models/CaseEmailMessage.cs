namespace CaseManagement.Api.Models;

/// <summary>
/// An email sent to the customer from a case (Customer Reply): the outbox row and the record of what was sent. Written in the same
/// save as the reply's timeline entry, then sent; a failure is retried, and its reason kept. Its Message-ID threads the customer's
/// answer back into the case.
/// </summary>
public class CaseEmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Case? Case { get; set; }
    /// <summary>The Customer Reply entry on the timeline this email carries.</summary>
    public Guid? CaseEventId { get; set; }
    public CaseEvent? CaseEvent { get; set; }
    /// <summary>The mailbox it is sent from (SMTP). Kept when the mailbox is deleted later, as null.</summary>
    public Guid? MailboxId { get; set; }
    public EmailMailbox? Mailbox { get; set; }

    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;

    /// <summary>A calendar invite (iCalendar text) sent with the email, for a meeting invitation, update or cancellation.</summary>
    public string? CalendarIcs { get; set; }
    /// <summary>The invite's method: REQUEST (add or update) or CANCEL.</summary>
    public string? CalendarMethod { get; set; }
    /// <summary>Our Message-ID (normalised, no angle brackets): a reply to it threads into the case.</summary>
    public string MessageId { get; set; } = string.Empty;
    public string? InReplyTo { get; set; }
    /// <summary>The thread's message ids, space-separated.</summary>
    public string? References { get; set; }

    /// <summary><see cref="CaseEmailStatuses"/>.</summary>
    public string Status { get; set; } = CaseEmailStatuses.Pending;
    public string? Error { get; set; }
    public int Attempts { get; set; }
    /// <summary>When it may be tried (again): its lease while sending, its retry time after a failure; null once done.</summary>
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public Guid? SentById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Bumped on every claim, so two servers never send the same email.</summary>
    public int Version { get; set; }
}

public static class CaseEmailStatuses
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    /// <summary>Not sent. Retried automatically until the attempts run out (then <see cref="CaseEmailMessage.NextAttemptAt"/> is null).</summary>
    public const string Failed = "Failed";
}
