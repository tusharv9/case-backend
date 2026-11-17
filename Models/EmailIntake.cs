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

    /// <summary>Who added and last changed the mailbox (Case Management user ids; plain values, so removing a user never blocks).</summary>
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>
    /// No longer read: every new email opens a ticket (there is no automatic case any more). Kept only because the column
    /// exists in the database; always <see cref="MailboxIntakeModes.Tickets"/>.
    /// </summary>
    public string IntakeMode { get; set; } = MailboxIntakeModes.Tickets;

    // What the Assign drawer starts from for this mailbox's tickets. All optional; the person assigning can change each one.
    public Guid? DefaultDepartmentId { get; set; }
    public string? DefaultCaseType { get; set; }
    public string? DefaultSubCategory { get; set; }
    public string? DefaultPriority { get; set; }
    public string? DefaultPreferredLanguage { get; set; }

    /// <summary>The teams whose members see this mailbox's emails and tickets (<see cref="HostIntegration.MailboxAccessScope"/>).</summary>
    public List<EmailMailboxTeam> Teams { get; set; } = new();

    // ── Sender review deadline: how long an email from an unregistered sender may wait for a decision, in working minutes from
    //    when it started waiting. Each waiting email takes these when it starts waiting, so a later change never rewrites it. ──

    /// <summary>The review deadline. 0 = no deadline (no reminder or escalation either).</summary>
    public int ReviewSlaMinutes { get; set; } = 60;
    /// <summary>When to remind the mailbox's teams, before the deadline. 0 = no reminder.</summary>
    public int ReviewReminderMinutes { get; set; } = 30;
    /// <summary>When an undecided email is escalated: at or after the deadline.</summary>
    public int ReviewEscalationMinutes { get; set; } = 60;
    /// <summary><see cref="ReviewEscalationTargets"/>: who is told when an email is escalated.</summary>
    public string ReviewEscalateTo { get; set; } = ReviewEscalationTargets.TeamLeads;
}

/// <summary>Who an overdue review is escalated to.</summary>
public static class ReviewEscalationTargets
{
    /// <summary>The leads of the mailbox's teams (the administrators when it has none).</summary>
    public const string TeamLeads = "TeamLeads";
    /// <summary>The administrators (as set for the notification under Settings → Manage Notifications).</summary>
    public const string Administrators = "Administrators";
    public static readonly string[] All = [TeamLeads, Administrators];
}

/// <summary>A team allowed to see a mailbox: its active members and its lead see that mailbox's emails and tickets.</summary>
public class EmailMailboxTeam
{
    public Guid MailboxId { get; set; }
    public EmailMailbox? Mailbox { get; set; }
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
}

public static class MailboxIntakeModes
{
    public const string Tickets = "Tickets";
    public const string AutoCase = "AutoCase";
    public static readonly string[] All = [Tickets, AutoCase];
}

/// <summary>
/// An organisation that writes to us — e.g. BSN. One record however many domains it has, so its name is typed once and an email
/// from any of its domains is filed under the same organisation.
/// </summary>
public class EmailOrganisation : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>The name lower-cased and trimmed: unique, so "BSN" and "bsn " can never be two organisations.</summary>
    public string NameKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    public static string KeyOf(string name) => System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ").ToLowerInvariant();
}

/// <summary>
/// A sender domain whose emails open verified tickets. The domain identifies the organisation; its <see cref="VerificationPolicy"/> decides which
/// of its senders are verified — everyone, or only the listed <see cref="Senders"/>.
/// </summary>
public class EmailVerifiedDomain : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>e.g. sbi.co.in — lower-case, unique.</summary>
    public string Domain { get; set; } = string.Empty;
    public Guid OrganisationId { get; set; }
    public EmailOrganisation? Organisation { get; set; }
    /// <summary>The organisation's name when it was last saved. Kept (and written) for one release as the fallback for a rollback; read <see cref="Organisation"/>.</summary>
    public string OrganisationName { get; set; } = string.Empty;
    /// <summary><see cref="VerificationPolicies"/>: every sender at the domain, or only the listed senders.</summary>
    public string VerificationPolicy { get; set; } = VerificationPolicies.EntireDomain;
    /// <summary>Only emails that carry a valid signature of the domain (DKIM, aligned with the sender) are verified.</summary>
    public bool RequireAuthentication { get; set; }
    public List<EmailVerifiedSender> Senders { get; set; } = new();
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    /// <summary>Also accept it.sbi.co.in and other sub-domains.</summary>
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A named sender at a verified domain, e.g. tushar@bsn.com.in shown as "Tushar". Under the "specific senders" policy only these are verified.</summary>
public class EmailVerifiedSender : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VerifiedDomainId { get; set; }
    public EmailVerifiedDomain? VerifiedDomain { get; set; }
    /// <summary>Lower-case. Unique across every domain: one address is one identity.</summary>
    public string Address { get; set; } = string.Empty;
    /// <summary>The name the sender is shown by (the alias), preferred over the name in the email's From line.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>
    /// The mailbox this sender writes to. Optional; a hint for routing, never a permission: the sender's email stays in the mailbox
    /// it actually arrived at, and is only flagged when that is another one.
    /// </summary>
    public Guid? MailboxId { get; set; }
    public EmailMailbox? Mailbox { get; set; }
    /// <summary>
    /// The team this sender's tickets are suggested to. Needs <see cref="MailboxId"/>, and must be one of that mailbox's teams
    /// (<see cref="EmailMailboxTeam"/>): a sender can never route work to a team the mailbox is not shared with.
    /// </summary>
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
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
    /// <summary>The organisation the sender's domain belongs to — set whenever the domain is known, verified or not.</summary>
    public Guid? OrganisationId { get; set; }
    public EmailOrganisation? Organisation { get; set; }
    /// <summary>The listed sender the address matched, if any.</summary>
    public Guid? VerifiedSenderId { get; set; }
    public EmailVerifiedSender? VerifiedSender { get; set; }
    /// <summary>The listed sender's name when the email was processed, so renaming the sender later does not rewrite this email.</summary>
    public string? SenderAlias { get; set; }
    /// <summary><see cref="EmailAuthenticity"/>: whether the email proves it came from its domain — kept apart from <see cref="Verification"/>.</summary>
    public string Authenticity { get; set; } = EmailAuthenticity.NotChecked;
    /// <summary>What the check found, e.g. "dkim=pass d=bsn.com.in; dmarc=reject".</summary>
    public string? AuthenticationDetail { get; set; }

    /// <summary><see cref="EmailStatuses"/>.</summary>
    public string Status { get; set; } = EmailStatuses.Received;
    public string? StatusNote { get; set; }
    public Guid? CaseId { get; set; }
    public Case? Case { get; set; }
    /// <summary>The ticket this email opened, or the ticket whose thread it replies to.</summary>
    public Guid? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    /// <summary>Bumped on every claim, so two workers (or a worker and a person) can never process the same email at once.</summary>
    public int Version { get; set; }

    // ── Sender review: an email from anyone who is not a registered sender waits here instead of opening a ticket. ──

    /// <summary><see cref="SenderMatchKinds"/>: how the sender relates to the registered organisations.</summary>
    public string? MatchKind { get; set; }
    /// <summary>Why it was classified so, in words a reviewer can check, e.g. "looks like bsn.example (Cyrillic 'а' for 'a')".</summary>
    public string? MatchReason { get; set; }
    /// <summary>When the email started waiting for review (the review SLA counts from here).</summary>
    public DateTime? ReviewStartedAt { get; set; }
    /// <summary>The mailbox's deadline when it started waiting (0 = none); with the times below, frozen so a settings change never rewrites it.</summary>
    public int? ReviewSlaMinutes { get; set; }
    public DateTime? ReviewDueAt { get; set; }
    public DateTime? ReviewReminderAt { get; set; }
    public DateTime? ReviewEscalateAt { get; set; }
    /// <summary>Set once each was sent, so each is sent once.</summary>
    public DateTime? ReviewReminderSentAt { get; set; }
    public DateTime? ReviewEscalatedAt { get; set; }
    /// <summary>"Met" or "Breached", when the decision was made (null without a deadline, or while waiting).</summary>
    public string? ReviewSlaOutcome { get; set; }
    /// <summary><see cref="ReviewDecisions"/>; null while it waits.</summary>
    public string? ReviewDecision { get; set; }
    public string? ReviewNote { get; set; }
    public Guid? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }

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
    /// <summary>The domain is verified, but under "specific senders" this address is not on its list.</summary>
    public const string NotListed = "NotListed";
}

public static class VerificationPolicies
{
    public const string EntireDomain = "EntireDomain";
    public const string SpecificSenders = "SpecificSenders";
    public static readonly string[] All = { EntireDomain, SpecificSenders };
}

/// <summary>Whether an email proves it was sent by its domain. A domain match alone proves nothing: anyone can write any From line.</summary>
public static class EmailAuthenticity
{
    /// <summary>Not checked: the sender's domain is not a verified one, or the email was processed before checks existed.</summary>
    public const string NotChecked = "NotChecked";
    /// <summary>A valid DKIM signature of the sender's domain (aligned with the From address).</summary>
    public const string Pass = "Pass";
    /// <summary>No valid signature of the domain, and the domain does not ask for such mail to be refused.</summary>
    public const string None = "None";
    /// <summary>Forged or altered: a broken signature, or the domain's DMARC policy refuses mail without its valid signature.</summary>
    public const string Fail = "Fail";
    /// <summary>The check could not be completed (DNS unavailable).</summary>
    public const string Unknown = "Unknown";
}

public static class EmailStatuses
{
    public const string Received = "Received";
    /// <summary>Being worked on; <see cref="InboundEmail.NextAttemptAt"/> is the lease, after which it is picked up again.</summary>
    public const string Processing = "Processing";
    public const string CaseCreated = "CaseCreated";
    /// <summary>A reply on an email thread that already has a case: added to that case, no new case.</summary>
    public const string LinkedToCase = "LinkedToCase";
    /// <summary>The mailbox is in Tickets mode: the email opened a ticket that waits for a person to assign it.</summary>
    public const string TicketCreated = "TicketCreated";
    /// <summary>A reply on a ticket's thread before the ticket became a case: added to the ticket.</summary>
    public const string LinkedToTicket = "LinkedToTicket";
    /// <summary>Not from a registered sender (known organisation, look-alike or unknown): waits for a person, no ticket yet.</summary>
    public const string PendingReview = "PendingReview";
    /// <summary>A reviewer refused it: never becomes a ticket.</summary>
    public const string Rejected = "Rejected";
    /// <summary>Forged (the domain's signature check failed) or marked suspicious by a reviewer: never becomes a ticket.</summary>
    public const string Suspicious = "Suspicious";
    /// <summary>Before sender review existed: a ticket was opened and marked not verified. Kept for old rows.</summary>
    public const string NotVerified = "NotVerified";
    public const string Ignored = "Ignored";
    public const string Failed = "Failed";
}

/// <summary>How an email's sender relates to the registered organisations. Only <see cref="RegisteredSender"/> is verification.</summary>
public static class SenderMatchKinds
{
    /// <summary>The address is a registered, active sender (and the email is not forged).</summary>
    public const string RegisteredSender = "RegisteredSender";
    /// <summary>The domain is an organisation's registered domain, but this person is not registered.</summary>
    public const string KnownDomain = "KnownDomain";
    /// <summary>The domain resembles a registered one (look-alike characters, a typo, another ending). A hint for the reviewer, never trust.</summary>
    public const string SimilarDomain = "SimilarDomain";
    /// <summary>Nothing matches.</summary>
    public const string Unknown = "Unknown";
}

public static class ReviewDecisions
{
    /// <summary>This one email is released into Tickets. The sender stays unregistered unless registered in the same decision.</summary>
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Suspicious = "Suspicious";
}

public static class AcknowledgementStatuses
{
    public const string NotSent = "NotSent";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}
