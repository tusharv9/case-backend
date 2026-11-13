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
    public int AttachmentCount { get; set; }
    public string AckStatus { get; set; } = string.Empty;
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
    public string AckStatus { get; set; } = string.Empty;
    public DateTime? AckSentAt { get; set; }
    public string? AckError { get; set; }
    public bool HasOriginal { get; set; }
    /// <summary>"Check again" is offered: the email is not verified, or failed.</summary>
    public bool CanRecheck { get; set; }
}

public sealed class InboxCountsDto
{
    public int All { get; set; }
    public int CaseCreated { get; set; }
    public int NotVerified { get; set; }
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
