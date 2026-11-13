namespace CaseManagement.Api.Services.EmailIntake;

using System.Net.Mail;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;

/// <summary>The Inbox (reading what arrived) and Maintenance (mailboxes and verified domains) behind the email intake screens.</summary>
public sealed class EmailIntakeAdminService(AppDbContext db, IMailboxSecretProtector secrets, IMailboxClient mail, IAttachmentStore store)
{
    /// <summary>Free email providers: anyone can have an address there, so a whole provider can never be a verified domain.</summary>
    public static readonly HashSet<string> PublicEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "yahoo.com", "yahoo.co.in", "yahoo.co.uk", "ymail.com", "outlook.com", "hotmail.com", "live.com",
        "msn.com", "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com", "gmx.com", "gmx.net", "mail.com",
        "zoho.com", "yandex.com", "yandex.ru", "rediffmail.com", "qq.com", "163.com", "126.com",
    };

    // ───────────────────────────── inbox ─────────────────────────────

    public async Task<PagedResponseDto<InboxEmailListItemDto>> ListAsync(string? filter, string? search, Guid? mailboxId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = Filter(db.InboundEmails.AsNoTracking(), filter);
        if (mailboxId is { } mb) query = query.Where(e => e.MailboxId == mb);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(e => e.Subject.ToLower().Contains(s) || e.FromAddress.Contains(s) || (e.FromName != null && e.FromName.ToLower().Contains(s))
                || (e.Case != null && e.Case.CaseNumber.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.ReceivedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new InboxEmailListItemDto
            {
                Id = e.Id, FromAddress = e.FromAddress, FromName = e.FromName, Subject = e.Subject, Snippet = e.Snippet, ReceivedAt = e.ReceivedAt,
                MailboxAddress = e.Mailbox!.Address, Verification = e.Verification, OrganisationName = e.VerifiedDomain != null ? e.VerifiedDomain.OrganisationName : null,
                Status = e.Status, CaseNumber = e.Case != null ? e.Case.CaseNumber : null, CaseId = e.CaseId, AttachmentCount = e.AttachmentCount, AckStatus = e.AckStatus,
            }).ToListAsync(ct);
        return new PagedResponseDto<InboxEmailListItemDto> { Items = items, TotalCount = total, Page = page, PageSize = pageSize };
    }

    public static IQueryable<InboundEmail> Filter(IQueryable<InboundEmail> query, string? filter) => (filter ?? "all").ToLowerInvariant() switch
    {
        "case-created" => query.Where(e => e.Status == EmailStatuses.CaseCreated || e.Status == EmailStatuses.LinkedToCase),
        "not-verified" => query.Where(e => e.Status == EmailStatuses.NotVerified),
        "failed" => query.Where(e => e.Status == EmailStatuses.Failed),
        "other" => query.Where(e => e.Status == EmailStatuses.Ignored || e.Status == EmailStatuses.Received || e.Status == EmailStatuses.Processing),
        _ => query,
    };

    public async Task<InboxCountsDto> CountsAsync(Guid? mailboxId, CancellationToken ct)
    {
        var query = db.InboundEmails.AsNoTracking();
        if (mailboxId is { } mb) query = query.Where(e => e.MailboxId == mb);
        var byStatus = await query.GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(params string[] s) => byStatus.Where(x => s.Contains(x.Key)).Sum(x => x.Count);
        return new InboxCountsDto
        {
            All = byStatus.Sum(x => x.Count),
            CaseCreated = Of(EmailStatuses.CaseCreated, EmailStatuses.LinkedToCase),
            NotVerified = Of(EmailStatuses.NotVerified),
            Failed = Of(EmailStatuses.Failed),
            Other = Of(EmailStatuses.Ignored, EmailStatuses.Received, EmailStatuses.Processing),
        };
    }

    public async Task<InboxEmailDetailDto> GetAsync(Guid id, CancellationToken ct)
    {
        var e = await db.InboundEmails.AsNoTracking().Include(x => x.Mailbox).Include(x => x.VerifiedDomain).Include(x => x.Case)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Email not found.");
        return new InboxEmailDetailDto
        {
            Id = e.Id, FromAddress = e.FromAddress, FromName = e.FromName, ToList = e.ToList, CcList = e.CcList, Subject = e.Subject, BodyText = e.BodyText,
            ReceivedAt = e.ReceivedAt, SentAt = e.SentAt, SizeBytes = e.SizeBytes, MailboxName = e.Mailbox?.Name ?? "", MailboxAddress = e.Mailbox?.Address ?? "",
            Attachments = (e.AttachmentNames ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Kind = e.Kind, Verification = e.Verification, VerificationNote = e.VerificationNote, OrganisationName = e.VerifiedDomain?.OrganisationName,
            Status = e.Status, StatusNote = e.StatusNote, Attempts = e.Attempts, CaseNumber = e.Case?.CaseNumber, CaseId = e.CaseId,
            AckStatus = e.AckStatus, AckSentAt = e.AckSentAt, AckError = e.AckError, HasOriginal = e.RawKey != null,
            CanRecheck = e.RawKey != null && e.Status is EmailStatuses.NotVerified or EmailStatuses.Failed,
        };
    }

    public async Task<(Stream Content, string FileName)> OpenOriginalAsync(Guid id, CancellationToken ct)
    {
        var e = await db.InboundEmails.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Email not found.");
        if (e.RawKey == null) throw new KeyNotFoundException("The original of this email was not kept (it was too large to read).");
        return (await store.OpenReadAsync(e.RawKey, ct), $"email-{e.ReceivedAt:yyyyMMdd-HHmm}-{e.Id.ToString()[..8]}.eml");
    }

    // ───────────────────────────── mailboxes ─────────────────────────────

    public async Task<List<MailboxDto>> MailboxesAsync(CancellationToken ct)
    {
        var counts = await db.InboundEmails.AsNoTracking().GroupBy(e => e.MailboxId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var list = await db.EmailMailboxes.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);
        return list.Select(m => ToDto(m, counts.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<MailboxDto> CreateMailboxAsync(SaveMailboxDto dto, CancellationToken ct)
    {
        var errors = ValidateMailbox(dto, requirePassword: true);
        var address = dto.Address.Trim().ToLowerInvariant();
        if (await db.EmailMailboxes.AnyAsync(m => m.Address == address, ct)) errors.Add(new FieldError("address", "This mailbox is already configured."));
        if (errors.Count > 0) throw new FieldValidationException(errors);

        var mailbox = new EmailMailbox { CreatedAt = DateTime.UtcNow };
        Apply(mailbox, dto);
        db.EmailMailboxes.Add(mailbox);
        await db.SaveChangesAsync(ct);
        return ToDto(mailbox, 0);
    }

    public async Task<MailboxDto> UpdateMailboxAsync(Guid id, SaveMailboxDto dto, CancellationToken ct)
    {
        var mailbox = await db.EmailMailboxes.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("Mailbox not found.");
        var errors = ValidateMailbox(dto, requirePassword: mailbox.PasswordEncrypted == null);
        var address = dto.Address.Trim().ToLowerInvariant();
        if (await db.EmailMailboxes.AnyAsync(m => m.Id != id && m.Address == address, ct)) errors.Add(new FieldError("address", "This mailbox is already configured."));
        if (errors.Count > 0) throw new FieldValidationException(errors);

        // A different mailbox (address, server or sign-in): start reading it from now, not from the old one's position.
        var newUser = string.IsNullOrWhiteSpace(dto.Username) ? null : dto.Username.Trim();
        var differentMailbox = mailbox.Address != address || !string.Equals(mailbox.ImapHost, dto.ImapHost.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mailbox.Username, newUser, StringComparison.OrdinalIgnoreCase);
        var wasEnabled = mailbox.IsEnabled;
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
        await db.SaveChangesAsync(ct);
        return ToDto(mailbox, await db.InboundEmails.CountAsync(e => e.MailboxId == id, ct));
    }

    /// <summary>Only a mailbox that never received anything can be deleted; otherwise it is switched off, so its emails keep their source.</summary>
    /// <summary>What the emails this mailbox received since <paramref name="since"/> became, counted by status ("Check now").</summary>
    public async Task<Dictionary<string, int>> SyncOutcomeAsync(Guid mailboxId, DateTime since, CancellationToken ct) =>
        await db.InboundEmails.AsNoTracking()
            .Where(e => e.MailboxId == mailboxId && e.CreatedAt >= since)
            .GroupBy(e => e.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    public async Task DeleteMailboxAsync(Guid id, CancellationToken ct)
    {
        // Deleting a mailbox also removes the emails it received from the Inbox (one save, so both go or neither does). The
        // cases those emails created are not touched: a case keeps its own copy of the subject, text and attachments.
        var mailbox = await db.EmailMailboxes.FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw new KeyNotFoundException("Mailbox not found.");
        var emails = await db.InboundEmails.Where(e => e.MailboxId == id).ToListAsync(ct);
        var originals = emails.Where(e => e.RawKey != null).Select(e => e.RawKey!).ToList();
        db.InboundEmails.RemoveRange(emails);
        db.EmailMailboxes.Remove(mailbox);
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
        var counts = await db.InboundEmails.AsNoTracking().Where(e => e.Status == EmailStatuses.CaseCreated && e.VerifiedDomainId != null)
            .GroupBy(e => e.VerifiedDomainId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var list = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Department).OrderBy(d => d.OrganisationName).ThenBy(d => d.Domain).ToListAsync(ct);
        return list.Select(d => ToDto(d, counts.GetValueOrDefault(d.Id))).ToList();
    }

    public async Task<VerifiedDomainDto> SaveDomainAsync(Guid? id, SaveVerifiedDomainDto dto, CancellationToken ct)
    {
        var domain = id is { } existingId
            ? await db.EmailVerifiedDomains.FirstOrDefaultAsync(d => d.Id == existingId, ct) ?? throw new KeyNotFoundException("Verified domain not found.")
            : new EmailVerifiedDomain { CreatedAt = DateTime.UtcNow };

        var errors = new List<FieldError>();
        var name = EmailParsing.NormalizeDomain(dto.Domain);
        if (name.Length == 0) errors.Add(new("domain", "Enter a domain like sbi.co.in (without @ or http)."));
        else if (PublicEmailDomains.Contains(name)) errors.Add(new("domain", $"{name} is a public email provider: anyone can have an address there, so it cannot be a verified domain."));
        else if (await db.EmailVerifiedDomains.AnyAsync(d => d.Domain == name && d.Id != domain.Id, ct)) errors.Add(new("domain", "This domain is already verified."));
        if (string.IsNullOrWhiteSpace(dto.OrganisationName)) errors.Add(new("organisationName", "Enter the organisation the domain belongs to."));
        else if (dto.OrganisationName.Trim().Length > 200) errors.Add(new("organisationName", "At most 200 characters."));

        // The case details must be ones Create Case accepts today, so a case from this domain cannot fail on them.
        var options = await CaseOptionsAsync(ct);
        var caseType = options.CaseTypes.FirstOrDefault(c => string.Equals(c.Value, dto.CaseType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (caseType == null) errors.Add(new("caseType", "Choose an active case type."));
        var department = options.Departments.FirstOrDefault(d => d.Id == dto.DepartmentId);
        if (department == null) errors.Add(new("departmentId", "Choose an active department."));
        else if (!department.HasTeam) errors.Add(new("departmentId", $"{department.Name} has no active team, so it cannot take cases. Assign it to a team first."));
        var sub = department?.SubCategories.FirstOrDefault(s => string.Equals(s, dto.SubCategory?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (department != null && sub == null) errors.Add(new("subCategory", $"Choose one of {department.Name}'s sub-categories."));
        var priority = options.Priorities.FirstOrDefault(p => string.Equals(p, dto.Priority?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (priority == null) errors.Add(new("priority", "Choose an active priority."));
        var language = options.Languages.FirstOrDefault(l => string.Equals(l, dto.PreferredLanguage?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (language == null) errors.Add(new("preferredLanguage", "Choose a language."));
        if (errors.Count > 0) throw new FieldValidationException(errors);

        domain.Domain = name;
        domain.OrganisationName = dto.OrganisationName.Trim();
        domain.IncludeSubdomains = dto.IncludeSubdomains;
        domain.IsActive = dto.IsActive;
        domain.CaseType = caseType!.Value;
        domain.DepartmentId = department!.Id;
        domain.SubCategory = sub!;
        domain.Priority = priority!;
        domain.PreferredLanguage = language!;
        if (id == null) db.EmailVerifiedDomains.Add(domain);
        else domain.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var saved = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Department).FirstAsync(d => d.Id == domain.Id, ct);
        return ToDto(saved, await db.InboundEmails.CountAsync(e => e.VerifiedDomainId == domain.Id && e.Status == EmailStatuses.CaseCreated, ct));
    }

    public async Task DeleteDomainAsync(Guid id, CancellationToken ct)
    {
        var domain = await db.EmailVerifiedDomains.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new KeyNotFoundException("Verified domain not found.");
        db.EmailVerifiedDomains.Remove(domain);
        await db.SaveChangesAsync(ct);
    }

    private static VerifiedDomainDto ToDto(EmailVerifiedDomain d, int cases) => new()
    {
        Id = d.Id, Domain = d.Domain, OrganisationName = d.OrganisationName, IncludeSubdomains = d.IncludeSubdomains, IsActive = d.IsActive,
        CaseType = d.CaseType, DepartmentId = d.DepartmentId, DepartmentName = d.Department?.Name, SubCategory = d.SubCategory, Priority = d.Priority,
        PreferredLanguage = d.PreferredLanguage, CaseCount = cases, CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
    };

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
        return new EmailCaseOptionsDto { CaseTypes = types, Departments = deptOptions, Priorities = priorities, Languages = languages };
    }
}
