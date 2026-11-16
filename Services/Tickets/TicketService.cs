namespace CaseManagement.Api.Services.Tickets;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.EmailIntake;
using CaseManagement.Api.Services.Notifications;
using CaseManagement.Api.Services.Teams;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

/// <summary>A ticket that was assigned or closed by someone else, or changed since it was opened (answered 409).</summary>
public sealed class TicketConflictException(string message) : Exception(message);

/// <summary>
/// Tickets: requests waiting for a person to assign them. Reading is scoped by mailbox (<see cref="IMailboxAccessScope"/>);
/// assigning creates the case through Create Case itself (<see cref="ICaseService.CreateCaseForTicketAsync"/>), so a ticket's
/// case passes exactly the same configured rules as a case typed in by hand.
/// </summary>
/// <remarks>
/// Assigning happens once: the ticket's version is checked in the same transaction that stores the case, so two people pressing
/// Assign together make one case, and the second is told who was faster. The ticket keeps the request as it arrived; the case
/// gets its own copy (title, description, files), as a case made from an email always has.
/// </remarks>
public sealed class TicketService(
    AppDbContext db,
    IMailboxAccessScope access,
    ICaseService cases,
    IAttachmentService attachments,
    IAttachmentStore store,
    IAssignmentEligibilityService eligibility,
    ILiveEvents live,
    ICaseNotificationSink notifications,
    ILogger<TicketService> logger)
{
    public const int MaxFilesPerTicket = 10;
    public const long MaxFileBytes = 25 * 1024 * 1024;

    private IQueryable<Ticket> Visible()
    {
        var query = db.Tickets.AsNoTracking();
        if (access.SeesAll) return query;
        var mailboxes = access.VisibleMailboxIds();
        return query.Where(t => mailboxes.Contains(t.MailboxId));
    }

    // ───────────────────────────── reading ─────────────────────────────

    public async Task<PagedResponseDto<TicketListItemDto>> ListAsync(string? status, string? search, Guid? mailboxId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = Visible();
        query = (status ?? "new").ToLowerInvariant() switch
        {
            "all" => query,
            "converted" => query.Where(t => t.Status == TicketStatuses.Converted),
            "closed" => query.Where(t => t.Status == TicketStatuses.Closed),
            _ => query.Where(t => t.Status == TicketStatuses.New),
        };
        if (mailboxId is { } mb) query = query.Where(t => t.MailboxId == mb);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(t => t.TicketNumber.ToLower().Contains(s) || t.Subject.ToLower().Contains(s) || t.RequesterEmail.Contains(s)
                || t.RequesterName.ToLower().Contains(s) || (t.Case != null && t.Case.CaseNumber.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        // New tickets oldest first (the longest wait at the top); handled ones newest first.
        var ordered = string.Equals(status, "new", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(status)
            ? query.OrderBy(t => t.ReceivedAt) : query.OrderByDescending(t => t.ReceivedAt);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new TicketListItemDto
            {
                Id = t.Id, TicketNumber = t.TicketNumber, Source = t.Source, RequesterName = t.RequesterName, RequesterEmail = t.RequesterEmail,
                Subject = t.Subject, Snippet = t.Description.Length > 160 ? t.Description.Substring(0, 160) : t.Description,
                MailboxId = t.MailboxId, MailboxName = t.Mailbox!.Name, MailboxAddress = t.Mailbox.Address, Priority = t.Priority,
                SenderVerification = t.SenderVerification, Status = t.Status, ReceivedAt = t.ReceivedAt, AttachmentCount = t.Attachments.Count,
                ReplyCount = db.InboundEmails.Count(e => e.TicketId == t.Id && e.Status == EmailStatuses.LinkedToTicket),
                CaseId = t.CaseId, CaseNumber = t.Case != null ? t.Case.CaseNumber : null, CaseOwnerName = t.Case != null ? t.Case.Owner.Name : null,
            }).ToListAsync(ct);
        return new PagedResponseDto<TicketListItemDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public async Task<TicketCountsDto> CountsAsync(Guid? mailboxId, CancellationToken ct)
    {
        var query = Visible();
        if (mailboxId is { } mb) query = query.Where(t => t.MailboxId == mb);
        var byStatus = await query.GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(string s) => byStatus.Where(x => x.Key == s).Sum(x => x.Count);
        return new TicketCountsDto { New = Of(TicketStatuses.New), Converted = Of(TicketStatuses.Converted), Closed = Of(TicketStatuses.Closed), All = byStatus.Sum(x => x.Count) };
    }

    public async Task<TicketDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var t = await Visible().Include(x => x.Mailbox).Include(x => x.Attachments).Include(x => x.Case).ThenInclude(c => c!.Owner)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Ticket not found.");
        var names = await db.Users.AsNoTracking().Where(u => u.Id == t.CreatedById || u.Id == t.HandledById)
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var replies = await db.InboundEmails.AsNoTracking()
            .Where(e => e.TicketId == t.Id && e.Status == EmailStatuses.LinkedToTicket).OrderBy(e => e.ReceivedAt)
            .Select(e => new TicketMessageDto
            {
                EmailId = e.Id, FromAddress = e.FromAddress, FromName = e.FromName, Subject = e.Subject, BodyText = e.BodyText,
                ReceivedAt = e.ReceivedAt, AttachmentCount = e.AttachmentCount,
            }).ToListAsync(ct);
        var email = t.RequesterEmail.ToLower();
        var customer = email.Length == 0 ? null : await db.Customers.AsNoTracking().Where(c => c.Email != null && c.Email.ToLower() == email)
            .OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        return new TicketDetailDto
        {
            Id = t.Id, TicketNumber = t.TicketNumber, Source = t.Source, MailboxId = t.MailboxId, MailboxName = t.Mailbox?.Name ?? "",
            MailboxAddress = t.Mailbox?.Address ?? "", InboundEmailId = t.InboundEmailId, RequesterName = t.RequesterName, RequesterEmail = t.RequesterEmail,
            RequesterPhone = t.RequesterPhone, Subject = t.Subject, Description = t.Description, Priority = t.Priority, CaseType = t.CaseType,
            DepartmentId = t.DepartmentId, SubCategory = t.SubCategory, PreferredLanguage = t.Mailbox?.DefaultPreferredLanguage,
            SenderVerification = t.SenderVerification, SenderNote = t.SenderNote, Status = t.Status, CloseReason = t.CloseReason,
            CaseId = t.CaseId, CaseNumber = t.Case?.CaseNumber, CaseOwnerName = t.Case?.Owner?.Name, ReceivedAt = t.ReceivedAt,
            CreatedByName = names.GetValueOrDefault(t.CreatedById), HandledByName = t.HandledById is { } h ? names.GetValueOrDefault(h) : null,
            HandledAt = t.HandledAt, Version = t.Version, MatchedCustomerId = customer,
            Attachments = t.Attachments.OrderBy(a => a.CreatedAt).Select(a => new TicketAttachmentDto { Id = a.Id, FileName = a.FileName, ContentType = a.ContentType, SizeBytes = a.SizeBytes }).ToList(),
            Replies = replies,
        };
    }

    public async Task<(Stream Content, string FileName, string ContentType)> OpenAttachmentAsync(Guid ticketId, Guid attachmentId, CancellationToken ct)
    {
        if (!await Visible().AnyAsync(t => t.Id == ticketId, ct)) throw new KeyNotFoundException("Ticket not found.");
        var a = await db.TicketAttachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == attachmentId && x.TicketId == ticketId, ct)
                ?? throw new KeyNotFoundException("File not found.");
        return (await store.OpenReadAsync(a.StorageKey, ct), a.FileName, a.ContentType);
    }

    /// <summary>Who could take the case of a department's team: the manual Assign &amp; Reassign rules' verdict for each person.</summary>
    public async Task<List<TicketAssigneeDto>> AssigneesAsync(Guid departmentId, CancellationToken ct)
    {
        var department = await db.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId && d.IsActive, ct)
                         ?? throw new KeyNotFoundException("Department not found.");
        var team = await DepartmentTeams.TeamOfAsync(db, departmentId, ct);
        if (team is not { IsActive: true }) return new();
        var policy = await eligibility.GetManualPolicyAsync(ct);
        var result = await eligibility.EvaluateForCaseAsync(team.Id, policy, ct);
        return result.Candidates
            .OrderByDescending(c => c.IsEligible).ThenBy(c => c.IsCrossTeam).ThenBy(c => c.User.Name)
            .Select(c => new TicketAssigneeDto(c.User.Id, c.User.Name, c.MemberRole, c.HomeTeamName, c.IsTeamLead, c.IsCrossTeam,
                c.IsEligible, c.IsEligible ? null : c.Explain(result.Team.Name), c.User.Status.ToString(), c.OpenCases, c.Capacity))
            .ToList();
    }

    // ───────────────────────────── raising by hand ─────────────────────────────

    public async Task<TicketDetailDto> CreateAsync(CreateTicketDto dto, IReadOnlyList<IFormFile> files, Guid actorId, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        var mailbox = await db.EmailMailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == dto.MailboxId, ct);
        if (mailbox == null || !await access.CanSeeMailboxAsync(mailbox.Id, ct)) errors.Add(new("mailboxId", "Choose the mailbox (queue) the ticket belongs to."));
        if (string.IsNullOrWhiteSpace(dto.RequesterName)) errors.Add(new("requesterName", "Enter who the request is from."));
        else if (dto.RequesterName.Trim().Length > 200) errors.Add(new("requesterName", "At most 200 characters."));
        var email = dto.RequesterEmail?.Trim().ToLowerInvariant() ?? "";
        if (email.Length > 0 && !IsEmail(email)) errors.Add(new("requesterEmail", "Enter a valid email address, or leave it empty."));
        if (email.Length == 0 && string.IsNullOrWhiteSpace(dto.RequesterPhone)) errors.Add(new("requesterEmail", "Enter the requester's email or phone, so they can be contacted."));
        if (dto.RequesterPhone?.Trim().Length > 50) errors.Add(new("requesterPhone", "At most 50 characters."));
        if (string.IsNullOrWhiteSpace(dto.Subject)) errors.Add(new("subject", "Enter a subject."));
        else if (dto.Subject.Trim().Length > 500) errors.Add(new("subject", "At most 500 characters."));
        if (string.IsNullOrWhiteSpace(dto.Description)) errors.Add(new("description", "Describe the request."));
        else if (dto.Description.Trim().Length > 20000) errors.Add(new("description", "At most 20,000 characters."));
        if (dto.DepartmentId is { } dept && !await db.Departments.AsNoTracking().AnyAsync(d => d.Id == dept && d.IsActive, ct))
            errors.Add(new("departmentId", "Choose an active department, or leave it empty."));
        if (files.Count > MaxFilesPerTicket) errors.Add(new("files", $"At most {MaxFilesPerTicket} files."));
        if (files.Any(f => f.Length > MaxFileBytes)) errors.Add(new("files", $"Each file can be at most {MaxFileBytes / (1024 * 1024)} MB."));
        if (errors.Count > 0) throw new FieldValidationException(errors);

        var ticket = new Ticket
        {
            Source = TicketSources.Manual, MailboxId = mailbox!.Id,
            RequesterName = dto.RequesterName.Trim(), RequesterEmail = email, RequesterPhone = Blank(dto.RequesterPhone),
            Subject = dto.Subject.Trim(), Description = dto.Description.Trim(),
            Priority = Blank(dto.Priority) ?? mailbox.DefaultPriority, CaseType = Blank(dto.CaseType) ?? mailbox.DefaultCaseType,
            DepartmentId = dto.DepartmentId ?? mailbox.DefaultDepartmentId, SubCategory = Blank(dto.SubCategory) ?? mailbox.DefaultSubCategory,
            SenderVerification = SenderVerification.Verified, SenderNote = "Raised by hand in Case Management.",
            ReceivedAt = DateTime.UtcNow, CreatedById = actorId, CreatedAt = DateTime.UtcNow,
        };
        foreach (var file in files.Where(f => f.Length > 0))
        {
            await using var content = file.OpenReadStream();
            var key = await store.SaveAsync(content, Path.GetExtension(file.FileName), ct);
            ticket.Attachments.Add(new TicketAttachment
            {
                FileName = Path.GetFileName(file.FileName), ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                SizeBytes = file.Length, StorageKey = key,
            });
        }
        await TicketNumbers.AddAsync(db, ticket, ct);
        Publish(ticket);
        await TicketAlerts.NotifyReceivedAsync(db, notifications, ticket, mailbox.Name, logger, ct);
        return await GetAsync(ticket.Id, ct);
    }

    // ───────────────────────────── assigning ─────────────────────────────

    public async Task<TicketDetailDto> AssignAsync(Guid id, AssignTicketDto dto, Guid actorId, bool callerMayAssignAnyCase, CancellationToken ct)
    {
        var ticket = await Visible().Include(t => t.Attachments).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException("Ticket not found.");
        await EnsureOpenAsync(ticket, dto.Version, ct);

        var caseDto = dto.Case;
        caseDto.Title = string.IsNullOrWhiteSpace(caseDto.Title) ? ticket.Subject : caseDto.Title;
        caseDto.Description = string.IsNullOrWhiteSpace(caseDto.Description) ? ticket.Description : caseDto.Description;
        if (ticket.Source == TicketSources.Email)
        {
            caseDto.SourceChannel = string.IsNullOrWhiteSpace(caseDto.SourceChannel) ? "Email" : caseDto.SourceChannel;
            caseDto.CommunicationChannel ??= "Email";
        }
        if (caseDto.CustomerId == null && caseDto.Customer360 == null)
        {
            if (!dto.CreateCustomerFromRequester)
                throw new FieldValidationException([new FieldError("selectCustomer", "Choose the customer, or use the requester as a new customer.")]);
            caseDto.CustomerId = await CustomerFromRequesterAsync(ticket, caseDto.PreferredLanguage, ct);
        }
        // Automatic assignment from a ticket must not fail on an empty team: the lead holds it, as for an email's case.
        caseDto.UnstaffedTeamAction ??= UnstaffedTeamActions.AssignToSupervisor;

        Case created;
        try
        {
            created = await cases.CreateCaseForTicketAsync(caseDto, actorId, dto.OwnerId, ticket.ReceivedAt, callerMayAssignAnyCase, async newCase =>
            {
                var tracked = await db.Tickets.FirstAsync(t => t.Id == ticket.Id, ct);
                if (tracked.Status != TicketStatuses.New || tracked.Version != dto.Version)
                    throw new TicketConflictException("This ticket was just assigned or closed by someone else.");
                tracked.Status = TicketStatuses.Converted;
                tracked.CaseId = newCase.Id;
                tracked.HandledById = actorId;
                tracked.HandledAt = DateTime.UtcNow;
                tracked.Version++;
                tracked.UpdatedAt = DateTime.UtcNow;

                // The ticket's emails now belong to the case: later replies thread onto it, and the Inbox shows its number.
                var emails = await db.InboundEmails.Where(e => e.TicketId == ticket.Id).OrderBy(e => e.ReceivedAt).ToListAsync(ct);
                foreach (var e in emails) e.CaseId = newCase.Id;
                db.CaseEvents.Add(new CaseEvent
                {
                    CaseId = newCase.Id, EventType = EventType.Other, Channel = ticket.Source == TicketSources.Email ? "Email" : null, IsInternal = true,
                    CreatedAt = DateTime.UtcNow, UserId = actorId, Module = "Tickets", ActionType = "TICKET_CONVERTED",
                    Message = $"Created from ticket {ticket.TicketNumber} ({(ticket.Source == TicketSources.Email ? "email" : "raised by hand")} from {Requester(ticket)}).",
                });
                foreach (var reply in emails.Where(e => e.Status == EmailStatuses.LinkedToTicket))
                    db.CaseEvents.Add(new CaseEvent
                    {
                        CaseId = newCase.Id, EventType = EventType.Comment, Channel = "Email", IsInternal = false, CreatedAt = reply.ReceivedAt, UserId = actorId,
                        Module = "EmailIntake", ActionType = "EMAIL_REPLY_RECEIVED",
                        Message = $"Email received from {(string.IsNullOrWhiteSpace(reply.FromName) ? reply.FromAddress : $"{reply.FromName} <{reply.FromAddress}>")}: {reply.Subject}\n\n{EmailParsing.Truncate(reply.BodyText, 4000)}",
                    });
                await db.SaveChangesAsync(ct);
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new TicketConflictException("This ticket was just assigned or closed by someone else.");
        }

        // The case is saved; its files follow through the case's own checks. A refused file is named on the case, never lost.
        await CopyAttachmentsAsync(ticket, created.Id, actorId);
        Publish(ticket, TicketStatuses.Converted);
        return await GetAsync(ticket.Id, ct);
    }

    public async Task<TicketDetailDto> CloseAsync(Guid id, CloseTicketDto dto, Guid actorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Reason)) throw new FieldValidationException([new FieldError("reason", "Say why the ticket is closed without a case.")]);
        if (dto.Reason.Trim().Length > 500) throw new FieldValidationException([new FieldError("reason", "At most 500 characters.")]);
        var visible = await Visible().AnyAsync(t => t.Id == id, ct);
        var ticket = visible ? await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, ct) : null;
        if (ticket == null) throw new KeyNotFoundException("Ticket not found.");
        await EnsureOpenAsync(ticket, dto.Version, ct);
        ticket.Status = TicketStatuses.Closed;
        ticket.CloseReason = dto.Reason.Trim();
        ticket.HandledById = actorId;
        ticket.HandledAt = DateTime.UtcNow;
        ticket.Version++;
        ticket.UpdatedAt = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new TicketConflictException("This ticket was just assigned or closed by someone else."); }
        Publish(ticket);
        return await GetAsync(id, ct);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private async Task EnsureOpenAsync(Ticket ticket, int version, CancellationToken ct)
    {
        if (ticket.Status == TicketStatuses.New && ticket.Version == version) return;
        if (ticket.Status != TicketStatuses.New)
        {
            var by = ticket.HandledById is { } h ? await db.Users.AsNoTracking().Where(u => u.Id == h).Select(u => u.Name).FirstOrDefaultAsync(ct) : null;
            throw new TicketConflictException(ticket.Status == TicketStatuses.Converted
                ? $"This ticket was already assigned{(by != null ? $" by {by}" : "")}."
                : $"This ticket was already closed{(by != null ? $" by {by}" : "")}.");
        }
        throw new TicketConflictException("This ticket changed since you opened it (a reply arrived, or it was edited). Review it and try again.");
    }

    /// <summary>The Case Management customer with the requester's email, or a new one with their name, email and phone.</summary>
    private async Task<Guid> CustomerFromRequesterAsync(Ticket ticket, string? language, CancellationToken ct)
    {
        var email = ticket.RequesterEmail.ToLower();
        if (email.Length > 0 && await db.Customers.AsNoTracking().Where(c => c.Email != null && c.Email.ToLower() == email)
                .OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct) is { } existing)
            return existing;
        var customer = new Customer
        {
            FullName = EmailParsing.Truncate(ticket.RequesterName, 200), Email = email.Length == 0 ? null : email,
            PhoneNumber = ticket.RequesterPhone ?? string.Empty, PreferredLanguage = string.IsNullOrWhiteSpace(language) ? "English" : language,
            CreatedAt = DateTime.UtcNow,
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync(ct);
        return customer.Id;
    }

    private async Task CopyAttachmentsAsync(Ticket ticket, Guid caseId, Guid actorId)
    {
        var refused = new List<string>();
        foreach (var a in ticket.Attachments)
        {
            try
            {
                await using var raw = await store.OpenReadAsync(a.StorageKey, CancellationToken.None);
                using var content = new MemoryStream();
                await raw.CopyToAsync(content);
                content.Position = 0;
                var file = new FormFile(content, 0, content.Length, "file", a.FileName) { Headers = new HeaderDictionary(), ContentType = a.ContentType };
                await attachments.UploadAttachmentAsync(caseId, file, $"From ticket {ticket.TicketNumber}", actorId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                refused.Add($"{a.FileName} ({EmailParsing.Truncate(ex.Message, 150)})");
            }
        }
        if (refused.Count == 0) return;
        try
        {
            db.CaseEvents.Add(new CaseEvent
            {
                CaseId = caseId, EventType = EventType.Other, IsInternal = true, CreatedAt = DateTime.UtcNow, UserId = actorId,
                Module = "Tickets", ActionType = "TICKET_ATTACHMENT_REFUSED",
                Message = $"Not added from ticket {ticket.TicketNumber} (they stay on the ticket): " + string.Join("; ", refused),
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record the files refused from ticket {Ticket}.", ticket.TicketNumber); }
    }

    private void Publish(Ticket t, string? status = null)
    {
        try { live.Publish(new LiveEvent("tickets", new { ticketId = t.Id, mailboxId = t.MailboxId, status = status ?? t.Status })); }
        catch (Exception ex) { logger.LogDebug(ex, "Tickets live update not published."); }
    }

    private static string Requester(Ticket t) => string.IsNullOrWhiteSpace(t.RequesterEmail) ? t.RequesterName : $"{t.RequesterName} <{t.RequesterEmail}>";

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool IsEmail(string value)
    {
        try { return new System.Net.Mail.MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase) && EmailParsing.DomainOf(value).Contains('.'); }
        catch (FormatException) { return false; }
    }
}

/// <summary>"New ticket to assign": told to the leads of the teams linked to the ticket's mailbox (administrators by default too).</summary>
public static class TicketAlerts
{
    public static async Task NotifyReceivedAsync(AppDbContext db, ICaseNotificationSink? sink, Ticket ticket, string mailboxName, ILogger logger, CancellationToken ct)
    {
        if (sink == null) return;
        try
        {
            var leads = await db.EmailMailboxTeams.AsNoTracking().Where(x => x.MailboxId == ticket.MailboxId && x.Team!.IsActive && x.Team.OwnerId != null)
                .Select(x => x.Team!.OwnerId!.Value).Distinct().ToListAsync(ct);
            sink.Enqueue(new CaseNotificationRequest(CaseNotificationKinds.TicketReceived, leads,
                CaseNumber: ticket.TicketNumber, EventKey: $"ticket:{ticket.Id}",
                Values: new Dictionary<string, string?> { ["mailbox"] = mailboxName }, Link: $"tickets/{ticket.Id}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not raise the new-ticket notification for {Ticket}.", ticket.TicketNumber);
        }
    }
}
