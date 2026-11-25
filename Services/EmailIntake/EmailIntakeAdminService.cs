namespace CaseManagement.Api.Services.EmailIntake;

using System.Net.Mail;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;

/// <summary>The Inbox (reading what arrived, and reviewing unregistered senders) and Email Configuration behind the email intake screens.</summary>
public sealed class EmailIntakeAdminService(AppDbContext db, IMailboxSecretProtector secrets, IMailboxClient mail, IAttachmentStore store,
    HostIntegration.IMailboxAccessScope? access = null)
{
    /// <summary>The emails of the mailboxes the caller may see (all of them for background work and tests).</summary>
    private IQueryable<InboundEmail> VisibleEmails()
    {
        var query = db.InboundEmails.AsNoTracking();
        if (access == null || access.SeesAll) return query;
        var visible = access.VisibleMailboxIds();
        return query.Where(e => visible.Contains(e.MailboxId));
    }

    /// <summary>The mailboxes the caller may see, for the Inbox and Tickets filters and the Raise Ticket form (no connection details).</summary>
    public async Task<List<AccessibleMailboxDto>> AccessibleMailboxesAsync(CancellationToken ct)
    {
        var query = db.EmailMailboxes.AsNoTracking();
        if (access != null && !access.SeesAll)
        {
            var visible = access.VisibleMailboxIds();
            query = query.Where(m => visible.Contains(m.Id));
        }
        return await query.OrderBy(m => m.Name)
            .Select(m => new AccessibleMailboxDto(m.Id, m.Name, m.Address, m.DefaultDepartmentId, m.DefaultCaseType, m.DefaultSubCategory,
                m.DefaultPriority, m.DefaultPreferredLanguage))
            .ToListAsync(ct);
    }

    /// <summary>Free email providers: anyone can have an address there, so a whole provider can never be a verified domain.</summary>
    public static readonly HashSet<string> PublicEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.in", "yahoo.co.uk", "ymail.com", "outlook.com", "hotmail.com", "live.com",
        "msn.com", "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com", "gmx.com", "gmx.net", "mail.com",
        "zoho.com", "yandex.com", "yandex.ru", "rediffmail.com", "qq.com", "163.com", "126.com",
    };

    // ───────────────────────────── inbox ─────────────────────────────

    public async Task<PagedResponseDto<InboxEmailListItemDto>> ListAsync(string? filter, string? search, Guid? mailboxId, int page, int pageSize, CancellationToken ct,
        Guid? organisationId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, Paging.MaxPageSize);
        var query = Filter(VisibleEmails(), filter);
        if (mailboxId is { } mb) query = query.Where(e => e.MailboxId == mb);
        if (organisationId is { } org) query = query.Where(e => e.OrganisationId == org);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.Subject.ToLower().Contains(s) || e.FromAddress.Contains(s) || (e.FromName != null && e.FromName.ToLower().Contains(s))
                || (e.SenderAlias != null && e.SenderAlias.ToLower().Contains(s)) || (e.Organisation != null && e.Organisation.NameKey.Contains(s))
                || (e.Case != null && e.Case.CaseNumber.ToLower().Contains(s)) || (e.Ticket != null && e.Ticket.TicketNumber.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new InboxEmailListItemDto
            {
                Id = e.Id, FromAddress = e.FromAddress, FromName = e.FromName, Subject = e.Subject, Snippet = e.Snippet, ReceivedAt = e.ReceivedAt,
                MailboxAddress = e.Mailbox!.Address, Verification = e.Verification,
                OrganisationName = e.Organisation != null ? e.Organisation.Name : e.VerifiedDomain != null ? e.VerifiedDomain.OrganisationName : null,
                SenderAlias = e.SenderAlias, Authenticity = e.Authenticity,
                Status = e.Status, CaseNumber = e.Case != null ? e.Case.CaseNumber : null, CaseId = e.CaseId, AttachmentCount = e.AttachmentCount, AckStatus = e.AckStatus,
                TicketId = e.TicketId, TicketNumber = e.Ticket != null ? e.Ticket.TicketNumber : null,
                MatchKind = e.MatchKind, MatchReason = e.MatchReason, ReviewStartedAt = e.ReviewStartedAt,
                ReviewDueAt = e.ReviewDueAt, ReviewEscalatedAt = e.ReviewEscalatedAt,
            }).ToListAsync(ct);
        return new PagedResponseDto<InboxEmailListItemDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    /// <remarks>
    /// The tabs say where an email is now, not what intake first did with it. An email that opened a ticket keeps the status
    /// TicketCreated for good (reply threading matches on it); once that ticket is assigned, its emails get the case's id, and from
    /// then on they belong under Case Created.
    /// </remarks>
    public static IQueryable<InboundEmail> Filter(IQueryable<InboundEmail> query, string? filter) => (filter ?? "all").ToLowerInvariant() switch
    {
        "case-created" => query.Where(e => e.Status == EmailStatuses.CaseCreated || e.Status == EmailStatuses.LinkedToCase
            || ((e.Status == EmailStatuses.TicketCreated || e.Status == EmailStatuses.LinkedToTicket) && e.CaseId != null)),
        "ticket-created" => query.Where(e => (e.Status == EmailStatuses.TicketCreated || e.Status == EmailStatuses.LinkedToTicket) && e.CaseId == null),
        "verified" => query.Where(e => e.Verification == SenderVerification.Verified),
        // Sender review: an organisation's unregistered sender (or a look-alike of one), and the unknown and the suspicious.
        "pending-verification" => query.Where(e => e.Status == EmailStatuses.PendingReview
            && (e.MatchKind == SenderMatchKinds.KnownDomain || e.MatchKind == SenderMatchKinds.SimilarDomain)),
        "unknown-suspicious" => query.Where(e => (e.Status == EmailStatuses.PendingReview && e.MatchKind != SenderMatchKinds.KnownDomain && e.MatchKind != SenderMatchKinds.SimilarDomain)
            || e.Status == EmailStatuses.Suspicious || e.Status == EmailStatuses.Rejected),
        "not-verified" => query.Where(e => e.Status == EmailStatuses.NotVerified),
        "failed" => query.Where(e => e.Status == EmailStatuses.Failed),
        "other" => query.Where(e => e.Status == EmailStatuses.Ignored || e.Status == EmailStatuses.Received || e.Status == EmailStatuses.Processing
            || e.Status == EmailStatuses.NotVerified),
        _ => query,
    };

    public async Task<InboxCountsDto> CountsAsync(Guid? mailboxId, CancellationToken ct, Guid? organisationId = null)
    {
        var query = VisibleEmails();
        if (mailboxId is { } mb) query = query.Where(e => e.MailboxId == mb);
        if (organisationId is { } org) query = query.Where(e => e.OrganisationId == org);
        var byStatus = await query.GroupBy(e => new { e.Status, HasCase = e.CaseId != null })
            .Select(g => new { g.Key.Status, g.Key.HasCase, Count = g.Count() }).ToListAsync(ct);
        int Of(params string[] s) => byStatus.Where(x => s.Contains(x.Status)).Sum(x => x.Count);
        // As Filter: a ticket's emails move to Case Created once the ticket became a case.
        var ticketEmails = byStatus.Where(x => x.Status is EmailStatuses.TicketCreated or EmailStatuses.LinkedToTicket).ToList();
        var ticketEmailsNowInACase = ticketEmails.Where(x => x.HasCase).Sum(x => x.Count);
        return new InboxCountsDto
        {
            All = byStatus.Sum(x => x.Count),
            CaseCreated = Of(EmailStatuses.CaseCreated, EmailStatuses.LinkedToCase) + ticketEmailsNowInACase,
            TicketCreated = ticketEmails.Sum(x => x.Count) - ticketEmailsNowInACase,
            NotVerified = Of(EmailStatuses.NotVerified),
            Verified = await Filter(query, "verified").CountAsync(ct),
            PendingVerification = await Filter(query, "pending-verification").CountAsync(ct),
            UnknownSuspicious = await Filter(query, "unknown-suspicious").CountAsync(ct),
            Failed = Of(EmailStatuses.Failed),
            Other = Of(EmailStatuses.Ignored, EmailStatuses.Received, EmailStatuses.Processing, EmailStatuses.NotVerified),
        };
    }

    public async Task<InboxEmailDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var e = await VisibleEmails().Include(x => x.Mailbox).Include(x => x.VerifiedDomain).Include(x => x.Organisation).Include(x => x.Case).Include(x => x.Ticket)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Email not found.");
        return new InboxEmailDetailDto
        {
            Id = e.Id, FromAddress = e.FromAddress, FromName = e.FromName, ToList = e.ToList, CcList = e.CcList, Subject = e.Subject, BodyText = e.BodyText,
            ReceivedAt = e.ReceivedAt, SentAt = e.SentAt, SizeBytes = e.SizeBytes, MailboxName = e.Mailbox?.Name ?? "", MailboxAddress = e.Mailbox?.Address ?? "",
            Attachments = (e.AttachmentNames ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Kind = e.Kind, Verification = e.Verification, VerificationNote = e.VerificationNote, OrganisationName = e.Organisation?.Name ?? e.VerifiedDomain?.OrganisationName,
            SenderAlias = e.SenderAlias, Authenticity = e.Authenticity, AuthenticationDetail = e.AuthenticationDetail,
            Status = e.Status, StatusNote = e.StatusNote, Attempts = e.Attempts, CaseNumber = e.Case?.CaseNumber, CaseId = e.CaseId,
            TicketId = e.TicketId, TicketNumber = e.Ticket?.TicketNumber,
            AckStatus = e.AckStatus, AckSentAt = e.AckSentAt, AckError = e.AckError, HasOriginal = e.RawKey != null,
            CanRecheck = e.RawKey != null && (e.Status is EmailStatuses.NotVerified or EmailStatuses.Failed or EmailStatuses.PendingReview
                || (e.Status == EmailStatuses.Suspicious && e.ReviewDecision == null)),
            MatchKind = e.MatchKind, MatchReason = e.MatchReason, OrganisationId = e.OrganisationId, ReviewStartedAt = e.ReviewStartedAt,
            ReviewDueAt = e.ReviewDueAt, ReviewReminderSentAt = e.ReviewReminderSentAt, ReviewEscalatedAt = e.ReviewEscalatedAt, ReviewSlaOutcome = e.ReviewSlaOutcome,
            ReviewDecision = e.ReviewDecision, ReviewNote = e.ReviewNote, ReviewedAt = e.ReviewedAt,
            ReviewedByName = e.ReviewedById == null ? null : await db.Users.AsNoTracking().Where(u => u.Id == e.ReviewedById).Select(u => u.Name).FirstOrDefaultAsync(ct),
            CanReview = e.Status == EmailStatuses.PendingReview,
            FromPublicProvider = PublicEmailDomains.Contains(EmailParsing.DomainOf(e.FromAddress)),
            CanRegisterSender = e.Status == EmailStatuses.PendingReview && !PublicEmailDomains.Contains(EmailParsing.DomainOf(e.FromAddress))
                && !await db.EmailVerifiedSenders.AsNoTracking().AnyAsync(s => s.Address == e.FromAddress, ct),
            Version = e.Version,
            MailboxId = e.MailboxId,
            MailboxTeams = await db.EmailMailboxTeams.AsNoTracking().Where(t => t.MailboxId == e.MailboxId && t.Team!.IsActive)
                .OrderBy(t => t.Team!.Name).Select(t => new EmailOptionDto(t.TeamId, t.Team!.Name)).ToListAsync(ct),
        };
    }

    public async Task<(Stream Content, string FileName)> OpenOriginalAsync(Guid id, CancellationToken ct)
    {
        var e = await VisibleEmails().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Email not found.");
        if (e.RawKey == null) throw new KeyNotFoundException("The original of this email was not kept (it was too large to read).");
        return (await store.OpenReadAsync(e.RawKey, ct), $"email-{e.ReceivedAt:yyyyMMdd-HHmm}-{e.Id.ToString()[..8]}.eml");
    }

    // ───────────────────────────── mailboxes ─────────────────────────────

    public async Task<List<MailboxDto>> MailboxesAsync(CancellationToken ct)
    {
        var counts = await db.InboundEmails.AsNoTracking().GroupBy(e => e.MailboxId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var list = await db.EmailMailboxes.AsNoTracking().Include(m => m.Teams).ThenInclude(t => t.Team).OrderBy(m => m.Name).ToListAsync(ct);
        return list.Select(m => ToDto(m, counts.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<MailboxDto> CreateMailboxAsync(SaveMailboxDto dto, Guid actingUserId, CancellationToken ct)
    {
        var errors = ValidateMailbox(dto, requirePassword: true);
        var address = dto.Address.Trim().ToLowerInvariant();
        if (await db.EmailMailboxes.AnyAsync(m => m.Address == address, ct)) errors.Add(new FieldError("address", "This mailbox is already configured."));
        errors.AddRange(await ValidateTicketSettingsAsync(dto, ct));
        if (errors.Count > 0) throw new FieldValidationException(errors);

        var mailbox = new EmailMailbox { CreatedAt = DateTime.UtcNow, CreatedByUserId = Actor(actingUserId) };
        Apply(mailbox, dto);
        ApplyTeams(mailbox, dto);
        db.EmailMailboxes.Add(mailbox);
        Audit(actingUserId, "MAILBOX_CREATED", "EmailMailbox", $"Mailbox {mailbox.Address} added.", null, Snapshot(mailbox, passwordChanged: true));
        await db.SaveChangesAsync(ct);
        return await ReloadAsync(mailbox.Id, ct);
    }

    public async Task<MailboxDto> UpdateMailboxAsync(Guid id, SaveMailboxDto dto, Guid actingUserId, CancellationToken ct)
    {
        var mailbox = await db.EmailMailboxes.Include(m => m.Teams).FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("Mailbox not found.");
        var errors = ValidateMailbox(dto, requirePassword: mailbox.PasswordEncrypted == null);
        var address = dto.Address.Trim().ToLowerInvariant();
        if (await db.EmailMailboxes.AnyAsync(m => m.Id != id && m.Address == address, ct)) errors.Add(new FieldError("address", "This mailbox is already configured."));
        errors.AddRange(await ValidateTicketSettingsAsync(dto, ct));
        if (dto.TeamIds != null)
        {
            // A team that senders of this mailbox are routed to cannot lose access silently: name them, so they are changed first.
            var wanted = dto.TeamIds.ToHashSet();
            var stranded = await db.EmailVerifiedSenders.AsNoTracking()
                .Where(s => s.MailboxId == id && s.TeamId != null && !wanted.Contains(s.TeamId.Value))
                .Select(s => new { s.Address, Team = s.Team!.Name }).OrderBy(s => s.Address).ToListAsync(ct);
            if (stranded.Count > 0)
                errors.Add(new("teamIds", $"{string.Join(", ", stranded.Select(s => s.Team).Distinct())} still receive{(stranded.Count == 1 ? "s" : "")} the tickets of "
                    + $"{string.Join(", ", stranded.Take(5).Select(s => s.Address))}{(stranded.Count > 5 ? $" and {stranded.Count - 5} more" : "")}. "
                    + "Change those senders' team under Sender Verification first."));
        }
        if (errors.Count > 0) throw new FieldValidationException(errors);
        ApplyTeams(mailbox, dto);

        // A different mailbox (address, server or sign-in): start reading it from now, not from the old one's position.
        var newUser = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim();
        var differentMailbox = mailbox.Address != address || !string.Equals(mailbox.ImapHost, dto.ImapHost.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mailbox.Username, newUser, StringComparison.OrdinalIgnoreCase);
        var wasEnabled = mailbox.IsEnabled;
        var before = Snapshot(mailbox, passwordChanged: false);
        Apply(mailbox, dto);
        if (differentMailbox) { mailbox.LastUid = null; mailbox.UidValidity = null; }
        if (differentMailbox || !string.IsNullOrEmpty(dto.Password) || (!wasEnabled && mailbox.IsEnabled))
        {
            mailbox.NextSyncAt = null;
            mailbox.ConsecutiveFailures = 0;
            mailbox.Status = MailboxStatuses.NotConnected;
            mailbox.LastError = null;
        }
        mailbox.UpdatedAt = DateTime.UtcNow;
        mailbox.UpdatedByUserId = Actor(actingUserId);
        Audit(actingUserId, "MAILBOX_UPDATED", "EmailMailbox", $"Mailbox {mailbox.Address} changed.", before, Snapshot(mailbox, passwordChanged: !string.IsNullOrEmpty(dto.Password)));
        await db.SaveChangesAsync(ct);
        return await ReloadAsync(id, ct);
    }

    private async Task<MailboxDto> ReloadAsync(Guid id, CancellationToken ct)
    {
        var saved = await db.EmailMailboxes.AsNoTracking().Include(m => m.Teams).ThenInclude(t => t.Team).FirstAsync(m => m.Id == id, ct);
        return ToDto(saved, await db.InboundEmails.CountAsync(e => e.MailboxId == id, ct));
    }

    /// <summary>The Tickets settings: active teams, and defaults that exist (each default is optional).</summary>
    private async Task<List<FieldError>> ValidateTicketSettingsAsync(SaveMailboxDto dto, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        if (dto.TeamIds is { Count: > 0 } ids)
        {
            var distinct = ids.Distinct().ToList();
            var active = await db.Teams.AsNoTracking().Where(t => distinct.Contains(t.Id) && t.IsActive).CountAsync(ct);
            if (active != distinct.Count) errors.Add(new("teamIds", "Choose active teams only."));
        }

        // The review deadline: 0 switches it off; otherwise remind before it and escalate at or after it, within a working week.
        const int Limit = 7 * 24 * 60;
        var sla = dto.ReviewSlaMinutes;
        if (sla is < 0 or > Limit) errors.Add(new("reviewSlaMinutes", "Between 0 (no deadline) and 10080 minutes (a week)."));
        if (sla > 0)
        {
            if (dto.ReviewReminderMinutes is { } r && (r < 0 || r >= sla))
                errors.Add(new("reviewReminderMinutes", "Before the deadline (fewer minutes than it), or 0 for no reminder."));
            if (dto.ReviewEscalationMinutes is { } e && (e < sla || e > Limit))
                errors.Add(new("reviewEscalationMinutes", "At or after the deadline, and at most 10080 minutes."));
        }
        if (dto.ReviewEscalateTo != null && !ReviewEscalationTargets.All.Contains(dto.ReviewEscalateTo))
            errors.Add(new("reviewEscalateTo", "Choose the team leads or the administrators."));
        return errors;
    }

    private static void ApplyTeams(EmailMailbox m, SaveMailboxDto dto)
    {
        // A mailbox no longer carries case details (they are chosen when an email becomes a case): saving it clears any left
        // from before, so nothing hidden is kept. The columns stay for history; nothing reads them for new tickets.
        m.DefaultDepartmentId = null;
        m.DefaultCaseType = null;
        m.DefaultSubCategory = null;
        m.DefaultPriority = null;
        m.DefaultPreferredLanguage = null;
        if (dto.ReviewSlaMinutes is { } sla) m.ReviewSlaMinutes = sla;
        if (dto.ReviewReminderMinutes is { } reminder) m.ReviewReminderMinutes = reminder;
        if (dto.ReviewEscalationMinutes is { } escalation) m.ReviewEscalationMinutes = escalation;
        else if (m.ReviewEscalationMinutes < m.ReviewSlaMinutes) m.ReviewEscalationMinutes = m.ReviewSlaMinutes;
        if (dto.ReviewEscalateTo != null) m.ReviewEscalateTo = dto.ReviewEscalateTo;
        if (dto.TeamIds == null) return;
        var wanted = dto.TeamIds.Distinct().ToHashSet();
        m.Teams.RemoveAll(t => !wanted.Contains(t.TeamId));
        foreach (var teamId in wanted.Where(t => m.Teams.All(x => x.TeamId != t)))
            m.Teams.Add(new EmailMailboxTeam { MailboxId = m.Id, TeamId = teamId });

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>What the emails this mailbox received since <paramref name="since"/> became, counted by status ("Check now").</summary>
    public async Task<Dictionary<string, int>> SyncOutcomeAsync(Guid mailboxId, DateTime since, CancellationToken ct) =>
        await db.InboundEmails.AsNoTracking()
            .Where(e => e.MailboxId == mailboxId && e.CreatedAt >= since)
            .GroupBy(e => e.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task DeleteMailboxAsync(Guid id, Guid actingUserId, CancellationToken ct)
    {
        // Deleting a mailbox also removes the emails it received from the Inbox (one save, so both go or neither does). The
        // cases those emails created are not touched: a case keeps its own copy of the subject, text and attachments.
        var mailbox = await db.EmailMailboxes.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("Mailbox not found.");
        // Tickets are a record of requests (and may have become cases); they are never deleted with their queue.
        if (await db.Tickets.AnyAsync(t => t.MailboxId == id, ct))
            throw new InvalidOperationException("This mailbox has tickets, so it cannot be deleted. Switch it off instead: it stops being read and its tickets stay.");
        var emails = await db.InboundEmails.Where(e => e.MailboxId == id).ToListAsync(ct);
        var originals = emails.Where(e => e.RawKey != null).Select(e => e.RawKey!).ToList();
        db.InboundEmails.RemoveRange(emails);
        db.EmailMailboxes.Remove(mailbox);
        Audit(actingUserId, "MAILBOX_DELETED", "EmailMailbox", $"Mailbox {mailbox.Address} deleted with its {emails.Count} emails.", Snapshot(mailbox, passwordChanged: false), null);
        await db.SaveChangesAsync(ct);

        // The stored originals (.eml) go after the rows are gone; one that cannot be removed is only wasted space.
        foreach (var key in originals)
        {
            try { await store.DeleteAsync(key, CancellationToken.None); }
            catch (Exception) { /* best effort */ }
        }
    }

    public async Task<MailboxTestResultDto> TestAsync(TestMailboxDto dto, CancellationToken ct)
    {
        var errors = ValidateMailbox(dto.Settings, requirePassword: false);
        if (errors.Count > 0) throw new FieldValidationException(errors);
        var password = dto.Settings.Password;
        if (string.IsNullOrEmpty(password))
        {
            var stored = dto.MailboxId is { } id ? await db.EmailMailboxes.AsNoTracking().Where(m => m.Id == id).Select(m => m.PasswordEncrypted).FirstOrDefaultAsync(ct) : null;
            if (stored == null) throw new FieldValidationException([new FieldError("password", "Enter the password to test the connection.")]);
            password = secrets.Unprotect(stored);
        }
        var s = dto.Settings;
        var address = s.Address.Trim().ToLowerInvariant();
        var result = await mail.TestAsync(new MailboxConnection(address, string.IsNullOrWhiteSpace(s.Username) ? address : s.Username.Trim(), password,
            s.ImapHost.Trim(), s.ImapPort, s.ImapSecurity, s.SmtpHost.Trim(), s.SmtpPort, s.SmtpSecurity), ct);
        return new MailboxTestResultDto { ImapOk = result.ImapOk, ImapError = result.ImapError, SmtpOk = result.SmtpOk, SmtpError = result.SmtpError };
    }

    private List<FieldError> ValidateMailbox(SaveMailboxDto dto, bool requirePassword)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add(new("name", "Enter a name for the mailbox."));
        else if (dto.Name.Trim().Length > 100) errors.Add(new("name", "The name can be at most 100 characters."));
        if (!IsEmail(dto.Address)) errors.Add(new("address", "Enter a valid email address."));
        if (!string.IsNullOrWhiteSpace(dto.Username) && dto.Username.Trim().Length > 254) errors.Add(new("username", "The sign-in name is too long."));
        if (requirePassword && string.IsNullOrEmpty(dto.Password)) errors.Add(new("password", "Enter the mailbox password."));
        if (!string.IsNullOrEmpty(dto.Password) && !secrets.IsConfigured) errors.Add(new("password", MailboxSecretProtector.NotConfiguredMessage));
        Host("imapHost", dto.ImapHost, "IMAP");
        Host("smtpHost", dto.SmtpHost, "SMTP");
        if (dto.ImapPort is < 1 or > 65535) errors.Add(new("imapPort", "Enter a port between 1 and 65535."));
        if (dto.SmtpPort is < 1 or > 65535) errors.Add(new("smtpPort", "Enter a port between 1 and 65535."));
        if (!MailSecurity.All.Contains(dto.ImapSecurity)) errors.Add(new("imapSecurity", "Choose SSL/TLS or STARTTLS."));
        if (!MailSecurity.All.Contains(dto.SmtpSecurity)) errors.Add(new("smtpSecurity", "Choose SSL/TLS or STARTTLS."));
        return errors;

        void Host(string field, string? value, string kind)
        {
            var v = value?.Trim() ?? "";
            if (v.Length == 0) errors.Add(new(field, $"Enter the {kind} server, e.g. {(kind == "IMAP" ? "imap.gmail.com" : "smtp.gmail.com")}."));
            else if (v.Contains("://") || v.Contains('/') || v.Contains(' ') || v.Length > 253) errors.Add(new(field, $"Enter only the {kind} server name, e.g. {(kind == "IMAP" ? "imap.gmail.com" : "smtp.gmail.com")}."));
        }
    }

    private void Apply(EmailMailbox m, SaveMailboxDto dto)
    {
        m.Name = dto.Name.Trim();
        m.Address = dto.Address.Trim().ToLowerInvariant();
        m.Username = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim();
        m.ImapHost = dto.ImapHost.Trim().ToLowerInvariant();
        m.ImapPort = dto.ImapPort;
        m.ImapSecurity = dto.ImapSecurity;
        m.SmtpHost = dto.SmtpHost.Trim().ToLowerInvariant();
        m.SmtpPort = dto.SmtpPort;
        m.SmtpSecurity = dto.SmtpSecurity;
        m.IsEnabled = dto.IsEnabled;
        m.SendAcknowledgement = dto.SendAcknowledgement;
        if (!string.IsNullOrEmpty(dto.Password))
        {
            m.PasswordEncrypted = secrets.Protect(dto.Password);
            m.PasswordUpdatedAt = DateTime.UtcNow;
        }
    }

    private static MailboxDto ToDto(EmailMailbox m, int emails) => new()
    {
        Id = m.Id, Name = m.Name, Address = m.Address, Username = m.Username, HasPassword = m.PasswordEncrypted != null, PasswordUpdatedAt = m.PasswordUpdatedAt,
        ImapHost = m.ImapHost, ImapPort = m.ImapPort, ImapSecurity = m.ImapSecurity, SmtpHost = m.SmtpHost, SmtpPort = m.SmtpPort, SmtpSecurity = m.SmtpSecurity,
        IsEnabled = m.IsEnabled, SendAcknowledgement = m.SendAcknowledgement, Status = m.IsEnabled ? m.Status : "Disabled",
        LastSyncAt = m.LastSyncAt, LastSuccessAt = m.LastSuccessAt, LastError = m.LastError, EmailCount = emails, CreatedAt = m.CreatedAt, UpdatedAt = m.UpdatedAt,
        TeamIds = m.Teams.Select(t => t.TeamId).ToList(),
        TeamNames = m.Teams.Where(t => t.Team != null).Select(t => t.Team!.Name).OrderBy(n => n).ToList(),
        DefaultDepartmentId = m.DefaultDepartmentId, DefaultCaseType = m.DefaultCaseType, DefaultSubCategory = m.DefaultSubCategory,
        DefaultPriority = m.DefaultPriority, DefaultPreferredLanguage = m.DefaultPreferredLanguage,
        ReviewSlaMinutes = m.ReviewSlaMinutes, ReviewReminderMinutes = m.ReviewReminderMinutes, ReviewEscalationMinutes = m.ReviewEscalationMinutes,
        ReviewEscalateTo = m.ReviewEscalateTo,
    };

    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254) return false;
        try { return new MailAddress(value.Trim()).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase) && EmailParsing.DomainOf(value).Contains('.'); }
        catch (FormatException) { return false; }
    }

    // ───────────────────────────── verified domains ─────────────────────────────

    public async Task<List<VerifiedDomainDto>> DomainsAsync(CancellationToken ct)
    {
        var counts = await db.InboundEmails.AsNoTracking().Where(e => (e.Status == EmailStatuses.CaseCreated || e.Status == EmailStatuses.TicketCreated) && e.VerifiedDomainId != null)
            .GroupBy(e => e.VerifiedDomainId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var list = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Organisation)
            .Include(d => d.Senders).ThenInclude(x => x.Mailbox).Include(d => d.Senders).ThenInclude(x => x.Team)
            .ToListAsync(ct);
        return list.OrderBy(d => d.Organisation?.Name ?? d.OrganisationName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Domain)
            .Select(d => ToDto(d, counts.GetValueOrDefault(d.Id))).ToList();
    }

    /// <summary>The organisations, for the Verified Domain form's organisation field.</summary>
    public async Task<List<EmailOrganisationDto>> OrganisationsAsync(CancellationToken ct)
    {
        var domainCounts = await db.EmailVerifiedDomains.AsNoTracking().GroupBy(d => d.OrganisationId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var orgs = await db.EmailOrganisations.AsNoTracking().Where(o => o.IsActive).ToListAsync(ct);
        return orgs.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Select(o => new EmailOrganisationDto { Id = o.Id, Name = o.Name, DomainCount = domainCounts.GetValueOrDefault(o.Id) }).ToList();
    }

    /// <summary>
    /// Saves a verified domain with its organisation and its whole sender list in ONE save: the organisation (found by name, ignoring
    /// case and spacing, or created), the domain and every sender change commit together or not at all, with the audit entry.
    /// </summary>
    public async Task<VerifiedDomainDto> SaveDomainAsync(Guid? id, SaveVerifiedDomainDto dto, Guid actingUserId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var domain = id is { } existingId
            ? await db.EmailVerifiedDomains.Include(d => d.Senders).Include(d => d.Organisation).FirstOrDefaultAsync(d => d.Id == existingId, ct)
              ?? throw new KeyNotFoundException("Verified domain not found.")
            : new EmailVerifiedDomain { CreatedAt = now, CreatedByUserId = Actor(actingUserId) };
        var before = id == null ? null : Snapshot(domain);

        var errors = new List<FieldError>();
        var name = EmailParsing.NormalizeDomain(dto.Domain);
        if (name.Length == 0) errors.Add(new("domain", "Enter a domain like sbi.co.in (without @ or http)."));
        else if (PublicEmailDomains.Contains(name)) errors.Add(new("domain", $"{name} is a public email provider: anyone can have an address there, so it cannot be a verified domain."));
        else if (await db.EmailVerifiedDomains.AnyAsync(d => d.Domain == name && d.Id != domain.Id, ct)) errors.Add(new("domain", "This domain is already verified."));

        var orgName = CollapseSpaces(dto.OrganisationName ?? "");
        if (orgName.Length == 0) errors.Add(new("organisationName", "Enter the organisation the domain belongs to."));
        else if (orgName.Length > 200) errors.Add(new("organisationName", "At most 200 characters."));

        // A domain names the organisation; it never verifies everyone at it. Only its registered senders are verified, and a domain
        // may be registered before any of them (its people's emails then wait for review, filed under the organisation).
        const string policy = VerificationPolicies.SpecificSenders;

        var senders = await ValidateSendersAsync(dto.Senders ?? new(), name, dto.IncludeSubdomains, domain.Id, errors, ct);

        if (errors.Count > 0) throw new FieldValidationException(errors);

        // The organisation: one record per name, so several domains of one organisation share it.
        var key = EmailOrganisation.KeyOf(orgName);
        var organisation = await db.EmailOrganisations.FirstOrDefaultAsync(o => o.NameKey == key, ct);
        if (organisation == null)
        {
            organisation = new EmailOrganisation { Name = orgName, NameKey = key, CreatedAt = now, CreatedByUserId = Actor(actingUserId) };
            db.EmailOrganisations.Add(organisation);
        }
        else if (!organisation.IsActive)
        {
            organisation.IsActive = true;
            organisation.UpdatedAt = now;
            organisation.UpdatedByUserId = Actor(actingUserId);
        }

        domain.Domain = name;
        domain.OrganisationId = organisation.Id;
        domain.Organisation = organisation;
        domain.OrganisationName = organisation.Name;
        domain.IncludeSubdomains = dto.IncludeSubdomains;
        domain.IsActive = dto.IsActive;
        domain.VerificationPolicy = policy;
        domain.RequireAuthentication = dto.RequireAuthentication;
        if (id == null) db.EmailVerifiedDomains.Add(domain);
        else
        {
            domain.UpdatedAt = now;
            domain.UpdatedByUserId = Actor(actingUserId);
        }

        // The sender list is replaced as a set: a row keeps its id when its id (or, failing that, its address) is sent back.
        var keep = new HashSet<Guid>();
        foreach (var s in senders)
        {
            var row = (s.Id is { } sid ? domain.Senders.FirstOrDefault(x => x.Id == sid) : null)
                      ?? domain.Senders.FirstOrDefault(x => x.Address == s.Address && !keep.Contains(x.Id));
            if (row == null)
            {
                row = new EmailVerifiedSender { VerifiedDomainId = domain.Id, CreatedAt = now, CreatedByUserId = Actor(actingUserId) };
                domain.Senders.Add(row);
                db.EmailVerifiedSenders.Add(row);   // explicitly new: its id is set already, so EF would otherwise take it for an existing row
            }
            else if (row.Address != s.Address || row.DisplayName != s.DisplayName || row.Description != s.Description || row.IsActive != s.IsActive
                     || row.MailboxId != s.MailboxId || row.TeamId != s.TeamId)
            {
                row.UpdatedAt = now;
                row.UpdatedByUserId = Actor(actingUserId);
            }
            row.Address = s.Address;
            row.DisplayName = s.DisplayName;
            row.Description = s.Description;
            row.IsActive = s.IsActive;
            row.MailboxId = s.MailboxId;
            row.TeamId = s.TeamId;
            keep.Add(row.Id);
        }
        foreach (var gone in domain.Senders.Where(x => !keep.Contains(x.Id)).ToList())
        {
            domain.Senders.Remove(gone);
            db.EmailVerifiedSenders.Remove(gone);
        }

        Audit(actingUserId, id == null ? "DOMAIN_CREATED" : "DOMAIN_UPDATED", "EmailVerifiedDomain",
            id == null ? $"Verified domain {domain.Domain} ({organisation.Name}) added." : $"Verified domain {domain.Domain} ({organisation.Name}) changed.",
            before, Snapshot(domain));
        await db.SaveChangesAsync(ct);

        var saved = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Organisation)
            .Include(d => d.Senders).ThenInclude(x => x.Mailbox).Include(d => d.Senders).ThenInclude(x => x.Team)
            .FirstAsync(d => d.Id == domain.Id, ct);
        return ToDto(saved, await db.InboundEmails.CountAsync(e => e.VerifiedDomainId == domain.Id && (e.Status == EmailStatuses.CaseCreated || e.Status == EmailStatuses.TicketCreated), ct));
    }

    /// <summary>
    /// Checks every sender row and returns them normalised. A sender's address must be a valid address at this domain (or one of its
    /// sub-domains when those are accepted), appear once in the list, and not belong to another domain: one address is one identity.
    /// </summary>
    private async Task<List<VerifiedSenderDto>> ValidateSendersAsync(List<VerifiedSenderDto> input, string domainName, bool includeSubdomains, Guid domainId,
        List<FieldError> errors, CancellationToken ct)
    {
        var result = new List<VerifiedSenderDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Count; i++)
        {
            var s = input[i];
            var address = (s.Address ?? "").Trim().ToLowerInvariant();
            var alias = CollapseSpaces(s.DisplayName ?? "");
            var description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Trim();
            var field = $"senders.{i}";
            if (address.Length == 0 && alias.Length == 0 && description == null) continue;   // an empty row the form left behind

            if (!IsEmail(address)) errors.Add(new($"{field}.address", "Enter a valid email address."));
            else if (domainName.Length > 0)
            {
                var at = EmailParsing.DomainOf(address);
                if (at != domainName && !(includeSubdomains && at.EndsWith("." + domainName, StringComparison.Ordinal)))
                    errors.Add(new($"{field}.address", includeSubdomains ? $"Must be an address at {domainName} or one of its sub-domains." : $"Must be an address at {domainName}."));
                else if (!seen.Add(address)) errors.Add(new($"{field}.address", "This address is listed twice."));
            }
            if (alias.Length == 0) errors.Add(new($"{field}.displayName", "Enter the name this sender is shown by."));
            else if (alias.Length > 100) errors.Add(new($"{field}.displayName", "At most 100 characters."));
            if (description?.Length > 500) errors.Add(new($"{field}.description", "At most 500 characters."));
            result.Add(new VerifiedSenderDto { Id = s.Id, Address = address, DisplayName = alias, Description = description, IsActive = s.IsActive,
                MailboxId = s.MailboxId, TeamId = s.TeamId });
        }
        await ValidateMappingsAsync(result.Select((r, i) => ($"senders.{i}", r.MailboxId, r.TeamId)).ToList(), errors, ct);

        var addresses = result.Select(r => r.Address).Where(a => a.Length > 0).ToList();
        if (addresses.Count > 0)
        {
            var taken = await db.EmailVerifiedSenders.AsNoTracking().Where(x => addresses.Contains(x.Address) && x.VerifiedDomainId != domainId)
                .Select(x => new { x.Address, x.VerifiedDomain!.Domain }).ToListAsync(ct);
            foreach (var t in taken)
                errors.Add(new($"senders.{result.FindIndex(r => r.Address == t.Address)}.address", $"{t.Address} is already a sender of {t.Domain}."));
        }
        if (result.Count > 500) errors.Add(new("senders", "At most 500 senders per domain."));
        return result;
    }

    public async Task DeleteDomainAsync(Guid id, Guid actingUserId, CancellationToken ct)
    {
        var domain = await db.EmailVerifiedDomains.Include(d => d.Senders).Include(d => d.Organisation).FirstOrDefaultAsync(d => d.Id == id, ct)
                     ?? throw new KeyNotFoundException("Verified domain not found.");
        Audit(actingUserId, "DOMAIN_DELETED", "EmailVerifiedDomain", $"Verified domain {domain.Domain} ({domain.Organisation?.Name ?? domain.OrganisationName}) deleted.",
            Snapshot(domain), null);
        db.EmailVerifiedDomains.Remove(domain);   // its senders go with it
        await db.SaveChangesAsync(ct);
    }

    private static VerifiedDomainDto ToDto(EmailVerifiedDomain d, int cases) => new()
    {
        Id = d.Id, Domain = d.Domain, OrganisationId = d.OrganisationId, OrganisationName = d.Organisation?.Name ?? d.OrganisationName,
        IncludeSubdomains = d.IncludeSubdomains, IsActive = d.IsActive, VerificationPolicy = d.VerificationPolicy, RequireAuthentication = d.RequireAuthentication,
        Senders = d.Senders.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Address)
            .Select(s => SenderDto(s, d)).ToList(),
        CaseCount = cases, CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
    };

    private static VerifiedSenderDto SenderDto(EmailVerifiedSender s, EmailVerifiedDomain? d) => new()
    {
        Id = s.Id, Address = s.Address, DisplayName = s.DisplayName, Description = s.Description, IsActive = s.IsActive,
        MailboxId = s.MailboxId, MailboxName = s.Mailbox == null ? null : $"{s.Mailbox.Name} <{s.Mailbox.Address}>",
        TeamId = s.TeamId, TeamName = s.Team?.Name,
        VerifiedDomainId = s.VerifiedDomainId, Domain = d?.Domain, OrganisationId = d?.OrganisationId, OrganisationName = d?.Organisation?.Name ?? d?.OrganisationName,
        CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt,
    };

    /// <summary>
    /// A sender's mailbox and team. Both optional; a team needs a mailbox, and must be one of the teams that mailbox is shared with,
    /// so a sender can never route work to a team that may not see the mailbox. An inactive team is not offered.
    /// </summary>
    private async Task ValidateMappingsAsync(List<(string Field, Guid? MailboxId, Guid? TeamId)> rows, List<FieldError> errors, CancellationToken ct)
    {
        var mailboxIds = rows.Where(r => r.MailboxId != null).Select(r => r.MailboxId!.Value).Distinct().ToList();
        var teamIds = rows.Where(r => r.TeamId != null).Select(r => r.TeamId!.Value).Distinct().ToList();
        if (mailboxIds.Count == 0 && teamIds.Count == 0) return;
        var mailboxes = await db.EmailMailboxes.AsNoTracking().Include(m => m.Teams).Where(m => mailboxIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var (field, mailboxId, teamId) in rows)
        {
            EmailMailbox? mailbox = null;
            if (mailboxId is { } mid && !mailboxes.TryGetValue(mid, out mailbox))
                errors.Add(new($"{field}.mailboxId", "This mailbox no longer exists. Choose another."));
            if (teamId is not { } tid) continue;
            if (!teams.TryGetValue(tid, out var team)) errors.Add(new($"{field}.teamId", "This team no longer exists. Choose another."));
            else if (!team.IsActive) errors.Add(new($"{field}.teamId", $"{team.Name} is inactive. Choose an active team."));
            else if (mailboxId == null) errors.Add(new($"{field}.teamId", "Choose the mailbox first: the team must be one the mailbox is shared with."));
            else if (mailbox != null && mailbox.Teams.All(t => t.TeamId != tid))
                errors.Add(new($"{field}.teamId", $"{mailbox.Address} is not shared with {team.Name}. Give {team.Name} access to the mailbox under Inbound Mailboxes, or choose one of its teams."));
        }
    }

    /// <summary>Every registered sender, with its domain, organisation, mailbox and team (Sender Verification → Senders).</summary>
    public async Task<List<VerifiedSenderDto>> SendersAsync(CancellationToken ct)
    {
        var rows = await db.EmailVerifiedSenders.AsNoTracking()
            .Include(s => s.VerifiedDomain).ThenInclude(d => d!.Organisation).Include(s => s.Mailbox).Include(s => s.Team)
            .ToListAsync(ct);
        return rows.OrderBy(s => s.VerifiedDomain?.Organisation?.Name ?? s.VerifiedDomain?.OrganisationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Address)
            .Select(s => SenderDto(s, s.VerifiedDomain)).ToList();
    }

    /// <summary>
    /// Registers (or changes) one sender directly. The domain comes from the address: a registered domain must belong to the
    /// organisation named; an unregistered one is registered for it, verifying only its listed senders (never everyone at it).
    /// The same address can never be two senders: the unique address is checked here and by the database.
    /// </summary>
    public async Task<VerifiedSenderDto> SaveSenderAsync(Guid? id, SaveSenderDto dto, Guid actingUserId, CancellationToken ct)
    {
        var (sender, domain) = await StageSenderAsync(id, dto, actingUserId, ct);
        await db.SaveChangesAsync(ct);
        return SenderDto(sender, domain);
    }

    /// <summary>Validates and applies a sender registration to the context WITHOUT saving, so a review can commit it with its decision.</summary>
    private async Task<(EmailVerifiedSender Sender, EmailVerifiedDomain Domain)> StageSenderAsync(Guid? id, SaveSenderDto dto, Guid actingUserId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sender = id is { } existingId
            ? await db.EmailVerifiedSenders.Include(s => s.VerifiedDomain).ThenInclude(d => d!.Organisation).Include(s => s.Mailbox).Include(s => s.Team)
                  .FirstOrDefaultAsync(s => s.Id == existingId, ct) ?? throw new KeyNotFoundException("Sender not found.")
            : null;
        var before = sender == null ? null : Snapshot(sender, sender.VerifiedDomain!);

        var errors = new List<FieldError>();
        var address = (dto.Address ?? "").Trim().ToLowerInvariant();
        var alias = CollapseSpaces(dto.DisplayName ?? "");
        var description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        var orgName = CollapseSpaces(dto.OrganisationName ?? "");
        var domainName = "";
        if (!IsEmail(address)) errors.Add(new("address", "Enter a valid email address."));
        else
        {
            domainName = EmailParsing.NormalizeDomain(EmailParsing.DomainOf(address));
            if (domainName.Length == 0) errors.Add(new("address", "Enter a valid email address."));
            else if (PublicEmailDomains.Contains(domainName))
                errors.Add(new("address", $"{domainName} is a public email provider: anyone can have an address there, so its senders cannot be verified by organisation."));
            else if (await db.EmailVerifiedSenders.AsNoTracking().Where(s => s.Address == address && s.Id != (id ?? Guid.Empty))
                         .Select(s => s.VerifiedDomain!.Domain).FirstOrDefaultAsync(ct) is { } taken)
                errors.Add(new("address", $"{address} is already registered (under {taken}). Edit that sender instead."));
        }
        if (alias.Length == 0) errors.Add(new("displayName", "Enter the name this sender is shown by."));
        else if (alias.Length > 100) errors.Add(new("displayName", "At most 100 characters."));
        if (description?.Length > 500) errors.Add(new("description", "At most 500 characters."));
        if (orgName.Length == 0) errors.Add(new("organisationName", "Enter the organisation the sender belongs to."));
        else if (orgName.Length > 200) errors.Add(new("organisationName", "At most 200 characters."));

        // The sender's domain: the registered domain the address falls under (exact, or a parent that accepts sub-domains).
        EmailVerifiedDomain? domain = null;
        if (domainName.Length > 0)
        {
            var domains = await db.EmailVerifiedDomains.Include(d => d.Organisation).ToListAsync(ct);
            domain = EmailParsing.MatchDomain(domainName, domains, d => d.Domain, d => d.IncludeSubdomains);
            if (domain != null && orgName.Length > 0 && EmailOrganisation.KeyOf(orgName) != (domain.Organisation?.NameKey ?? EmailOrganisation.KeyOf(domain.OrganisationName)))
                errors.Add(new("organisationName", $"{domain.Domain} is registered to {domain.Organisation?.Name ?? domain.OrganisationName}. A sender at it belongs to that organisation."));
            if (sender != null && domain?.Id != sender.VerifiedDomainId)
                errors.Add(new("address", $"The address must stay at {sender.VerifiedDomain!.Domain}. To register an address at another domain, add a new sender."));
        }
        await ValidateMappingsAsync([("", dto.MailboxId, dto.TeamId)], errors, ct);
        if (errors.Count > 0)
            throw new FieldValidationException(errors.Select(e => e.Field.StartsWith('.') ? new FieldError(e.Field[1..], e.Message) : e).ToList());

        if (domain == null)
        {
            var key = EmailOrganisation.KeyOf(orgName);
            var organisation = await db.EmailOrganisations.FirstOrDefaultAsync(o => o.NameKey == key, ct);
            if (organisation == null)
            {
                organisation = new EmailOrganisation { Name = orgName, NameKey = key, CreatedAt = now, CreatedByUserId = Actor(actingUserId) };
                db.EmailOrganisations.Add(organisation);
            }
            else if (!organisation.IsActive)
            {
                organisation.IsActive = true;
                organisation.UpdatedAt = now;
                organisation.UpdatedByUserId = Actor(actingUserId);
            }
            domain = new EmailVerifiedDomain
            {
                Domain = domainName, OrganisationId = organisation.Id, Organisation = organisation, OrganisationName = organisation.Name,
                VerificationPolicy = VerificationPolicies.SpecificSenders, IsActive = true, CreatedAt = now, CreatedByUserId = Actor(actingUserId),
            };
            db.EmailVerifiedDomains.Add(domain);
            Audit(actingUserId, "DOMAIN_CREATED", "EmailVerifiedDomain",
                $"Domain {domain.Domain} ({organisation.Name}) added with its first sender; only its listed senders are verified.", null, Snapshot(domain));
        }

        if (sender == null)
        {
            sender = new EmailVerifiedSender { VerifiedDomainId = domain.Id, VerifiedDomain = domain, CreatedAt = now, CreatedByUserId = Actor(actingUserId) };
            db.EmailVerifiedSenders.Add(sender);
        }
        else
        {
            sender.UpdatedAt = now;
            sender.UpdatedByUserId = Actor(actingUserId);
        }
        sender.Address = address;
        sender.DisplayName = alias;
        sender.Description = description;
        sender.IsActive = dto.IsActive;
        sender.MailboxId = dto.MailboxId;
        sender.TeamId = dto.TeamId;
        sender.Mailbox = dto.MailboxId == null ? null : await db.EmailMailboxes.FirstOrDefaultAsync(m => m.Id == dto.MailboxId, ct);
        sender.Team = dto.TeamId == null ? null : await db.Teams.FirstOrDefaultAsync(t => t.Id == dto.TeamId, ct);

        Audit(actingUserId, before == null ? "SENDER_REGISTERED" : "SENDER_UPDATED", "EmailVerifiedSender",
            before == null ? $"Sender {address} ({domain.Organisation?.Name ?? domain.OrganisationName}) registered." : $"Sender {address} changed.",
            before, Snapshot(sender, domain));
        return (sender, domain);
    }

    // ───────────────────────────── sender review ─────────────────────────────

    /// <summary>
    /// A reviewer's decision on an email from a sender who is not registered. Approve releases THIS email only (the caller then works it
    /// into a ticket); registering the sender is a second, separately confirmed choice, committed in the same save as the decision.
    /// Reject and Suspicious need a reason and end the email for good. Two reviewers can never both decide: the email's version is checked.
    /// </summary>
    public async Task<InboxEmailDetailDto> ReviewAsync(Guid id, ReviewEmailDto dto, Guid actingUserId, bool mayRegister, CancellationToken ct)
    {
        // Visibility first (VisibleEmails is a no-tracking query), then the email itself, tracked, to change it.
        if (!await VisibleEmails().AnyAsync(e => e.Id == id, ct)) throw new KeyNotFoundException("Email not found.");
        var email = await db.InboundEmails.FirstAsync(e => e.Id == id, ct);
        if (email.Status != EmailStatuses.PendingReview || email.Version != dto.Version)
            throw new EmailReviewConflictException(email.Status == EmailStatuses.PendingReview
                ? "This email was just changed by someone else. Reload it and decide again."
                : "This email is no longer waiting for review; someone else has already decided.");

        var decision = ReviewDecisionsOf(dto.Decision);
        var note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim();
        var errors = new List<FieldError>();
        if (decision == null) errors.Add(new("decision", "Choose Approve, Reject or Mark as suspicious."));
        if (decision is ReviewDecisions.Rejected or ReviewDecisions.Suspicious && (note == null || note.Length < 3))
            errors.Add(new("note", "Say why, so the next reviewer and the audit trail know."));
        if (note?.Length > 1000) errors.Add(new("note", "At most 1000 characters."));
        if (dto.Register != null && decision != ReviewDecisions.Approved)
            errors.Add(new("register", "A sender can only be registered while approving their email."));
        if (dto.Register != null && decision == ReviewDecisions.Approved && !dto.ConfirmRegistration)
            errors.Add(new("confirmRegistration", "Confirm that this sender should be registered permanently: their future emails will be verified without review."));
        if (errors.Count > 0) throw new FieldValidationException(errors);
        if (dto.Register != null && !mayRegister)
            throw new EmailReviewForbiddenException("You may approve this email, but not register its sender. Approve it without registering, or ask someone who may register senders.");

        var before = new { email.Status, email.ReviewDecision };
        if (dto.Register is { } r)
        {
            try
            {
                await StageSenderAsync(null, new SaveSenderDto
                {
                    Address = email.FromAddress, DisplayName = r.DisplayName, Description = r.Description, OrganisationName = r.OrganisationName,
                    IsActive = true, MailboxId = r.MailboxId, TeamId = r.TeamId,
                }, actingUserId, ct);
            }
            catch (FieldValidationException ex)
            {
                throw new FieldValidationException(ex.Errors.Select(e => new FieldError($"register.{e.Field}", e.Message)).ToList());
            }
        }

        var now = DateTime.UtcNow;
        ReviewSla.Stop(email, now);
        email.ReviewDecision = decision;
        email.ReviewNote = note;
        email.ReviewedById = Actor(actingUserId);
        email.ReviewedAt = now;
        email.Version++;
        email.UpdatedAt = now;
        switch (decision)
        {
            case ReviewDecisions.Approved:
                // Worked through again with the approval: it opens its ticket (and its sender, if registered now, is verified).
                email.Status = EmailStatuses.Received;
                email.StatusNote = "Approved by a reviewer; opening its ticket.";
                break;
            case ReviewDecisions.Rejected:
                email.Status = EmailStatuses.Rejected;
                email.StatusNote = $"Rejected by a reviewer: {note}";
                break;
            default:
                email.Status = EmailStatuses.Suspicious;
                email.StatusNote = $"Marked suspicious by a reviewer: {note}";
                break;
        }
        db.CaseEvents.Add(new CaseEvent
        {
            CaseId = null, EventType = EventType.Other, CreatedAt = now, UserId = Actor(actingUserId), IsInternal = true,
            Module = "EmailReview", EntityName = "InboundEmail",
            ActionType = decision switch { ReviewDecisions.Approved => "EMAIL_APPROVED", ReviewDecisions.Rejected => "EMAIL_REJECTED", _ => "EMAIL_MARKED_SUSPICIOUS" },
            Message = decision switch
            {
                ReviewDecisions.Approved => $"Email \"{email.Subject}\" from {email.FromAddress} approved{(dto.Register != null ? " and its sender registered" : "; the sender stays unregistered")}.",
                ReviewDecisions.Rejected => $"Email \"{email.Subject}\" from {email.FromAddress} rejected: {note}",
                _ => $"Email \"{email.Subject}\" from {email.FromAddress} marked suspicious: {note}",
            },
            OldValue = System.Text.Json.JsonSerializer.Serialize(before),
            NewValue = System.Text.Json.JsonSerializer.Serialize(new { email.Status, email.ReviewDecision, email.ReviewNote, RegisteredSender = dto.Register != null,
                email.ReviewStartedAt, email.ReviewDueAt, DecidedAt = now, email.ReviewSlaOutcome }),
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private static string? ReviewDecisionsOf(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "approve" or "approved" => ReviewDecisions.Approved,
        "reject" or "rejected" => ReviewDecisions.Rejected,
        "suspicious" => ReviewDecisions.Suspicious,
        _ => null,
    };

    /// <summary>The organisations the caller's emails come from, for the Inbox's organisation filter.</summary>
    public async Task<List<EmailOrganisationDto>> InboxOrganisationsAsync(CancellationToken ct)
    {
        var ids = VisibleEmails().Where(e => e.OrganisationId != null).Select(e => e.OrganisationId!.Value).Distinct();
        var orgs = await db.EmailOrganisations.AsNoTracking().Where(o => ids.Contains(o.Id)).ToListAsync(ct);
        return orgs.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Select(o => new EmailOrganisationDto { Id = o.Id, Name = o.Name }).ToList();
    }

    public async Task DeleteSenderAsync(Guid id, Guid actingUserId, CancellationToken ct)
    {
        var sender = await db.EmailVerifiedSenders.Include(s => s.VerifiedDomain).ThenInclude(d => d!.Organisation).Include(s => s.Mailbox).Include(s => s.Team)
                         .FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException("Sender not found.");
        Audit(actingUserId, "SENDER_DELETED", "EmailVerifiedSender", $"Sender {sender.Address} removed; its emails are no longer verified.",
            Snapshot(sender, sender.VerifiedDomain!), null);
        db.EmailVerifiedSenders.Remove(sender);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── audit ─────────────────────────────

    /// <summary>
    /// Records a Maintenance change in the audit trail (CaseEvents with no case, Module "EmailMaintenance"): who, when, and the
    /// settings before and after. Added to the same save as the change, so there is never a change without its record. A password
    /// is never recorded — only that it was changed.
    /// </summary>
    private void Audit(Guid actingUserId, string action, string entity, string description, object? before, object? after) =>
        db.CaseEvents.Add(new CaseEvent
        {
            CaseId = null, EventType = EventType.Other, CreatedAt = DateTime.UtcNow, UserId = Actor(actingUserId), IsInternal = true,
            Module = "EmailMaintenance", EntityName = entity, ActionType = action, Message = description,
            OldValue = before == null ? null : System.Text.Json.JsonSerializer.Serialize(before),
            NewValue = after == null ? null : System.Text.Json.JsonSerializer.Serialize(after),
        });

    private static Guid? Actor(Guid id) => id == Guid.Empty ? null : id;

    private static object Snapshot(EmailMailbox m, bool passwordChanged) => new
    {
        m.Name, m.Address, m.Username, m.ImapHost, m.ImapPort, m.ImapSecurity, m.SmtpHost, m.SmtpPort, m.SmtpSecurity, m.IsEnabled, m.SendAcknowledgement,
        m.ReviewSlaMinutes, m.ReviewReminderMinutes, m.ReviewEscalationMinutes, m.ReviewEscalateTo,
        PasswordChanged = passwordChanged,
    };

    private static object Snapshot(EmailVerifiedDomain d) => new
    {
        d.Domain, Organisation = d.Organisation?.Name ?? d.OrganisationName, d.IncludeSubdomains, d.IsActive, d.VerificationPolicy, d.RequireAuthentication,
        Senders = d.Senders.OrderBy(s => s.Address).Select(s => new { s.Address, s.DisplayName, s.Description, s.IsActive, s.MailboxId, s.TeamId }).ToList(),
    };

    private static object Snapshot(EmailVerifiedSender s, EmailVerifiedDomain d) => new
    {
        s.Address, s.DisplayName, s.Description, s.IsActive, d.Domain, Organisation = d.Organisation?.Name ?? d.OrganisationName,
        s.MailboxId, Mailbox = s.Mailbox?.Address, s.TeamId, Team = s.Team?.Name,
    };

    private static string CollapseSpaces(string value) => System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");

    public async Task<EmailCaseOptionsDto> CaseOptionsAsync(CancellationToken ct)
    {
        var types = await db.CaseTypeConfigs.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
            .Select(c => new EmailCaseOptionsDto.OptionDto(c.Code, c.Name)).ToListAsync(ct);
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name).ToListAsync(ct);
        var subs = await db.DepartmentSubCategories.AsNoTracking().Where(s => s.IsActive && s.DepartmentId != null)
            .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Name).ToListAsync(ct);
        var deptOptions = new List<EmailCaseOptionsDto.DepartmentOptionDto>();
        foreach (var d in departments)
        {
            var team = await DepartmentTeams.TeamOfAsync(db, d.Id, ct);
            deptOptions.Add(new(d.Id, d.Name, team is { IsActive: true }, subs.Where(s => s.DepartmentId == d.Id).Select(s => s.Name).ToList()));
        }
        var priorities = await db.PrioritySlaRules.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.DisplayOrder).Select(p => p.Priority).ToListAsync(ct);
        var languages = await db.LookupValues.AsNoTracking().Where(v => v.TypeCode == "PREFERRED_LANGUAGE" && v.IsActive)
            .OrderBy(v => v.DisplayOrder).Select(v => v.Value).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name)
            .Select(t => new EmailCaseOptionsDto.OptionDto(t.Id.ToString(), t.Name)).ToListAsync(ct);
        return new EmailCaseOptionsDto { CaseTypes = types, Departments = deptOptions, Priorities = priorities, Languages = languages, Teams = teams };
    }
}

/// <summary>The email is no longer waiting for review, or changed since the reviewer opened it. Answered 409.</summary>
public sealed class EmailReviewConflictException(string message) : Exception(message);

/// <summary>The reviewer may decide on the email but not register its sender. Answered 403.</summary>
public sealed class EmailReviewForbiddenException(string message) : Exception(message);
