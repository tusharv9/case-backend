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
                || (e.SenderAlias != null && e.SenderAlias.ToLower().Contains(s)) || (e.Organisation != null && e.Organisation.NameKey.Contains(s))
                || (e.Case != null && e.Case.CaseNumber.ToLower().Contains(s)));
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
        var e = await db.InboundEmails.AsNoTracking().Include(x => x.Mailbox).Include(x => x.VerifiedDomain).Include(x => x.Organisation).Include(x => x.Case)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Email not found.");
        return new InboxEmailDetailDto
        {
            Id = e.Id, FromAddress = e.FromAddress, FromName = e.FromName, ToList = e.ToList, CcList = e.CcList, Subject = e.Subject, BodyText = e.BodyText,
            ReceivedAt = e.ReceivedAt, SentAt = e.SentAt, SizeBytes = e.SizeBytes, MailboxName = e.Mailbox?.Name ?? "", MailboxAddress = e.Mailbox?.Address ?? "",
            Attachments = (e.AttachmentNames ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
            Kind = e.Kind, Verification = e.Verification, VerificationNote = e.VerificationNote, OrganisationName = e.Organisation?.Name ?? e.VerifiedDomain?.OrganisationName,
            SenderAlias = e.SenderAlias, Authenticity = e.Authenticity, AuthenticationDetail = e.AuthenticationDetail,
            Status = e.Status, StatusNote = e.StatusNote, Attempts = e.Attempts, CaseNumber = e.Case?.CaseNumber, CaseId = e.CaseId,
            AckStatus = e.AckStatus, AckSentAt = e.AckSentAt, AckError = e.AckError, HasOriginal = e.RawKey != null,
            CanRecheck = e.RawKey != null && (e.Status is EmailStatuses.NotVerified or EmailStatuses.Failed),
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

    public async Task<MailboxDto> CreateMailboxAsync(SaveMailboxDto dto, Guid actingUserId, CancellationToken ct)
    {
        var errors = ValidateMailbox(dto, requirePassword: true);
        var address = dto.Address.Trim().ToLowerInvariant();
        if (await db.EmailMailboxes.AnyAsync(m => m.Address == address, ct)) errors.Add(new FieldError("address", "This mailbox is already configured."));
        if (errors.Count > 0) throw new FieldValidationException(errors);

        var mailbox = new EmailMailbox { CreatedAt = DateTime.UtcNow, CreatedByUserId = Actor(actingUserId) };
        Apply(mailbox, dto);
        db.EmailMailboxes.Add(mailbox);
        Audit(actingUserId, "MAILBOX_CREATED", "EmailMailbox", $"Mailbox {mailbox.Address} added.", null, Snapshot(mailbox, passwordChanged: true));
        await db.SaveChangesAsync(ct);
        return ToDto(mailbox, 0);
    }

    public async Task<MailboxDto> UpdateMailboxAsync(Guid id, SaveMailboxDto dto, Guid actingUserId, CancellationToken ct)
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
        return ToDto(mailbox, await db.InboundEmails.CountAsync(e => e.MailboxId == id, ct));
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
        var list = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Organisation).Include(d => d.Senders)
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

        var policy = string.IsNullOrWhiteSpace(dto.VerificationPolicy) ? VerificationPolicies.EntireDomain
            : VerificationPolicies.All.FirstOrDefault(p => string.Equals(p, dto.VerificationPolicy.Trim(), StringComparison.OrdinalIgnoreCase));
        if (policy == null) errors.Add(new("verificationPolicy", "Choose \"Entire domain\" or \"Specific email addresses only\"."));

        var senders = await ValidateSendersAsync(dto.Senders ?? new(), name, dto.IncludeSubdomains, domain.Id, errors, ct);
        if (policy == VerificationPolicies.SpecificSenders && !senders.Any(s => s.IsActive))
            errors.Add(new("senders", "Add at least one active sender, or choose \"Entire domain\"."));

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
        domain.VerificationPolicy = policy!;
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
            else if (row.Address != s.Address || row.DisplayName != s.DisplayName || row.Description != s.Description || row.IsActive != s.IsActive)
            {
                row.UpdatedAt = now;
                row.UpdatedByUserId = Actor(actingUserId);
            }
            row.Address = s.Address;
            row.DisplayName = s.DisplayName;
            row.Description = s.Description;
            row.IsActive = s.IsActive;
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

        var saved = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Organisation).Include(d => d.Senders)
            .FirstAsync(d => d.Id == domain.Id, ct);
        return ToDto(saved, await db.InboundEmails.CountAsync(e => e.VerifiedDomainId == domain.Id && e.Status == EmailStatuses.CaseCreated, ct));
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
            result.Add(new VerifiedSenderDto { Id = s.Id, Address = address, DisplayName = alias, Description = description, IsActive = s.IsActive });
        }

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
            .Select(s => new VerifiedSenderDto { Id = s.Id, Address = s.Address, DisplayName = s.DisplayName, Description = s.Description, IsActive = s.IsActive }).ToList(),
        CaseCount = cases, CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
    };

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
        PasswordChanged = passwordChanged,
    };

    private static object Snapshot(EmailVerifiedDomain d) => new
    {
        d.Domain, Organisation = d.Organisation?.Name ?? d.OrganisationName, d.IncludeSubdomains, d.IsActive, d.VerificationPolicy, d.RequireAuthentication,
        Senders = d.Senders.OrderBy(s => s.Address).Select(s => new { s.Address, s.DisplayName, s.Description, s.IsActive }).ToList(),
    };

    private static string CollapseSpaces(string value) => System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
}
