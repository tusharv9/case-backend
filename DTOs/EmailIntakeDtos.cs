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
    public string OrganisationName { get; set; } = string.Empty;
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; }
    public string CaseType { get; set; } = string.Empty;
    public Guid DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public string SubCategory { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = string.Empty;
    public int CaseCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class SaveVerifiedDomainDto
{
    public string Domain { get; set; } = string.Empty;
    public string OrganisationName { get; set; } = string.Empty;
    public bool IncludeSubdomains { get; set; }
    public bool IsActive { get; set; } = true;
    public string CaseType { get; set; } = string.Empty;
    public Guid DepartmentId { get; set; }
    public string SubCategory { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = string.Empty;
}

/// <summary>What the Verified Domain form offers: the active case types, departments (with their sub-categories), priorities and languages.</summary>
public sealed class EmailCaseOptionsDto
{
    public List<OptionDto> CaseTypes { get; set; } = new();
    public List<DepartmentOptionDto> Departments { get; set; } = new();
    public List<string> Priorities { get; set; } = new();
    public List<string> Languages { get; set; } = new();

    public sealed record OptionDto(string Value, string Label);
    public sealed record DepartmentOptionDto(Guid Id, string Name, bool HasTeam, List<string> SubCategories);
}
