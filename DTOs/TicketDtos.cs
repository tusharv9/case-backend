namespace CaseManagement.Api.DTOs;

public sealed class TicketListItemDto
{
    public Guid Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string RequesterName { get; set; } = string.Empty;
    public string RequesterEmail { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public Guid MailboxId { get; set; }
    public string MailboxName { get; set; } = string.Empty;
    public string MailboxAddress { get; set; } = string.Empty;
    public string? Priority { get; set; }
    public string SenderVerification { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public int AttachmentCount { get; set; }
    public int ReplyCount { get; set; }
    public Guid? CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public string? CaseOwnerName { get; set; }
}

public sealed class TicketCountsDto
{
    public int New { get; set; }
    public int Converted { get; set; }
    public int Closed { get; set; }
    public int All { get; set; }
}

public sealed class TicketMessageDto
{
    public Guid EmailId { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public int AttachmentCount { get; set; }
}

public sealed class TicketAttachmentDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class TicketDetailDto
{
    public Guid Id { get; set; }
    public string TicketNumber { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public Guid MailboxId { get; set; }
    public string MailboxName { get; set; } = string.Empty;
    public string MailboxAddress { get; set; } = string.Empty;
    public Guid? InboundEmailId { get; set; }
    public string RequesterName { get; set; } = string.Empty;
    public string RequesterEmail { get; set; } = string.Empty;
    public string? RequesterPhone { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Priority { get; set; }
    public string? CaseType { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? SubCategory { get; set; }
    public string? PreferredLanguage { get; set; }
    public string SenderVerification { get; set; } = string.Empty;
    public string? SenderNote { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? CloseReason { get; set; }
    public Guid? CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public string? CaseOwnerName { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? CreatedByName { get; set; }
    public string? HandledByName { get; set; }
    public DateTime? HandledAt { get; set; }
    public int Version { get; set; }
    /// <summary>A Case Management customer with the requester's email, offered as the case's customer.</summary>
    public Guid? MatchedCustomerId { get; set; }
    public List<TicketAttachmentDto> Attachments { get; set; } = new();
    /// <summary>Replies received on the ticket's email thread, oldest first (the first email is the ticket itself).</summary>
    public List<TicketMessageDto> Replies { get; set; } = new();
}

/// <summary>Raise a ticket by hand. Multipart: these fields plus any number of <c>files</c>.</summary>
public sealed class CreateTicketDto
{
    public Guid MailboxId { get; set; }
    public string RequesterName { get; set; } = string.Empty;
    public string RequesterEmail { get; set; } = string.Empty;
    public string? RequesterPhone { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Priority { get; set; }
    public string? CaseType { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? SubCategory { get; set; }
}

/// <summary>
/// Assign a ticket: it becomes a case with these details. The case fields are exactly Create Case's (same validation, same
/// configured rules); <see cref="OwnerId"/> null lets the team's assignment rule pick the agent.
/// </summary>
public sealed class AssignTicketDto
{
    public CreateCaseDto Case { get; set; } = new();
    public Guid? OwnerId { get; set; }
    /// <summary>No customer chosen: create a Case Management customer from the requester's name, email and phone.</summary>
    public bool CreateCustomerFromRequester { get; set; }
    /// <summary>The ticket version the person was looking at; a ticket changed since is refused (409).</summary>
    public int Version { get; set; }
}

public sealed class CloseTicketDto
{
    public string Reason { get; set; } = string.Empty;
    public int Version { get; set; }
}

/// <summary>A mailbox the caller may file tickets in / filter by (no connection details).</summary>
public sealed record AccessibleMailboxDto(Guid Id, string Name, string Address,
    Guid? DefaultDepartmentId, string? DefaultCaseType, string? DefaultSubCategory, string? DefaultPriority, string? DefaultPreferredLanguage);

/// <summary>Who could take a ticket's case in a department's team, as the manual Assign &amp; Reassign rules judge them.</summary>
public sealed record TicketAssigneeDto(Guid Id, string Name, string? Role, string TeamName, bool IsTeamLead, bool IsCrossTeam,
    bool IsEligible, string? Reason, string Status, int OpenCases, int Capacity);
