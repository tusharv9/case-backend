namespace CaseManagement.Api.Models;

/// <summary>
/// A company mailbox Case Management reads (IMAP) and answers from (SMTP). Emails that arrive in it become cases when the
/// sender's domain is verified (<see cref="EmailVerifiedDomain"/>); the sender is then sent an acknowledgement with the case number.
/// </summary>
public class EmailMailbox : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>The address emails are sent to, and acknowledgements are sent from. Unique (lower-case).</summary>
    public string Address { get; set; } = string.Empty;
    /// <summary>The sign-in name; the address when blank.</summary>
    public string? Username { get; set; }
    /// <summary>The mailbox password, encrypted (<see cref="Services.EmailIntake.MailboxSecretProtector"/>). Never returned by the API.</summary>
    public string? PasswordEncrypted { get; set; }
    public DateTime? PasswordUpdatedAt { get; set; }

    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    /// <summary><see cref="MailSecurity"/>.</summary>
    public string ImapSecurity { get; set; } = MailSecurity.SslOnConnect;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpSecurity { get; set; } = MailSecurity.StartTls;

    public bool IsEnabled { get; set; } = true;
    /// <summary>Send the sender an email with the case number when a case is created from their email.</summary>
    public bool SendAcknowledgement { get; set; } = true;

    /// <summary><see cref="MailboxStatuses"/>.</summary>
    public string Status { get; set; } = MailboxStatuses.NotConnected;
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int ConsecutiveFailures { get; set; }
    /// <summary>When the next read may run: pushed back after failures, and held while a read is running (its lease).</summary>
    public DateTime? NextSyncAt { get; set; }
    /// <summary>Bumped when a read is claimed, so two servers sharing the database never read the same mailbox at once.</summary>
    public int Version { get; set; }

    /// <summary>The IMAP folder's UIDVALIDITY when <see cref="LastUid"/> was recorded; a change means the UIDs were renumbered.</summary>
    public long? UidValidity { get; set; }
    /// <summary>The highest IMAP UID already read. Null until the first read, which starts from that moment (old mail is not imported).</summary>
    public long? LastUid { get; set; }
}

/// <summary>
/// A sender domain whose emails become cases by themselves, and the case details an email cannot supply (type, department,
/// sub-category, priority, language). The domain is matched on the sender's address.
/// </summary>
public class EmailVerifiedDomain : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>e.g. sbi.co.in — lower-case, unique.</summary>
    public string Domain { get; set; } = string.Empty;
    /// <summary>Who the domain belongs to, e.g. State Bank of India.</summary>
    public string OrganisationName { get; set; } = string.Empty;
    /// <summary>Also accept it.sbi.co.in and other sub-domains.</summary>
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; } = true;

    public string CaseType { get; set; } = string.Empty;
    public Guid DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string SubCategory { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = string.Empty;
}

/// <summary>One email read from a mailbox, and what was done with it.</summary>
public class InboundEmail : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxId { get; set; }
    public EmailMailbox? Mailbox { get; set; }

    /// <summary>The Message-ID (normalised), or a hash of the headers when there is none. Unique: the same email is never stored twice.</summary>
    public string MessageKey { get; set; } = string.Empty;
    public string? InternetMessageId { get; set; }
    public string? InReplyTo { get; set; }
    /// <summary>The References header's message ids, space-separated.</summary>
    public string? References { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string? ToList { get; set; }
    public string? CcList { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public DateTime? SentAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>The original email (.eml) in the attachment store.</summary>
    public string? RawKey { get; set; }
    /// <summary>The attachment file names, one per line.</summary>
    public string? AttachmentNames { get; set; }
    public int AttachmentCount { get; set; }

    /// <summary><see cref="EmailKinds"/>: auto-replies, bounces and our own mail never become cases.</summary>
    public string Kind { get; set; } = EmailKinds.Normal;
    /// <summary><see cref="SenderVerification"/>.</summary>
    public string Verification { get; set; } = SenderVerification.Pending;
    public string? VerificationNote { get; set; }
    public Guid? VerifiedDomainId { get; set; }
    public EmailVerifiedDomain? VerifiedDomain { get; set; }

    /// <summary><see cref="EmailStatuses"/>.</summary>
    public string Status { get; set; } = EmailStatuses.Received;
    public string? StatusNote { get; set; }
    public Guid? CaseId { get; set; }
    public Case? Case { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    /// <summary>Bumped on every claim, so two workers (or a worker and a person) can never process the same email at once.</summary>
    public int Version { get; set; }

    /// <summary><see cref="AcknowledgementStatuses"/>.</summary>
    public string AckStatus { get; set; } = AcknowledgementStatuses.NotSent;
    public string? AckMessageId { get; set; }
    public DateTime? AckSentAt { get; set; }
    public string? AckError { get; set; }
    public int AckAttempts { get; set; }
    public DateTime? AckNextAttemptAt { get; set; }
}

public static class MailSecurity
{
    public const string SslOnConnect = "SslOnConnect";
    public const string StartTls = "StartTls";
    /// <summary>No "Auto": it silently accepts a plain-text session when a server declines to upgrade, exposing the password.</summary>
    public static readonly string[] All = [SslOnConnect, StartTls];
}

public static class MailboxStatuses
{
    public const string NotConnected = "NotConnected";
    public const string Connected = "Connected";
    public const string Failing = "Failing";
}

public static class EmailKinds
{
    public const string Normal = "Normal";
    public const string AutoReply = "AutoReply";
    public const string Bounce = "Bounce";
    /// <summary>Sent by one of our own mailboxes (e.g. an acknowledgement) — never answered, or mail would loop.</summary>
    public const string OwnMailbox = "OwnMailbox";
}

public static class SenderVerification
{
    public const string Pending = "Pending";
    public const string Verified = "Verified";
    public const string NotVerified = "NotVerified";
}

public static class EmailStatuses
{
    public const string Received = "Received";
    /// <summary>Being worked on; <see cref="InboundEmail.NextAttemptAt"/> is the lease, after which it is picked up again.</summary>
    public const string Processing = "Processing";
    public const string CaseCreated = "CaseCreated";
    /// <summary>A reply on an email thread that already has a case: added to that case, no new case.</summary>
    public const string LinkedToCase = "LinkedToCase";
    public const string NotVerified = "NotVerified";
    public const string Ignored = "Ignored";
    public const string Failed = "Failed";
}

public static class AcknowledgementStatuses
{
    public const string NotSent = "NotSent";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}
