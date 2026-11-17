namespace CaseManagement.Api.DTOs;

public sealed class InboxEmailListItemDto
{
    public Guid Id { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public string MailboxAddress { get; set; } = string.Empty;
    public string Verification { get; set; } = string.Empty;
    public string? OrganisationName { get; set; }
    /// <summary>The listed sender's name (alias), when the address is on the domain's sender list.</summary>
    public string? SenderAlias { get; set; }
    public string Authenticity { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? CaseNumber { get; set; }
    public Guid? CaseId { get; set; }
    public string? TicketNumber { get; set; }
    public Guid? TicketId { get; set; }
    public int AttachmentCount { get; set; }
    public string AckStatus { get; set; } = string.Empty;
    /// <summary>RegisteredSender | KnownDomain | SimilarDomain | Unknown (null on emails from before sender review).</summary>
    public string? MatchKind { get; set; }
    public string? MatchReason { get; set; }
    public DateTime? ReviewStartedAt { get; set; }
    public DateTime? ReviewDueAt { get; set; }
    public DateTime? ReviewEscalatedAt { get; set; }
}

public sealed class InboxEmailDetailDto
{
    public Guid Id { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string? ToList { get; set; }
    public string? CcList { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public long SizeBytes { get; set; }
    public string MailboxName { get; set; } = string.Empty;
    public string MailboxAddress { get; set; } = string.Empty;
    public List<string> Attachments { get; set; } = new();
    public string Kind { get; set; } = string.Empty;
    public string Verification { get; set; } = string.Empty;
    public string? VerificationNote { get; set; }
    public string? OrganisationName { get; set; }
    public string? SenderAlias { get; set; }
    public string Authenticity { get; set; } = string.Empty;
    public string? AuthenticationDetail { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? StatusNote { get; set; }
    public int Attempts { get; set; }
    public string? CaseNumber { get; set; }
    public Guid? CaseId { get; set; }
    public string? TicketNumber { get; set; }
    public Guid? TicketId { get; set; }
    public string AckStatus { get; set; } = string.Empty;
    public DateTime? AckSentAt { get; set; }
    public string? AckError { get; set; }
    public bool HasOriginal { get; set; }
    /// <summary>"Check again" is offered: the email is not verified, failed, waits for review, or was held as suspicious automatically.</summary>
    public bool CanRecheck { get; set; }

    public string? MatchKind { get; set; }
    public string? MatchReason { get; set; }
    public Guid? OrganisationId { get; set; }
    public DateTime? ReviewStartedAt { get; set; }
    public DateTime? ReviewDueAt { get; set; }
    public DateTime? ReviewReminderSentAt { get; set; }
    public DateTime? ReviewEscalatedAt { get; set; }
    public string? ReviewSlaOutcome { get; set; }
    public string? ReviewDecision { get; set; }
    public string? ReviewNote { get; set; }
    public string? ReviewedByName { get; set; }
    public DateTime? ReviewedAt { get; set; }
    /// <summary>The email waits for review: Approve / Reject / Mark as suspicious are offered (to people who may review).</summary>
    public bool CanReview { get; set; }
    /// <summary>The address is not registered yet, so approving can also register it.</summary>
    public bool CanRegisterSender { get; set; }
    /// <summary>The sender writes from a public provider (gmail.com…): an organisation cannot own it, so only this email can be approved.</summary>
    public bool FromPublicProvider { get; set; }
    public Guid MailboxId { get; set; }
    /// <summary>The mailbox's active teams: the teams a sender registered from this email may be routed to.</summary>
    public List<EmailOptionDto> MailboxTeams { get; set; } = new();
    public int Version { get; set; }
}

public sealed class InboxCountsDto
{
    public int All { get; set; }
    public int CaseCreated { get; set; }
    public int TicketCreated { get; set; }
    public int NotVerified { get; set; }
    /// <summary>From registered senders.</summary>
    public int Verified { get; set; }
    /// <summary>Waiting for review: an organisation's unregistered sender, or a look-alike of one.</summary>
    public int PendingVerification { get; set; }
    /// <summary>Waiting for review from an unknown sender, or suspicious / rejected.</summary>
    public int UnknownSuspicious { get; set; }
    public int Failed { get; set; }
    public int Other { get; set; }
}

public sealed class MailboxDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Username { get; set; }
    /// <summary>Whether a password is stored. The password itself is never sent back.</summary>
    public bool HasPassword { get; set; }
    public DateTime? PasswordUpdatedAt { get; set; }
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; }
    public string ImapSecurity { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public string SmtpSecurity { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool SendAcknowledgement { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastSyncAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public int EmailCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<Guid> TeamIds { get; set; } = new();
    public List<string> TeamNames { get; set; } = new();
    public Guid? DefaultDepartmentId { get; set; }
    public string? DefaultCaseType { get; set; }
    public string? DefaultSubCategory { get; set; }
    public string? DefaultPriority { get; set; }
    public string? DefaultPreferredLanguage { get; set; }
    public int ReviewSlaMinutes { get; set; }
    public int ReviewReminderMinutes { get; set; }
    public int ReviewEscalationMinutes { get; set; }
    public string ReviewEscalateTo { get; set; } = string.Empty;
}

public sealed class SaveMailboxDto
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Username { get; set; }
    /// <summary>Leave empty to keep the stored password.</summary>
    public string? Password { get; set; }
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public string ImapSecurity { get; set; } = "SslOnConnect";
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpSecurity { get; set; } = "StartTls";
    public bool IsEnabled { get; set; } = true;
    public bool SendAcknowledgement { get; set; } = true;
    /// <summary>The teams that see this mailbox's emails and tickets. Null keeps the stored teams; empty = only people who see every mailbox see it.</summary>
    public List<Guid>? TeamIds { get; set; }
    public Guid? DefaultDepartmentId { get; set; }
    public string? DefaultCaseType { get; set; }
    public string? DefaultSubCategory { get; set; }
    public string? DefaultPriority { get; set; }
    public string? DefaultPreferredLanguage { get; set; }
    /// <summary>Sender review deadline, reminder and escalation, in working minutes from when an email starts waiting. Null keeps the stored value.</summary>
    public int? ReviewSlaMinutes { get; set; }
    public int? ReviewReminderMinutes { get; set; }
    public int? ReviewEscalationMinutes { get; set; }
    /// <summary>TeamLeads | Administrators. Null keeps the stored value.</summary>
    public string? ReviewEscalateTo { get; set; }
}

public sealed class TestMailboxDto
{
    /// <summary>The saved mailbox whose password is used when <see cref="SaveMailboxDto.Password"/> is empty.</summary>
    public Guid? MailboxId { get; set; }
    public SaveMailboxDto Settings { get; set; } = new();
}

public sealed class MailboxTestResultDto
{
    public bool ImapOk { get; set; }
    public string? ImapError { get; set; }
    public bool SmtpOk { get; set; }
    public string? SmtpError { get; set; }
}

public sealed class VerifiedDomainDto
{
    public Guid Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public Guid OrganisationId { get; set; }
    public string OrganisationName { get; set; } = string.Empty;
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; }
    public string VerificationPolicy { get; set; } = string.Empty;
    public bool RequireAuthentication { get; set; }
    public List<VerifiedSenderDto> Senders { get; set; } = new();
    public int CaseCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class VerifiedSenderDto
{
    public Guid? Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>The mailbox this sender writes to (optional).</summary>
    public Guid? MailboxId { get; set; }
    /// <summary>The team its tickets are suggested to (optional; needs a mailbox that is shared with the team).</summary>
    public Guid? TeamId { get; set; }

    // Read only: what the ids are, for the lists.
    public string? MailboxName { get; set; }
    public string? TeamName { get; set; }
    public Guid? VerifiedDomainId { get; set; }
    public string? Domain { get; set; }
    public Guid? OrganisationId { get; set; }
    public string? OrganisationName { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Registers one sender directly (Sender Verification → Senders), without opening its domain first. The domain is taken from the
/// address: an existing registered domain must belong to the organisation named; an unregistered one is added for it, verifying
/// only the listed senders.
/// </summary>
public sealed class SaveSenderDto
{
    public string Address { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>An existing organisation (matched ignoring case and spacing) or a new one.</summary>
    public string OrganisationName { get; set; } = string.Empty;
    public Guid? MailboxId { get; set; }
    public Guid? TeamId { get; set; }
}

public sealed class EmailOrganisationDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DomainCount { get; set; }
}

public sealed class SaveVerifiedDomainDto
{
    public string Domain { get; set; } = string.Empty;
    /// <summary>The organisation's name: an existing organisation (matched ignoring case and spacing) or a new one.</summary>
    public string OrganisationName { get; set; } = string.Empty;
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>EntireDomain | SpecificSenders. Empty means EntireDomain.</summary>
    public string? VerificationPolicy { get; set; }
    public bool RequireAuthentication { get; set; }
    /// <summary>The whole sender list (replaced as a set). Kept, though not used, while the policy is "entire domain".</summary>
    public List<VerifiedSenderDto> Senders { get; set; } = new();
}

/// <summary>What the mailbox form offers for its tickets: the active case types, departments (with their sub-categories), priorities, languages and teams.</summary>
public sealed class EmailCaseOptionsDto
{
    public List<OptionDto> CaseTypes { get; set; } = new();
    public List<DepartmentOptionDto> Departments { get; set; } = new();
    public List<string> Priorities { get; set; } = new();
    public List<string> Languages { get; set; } = new();

    public sealed record OptionDto(string Value, string Label);
    public sealed record DepartmentOptionDto(Guid Id, string Name, bool HasTeam, List<string> SubCategories);
    /// <summary>The active teams a mailbox can be linked to.</summary>
    public List<OptionDto> Teams { get; set; } = new();
}

/// <summary>A reviewer's decision on an email from a sender who is not registered.</summary>
public sealed class ReviewEmailDto
{
    /// <summary>approve | reject | suspicious.</summary>
    public string Decision { get; set; } = string.Empty;
    /// <summary>Required for reject and suspicious: why.</summary>
    public string? Note { get; set; }
    /// <summary>The email's version when the reviewer opened it.</summary>
    public int Version { get; set; }
    /// <summary>Also register the sender permanently (approve only). Needs <see cref="ConfirmRegistration"/>.</summary>
    public ReviewRegisterSenderDto? Register { get; set; }
    /// <summary>The reviewer's separate, explicit confirmation of the permanent registration.</summary>
    public bool ConfirmRegistration { get; set; }
}

/// <summary>The sender's details when a reviewer registers them; the address is the email's own.</summary>
public sealed class ReviewRegisterSenderDto
{
    public string DisplayName { get; set; } = string.Empty;
    public string OrganisationName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? MailboxId { get; set; }
    public Guid? TeamId { get; set; }
}

public sealed record EmailOptionDto(Guid Id, string Name);
