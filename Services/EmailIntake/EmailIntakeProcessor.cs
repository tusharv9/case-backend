namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

/// <summary>
/// Email intake: reads each enabled mailbox, stores every new email once, and opens a TICKET for each new request — never a case.
/// A person assigns the ticket (Tickets), which makes the case. A sender from a verified domain is marked verified, gets that
/// domain's suggested case details on the ticket, and is told the ticket number; anyone else's ticket is marked not verified.
/// </summary>
/// <remarks>
/// <para><b>Once, whatever happens.</b> An email is stored once (its Message-ID is unique), claimed before it is worked on (a
/// version check, so the worker and a person pressing "Check again" cannot both act), and its ticket is written in the same
/// save as the link to it — so there is never a ticket without its email, or a second ticket for the same email.</para>
/// <para><b>Nothing undoes a ticket.</b> The acknowledgement is sent after the ticket is saved; its failure is recorded on the
/// email (and retried), never rolled back into the ticket.</para>
/// <para><b>A reply is not a new ticket.</b> An email answering one we already have (In-Reply-To/References, including our own
/// acknowledgement), or quoting its "[Ticket T-00012]", is added to that ticket — or, once the ticket became a case, to the case.</para>
/// </remarks>
public sealed class EmailIntakeProcessor(
    AppDbContext db,
    IAttachmentService attachments,
    IAttachmentStore store,
    IMailboxClient mail,
    IMailboxSecretProtector secrets,
    IEmailAuthenticator authenticator,
    IOptions<EmailIntakeOptions> options,
    ILogger<EmailIntakeProcessor> logger,
    ILiveEvents? live = null,
    Notifications.ICaseNotificationSink? notifications = null)
{
    private readonly EmailIntakeOptions _options = options.Value;
    public const string SystemUserEmail = "email-integration@casemanagement.system";
    public const string SystemUserName = "Email Integration";

    // ───────────────────────────── one cycle ─────────────────────────────

    /// <summary>Reads every mailbox that is due, then works through the emails and acknowledgements waiting. Returns emails read.</summary>
    public async Task<int> RunCycleAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.EmailMailboxes.Where(m => m.IsEnabled && m.PasswordEncrypted != null && (m.NextSyncAt == null || m.NextSyncAt <= now))
            .Select(m => m.Id).ToListAsync(ct);
        var read = 0;
        foreach (var id in due) read += await SyncMailboxAsync(id, ct);

        await ProcessPendingAsync(ct);
        await RetryAcknowledgementsAsync(ct);
        return read;
    }

    /// <summary>Reads one mailbox's new emails and stores them. A failure is recorded on the mailbox and retried later, with back-off.</summary>
    public async Task<int> SyncMailboxAsync(Guid mailboxId, CancellationToken ct)
    {
        var mailbox = await db.EmailMailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct);
        if (mailbox == null || !mailbox.IsEnabled || mailbox.PasswordEncrypted == null) return 0;

        if (mailbox.NextSyncAt > DateTime.UtcNow) return 0;   // another server is reading it, or it is backing off

        // Claim the read (a lease of 10 minutes). A server that loses the version check leaves the mailbox alone.
        mailbox.NextSyncAt = DateTime.UtcNow.AddMinutes(10);
        mailbox.Version++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return 0; }

        var read = 0;
        mailbox.LastSyncAt = DateTime.UtcNow;
        try
        {
            var connection = Connect(mailbox);
            var own = await OwnAddressesAsync(ct);
            var cursor = mailbox.LastUid is { } uid && mailbox.UidValidity is { } validity ? new MailboxCursor(validity, uid) : null;
            var end = await mail.ReadNewAsync(connection, cursor, mailbox.LastSuccessAt, _options.MaxMessagesPerCycle, _options.MaxMessageBytes,
                async (incoming, position) =>
                {
                    if (await IngestAsync(mailbox, incoming, own, ct)) read++;
                    mailbox.UidValidity = position.UidValidity;
                    mailbox.LastUid = position.LastUid;
                    await db.SaveChangesAsync(ct);   // the cursor moves with each email, so a failure part-way never reads one twice
                }, ct);
            mailbox.UidValidity = end.UidValidity;
            mailbox.LastUid = end.LastUid;
            mailbox.Status = MailboxStatuses.Connected;
            mailbox.LastSuccessAt = DateTime.UtcNow;
            mailbox.LastError = null;
            mailbox.ConsecutiveFailures = 0;
            mailbox.NextSyncAt = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            mailbox.Status = MailboxStatuses.Failing;
            mailbox.LastError = EmailParsing.Truncate(MailKitMailboxClient.Describe(ex), 1000);
            mailbox.ConsecutiveFailures++;
            // 1, 2, 4… minutes, at most 30, so a wrong password is not retried every cycle (and the account not locked by the provider).
            mailbox.NextSyncAt = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, Math.Min(5, mailbox.ConsecutiveFailures - 1))));
            logger.LogWarning(ex, "Mailbox {Address} could not be read ({Failures} in a row).", mailbox.Address, mailbox.ConsecutiveFailures);
        }
        await db.SaveChangesAsync(ct);
        if (read > 0) Publish(null);
        return read;
    }

    /// <summary>Stores one email read from a mailbox. False when it was already stored (the same email delivered again).</summary>
    public async Task<bool> IngestAsync(EmailMailbox mailbox, IncomingMail incoming, IReadOnlyCollection<string> ownAddresses, CancellationToken ct)
    {
        var m = incoming.Message;
        var key = m != null ? EmailParsing.MessageKey(m, mailbox.Address)
            : EmailParsing.NormalizeMessageId(incoming.Envelope?.MessageId) ?? $"uid:{mailbox.Id:N}:{incoming.Uid}";
        if (await db.InboundEmails.AnyAsync(e => e.MessageKey == key, ct))
        {
            logger.LogInformation("Mailbox {Address}: email {Key} was already stored; skipped.", mailbox.Address, key);
            return false;
        }

        var email = new InboundEmail { MailboxId = mailbox.Id, MessageKey = key, ReceivedAt = DateTime.UtcNow, SizeBytes = incoming.SizeBytes, CreatedAt = DateTime.UtcNow };
        if (m == null)
        {
            // Too large to read: keep what the envelope says so a person can see it arrived, and fetch it from the mailbox themselves.
            var env = incoming.Envelope;
            email.InternetMessageId = EmailParsing.NormalizeMessageId(env?.MessageId);
            email.FromAddress = env?.From.Mailboxes.FirstOrDefault()?.Address?.ToLowerInvariant() ?? "";
            email.FromName = env?.From.Mailboxes.FirstOrDefault()?.Name;
            email.Subject = env?.Subject ?? "";
            email.SentAt = env?.Date?.UtcDateTime;
            email.Status = EmailStatuses.Failed;
            email.StatusNote = $"The email is larger than {_options.MaxMessageBytes / (1024 * 1024)} MB, so it was not read. Open it in the mailbox.";
            email.Verification = SenderVerification.Pending;
            email.AckStatus = AcknowledgementStatuses.Skipped;
        }
        else
        {
            var from = m.From.Mailboxes.FirstOrDefault();
            email.InternetMessageId = EmailParsing.NormalizeMessageId(m.MessageId);
            email.InReplyTo = EmailParsing.NormalizeMessageId(m.InReplyTo);
            email.References = m.References.Count == 0 ? null : string.Join(' ', m.References.Select(EmailParsing.NormalizeMessageId).OfType<string>());
            email.FromAddress = from?.Address?.Trim().ToLowerInvariant() ?? "";
            email.FromName = string.IsNullOrWhiteSpace(from?.Name) ? null : from!.Name.Trim();
            email.ToList = string.Join(", ", m.To.Mailboxes.Select(a => a.ToString()));
            email.CcList = m.Cc.Count == 0 ? null : string.Join(", ", m.Cc.Mailboxes.Select(a => a.ToString()));
            email.Subject = (m.Subject ?? "").Trim();
            email.BodyText = EmailParsing.Truncate(EmailParsing.BodyText(m), 200_000);
            email.Snippet = EmailParsing.Snippet(email.BodyText);
            email.SentAt = m.Date == DateTimeOffset.MinValue ? null : m.Date.UtcDateTime;
            var files = EmailParsing.RealAttachments(m).Select((a, i) => EmailParsing.AttachmentName(a, i + 1)).ToList();
            email.AttachmentCount = files.Count;
            email.AttachmentNames = files.Count == 0 ? null : string.Join('\n', files);
            email.Kind = EmailParsing.Kind(m, ownAddresses);

            using var raw = new MemoryStream();
            await m.WriteToAsync(raw, ct);
            if (email.SizeBytes == 0) email.SizeBytes = raw.Length;
            raw.Position = 0;
            email.RawKey = await store.SaveAsync(raw, ".eml", ct);
        }

        db.InboundEmails.Add(email);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(email).State = EntityState.Detached;
            if (email.RawKey != null) await store.DeleteAsync(email.RawKey, CancellationToken.None);
            // Stored by someone else at the same moment (two servers): theirs stands. Anything else is a real failure.
            if (await db.InboundEmails.AsNoTracking().AnyAsync(e => e.MessageKey == key, ct)) return false;
            throw;
        }
        return true;
    }

    // ───────────────────────────── processing ─────────────────────────────

    /// <summary>Works through the emails waiting: new ones, failed ones due for another try, and ones whose worker stopped part-way.</summary>
    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = await db.InboundEmails
            .Where(e => e.Status == EmailStatuses.Received
                || (e.Status == EmailStatuses.Processing && e.NextAttemptAt <= now)
                || (e.Status == EmailStatuses.Failed && e.NextAttemptAt != null && e.NextAttemptAt <= now && e.Attempts < _options.MaxAttempts))
            .OrderBy(e => e.ReceivedAt).Select(e => e.Id).Take(100).ToListAsync(ct);
        foreach (var id in ids) await ProcessAsync(id, manual: false, ct);
    }

    /// <summary>
    /// Decides what one email becomes: ignored (auto-reply, bounce, our own mail), added to the case or ticket it answers, or a new
    /// ticket. <paramref name="manual"/> is a person pressing "Check again".
    /// </summary>
    public async Task<InboundEmail?> ProcessAsync(Guid emailId, bool manual, CancellationToken ct)
    {
        var email = await db.InboundEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email == null) return null;
        if (email.Status is EmailStatuses.CaseCreated or EmailStatuses.LinkedToCase or EmailStatuses.Ignored
            or EmailStatuses.TicketCreated or EmailStatuses.LinkedToTicket) return email;
        if (email.RawKey == null) return email;   // too large to read: nothing to work with
        if (email.Status == EmailStatuses.Processing && email.NextAttemptAt > DateTime.UtcNow) return email;   // someone is on it

        // Claim it. A second claimant fails the version check and leaves it alone.
        email.Status = EmailStatuses.Processing;
        email.NextAttemptAt = DateTime.UtcNow.AddMinutes(10);
        email.Attempts = manual ? 1 : email.Attempts + 1;
        email.Version++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return await db.InboundEmails.AsNoTracking().FirstOrDefaultAsync(e => e.Id == emailId, ct);
        }

        try
        {
            var message = await LoadAsync(email, ct);
            await DecideAsync(email, message, ct);
        }
        catch (TransientCheckException ex)
        {
            email = await AfterFailureAsync(emailId, ex.Message, retry: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Email {EmailId} could not be processed.", emailId);
            email = await AfterFailureAsync(emailId, "Processing failed: " + EmailParsing.Truncate(ex.Message, 500), retry: true);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        Publish(email);
        return email;
    }

    /// <summary>
    /// After a failure, forgets everything the attempt left unsaved (a rolled-back case must never be saved later by accident) and
    /// records the failure — unless the case was in fact saved before the failure, in which case the email keeps it.
    /// </summary>
    private async Task<InboundEmail> AfterFailureAsync(Guid emailId, string note, bool retry)
    {
        db.ChangeTracker.Clear();
        var fresh = await db.InboundEmails.FirstAsync(e => e.Id == emailId, CancellationToken.None);
        if (fresh.Status is EmailStatuses.CaseCreated or EmailStatuses.LinkedToCase or EmailStatuses.TicketCreated or EmailStatuses.LinkedToTicket) return fresh;
        Fail(fresh, note, retry);
        return fresh;
    }

    private async Task DecideAsync(InboundEmail email, MimeMessage message, CancellationToken ct)
    {
        if (email.Kind != EmailKinds.Normal)
        {
            email.Status = EmailStatuses.Ignored;
            email.StatusNote = email.Kind switch
            {
                EmailKinds.AutoReply => "An automatic reply (e.g. out of office); no ticket is opened for it.",
                EmailKinds.Bounce => "A delivery failure notice; no ticket is opened for it.",
                _ => "Sent by one of our own mailboxes; no ticket is opened for it.",
            };
            email.AckStatus = AcknowledgementStatuses.Skipped;
            return;
        }

        // Start from nothing on every attempt ("Check again" may follow a change to the domain's settings).
        email.OrganisationId = null;
        email.VerifiedSenderId = null;
        email.SenderAlias = null;
        email.Authenticity = EmailAuthenticity.NotChecked;
        email.AuthenticationDetail = null;

        // Who is it from: the domain names the organisation; the policy and the email's signature decide whether the sender is verified.
        var senderDomain = EmailParsing.DomainOf(email.FromAddress);
        var domains = await db.EmailVerifiedDomains.AsNoTracking().Include(d => d.Organisation).Where(d => d.IsActive).ToListAsync(ct);
        var domain = EmailParsing.MatchDomain(senderDomain, domains, d => d.Domain, d => d.IncludeSubdomains);
        var verified = false;
        string? refusal = null;
        if (domain != null)
        {
            email.OrganisationId = domain.OrganisationId;
            var org = OrganisationOf(domain);

            var listed = await db.EmailVerifiedSenders.AsNoTracking()
                .FirstOrDefaultAsync(s => s.VerifiedDomainId == domain.Id && s.Address == email.FromAddress && s.IsActive, ct);
            email.VerifiedSenderId = listed?.Id;
            email.SenderAlias = listed?.DisplayName;

            var auth = await authenticator.CheckAsync(message, senderDomain, ct);
            email.Authenticity = auth.Result;
            email.AuthenticationDetail = auth.Detail;

            if (auth.Result == EmailAuthenticity.Fail)
                refusal = $"The email claims to be from {senderDomain} ({org}), but it does not carry that domain's valid signature and the domain's own policy "
                    + $"refuses such mail, or its signature was broken. It may be forged. ({auth.Detail})";
            else if (domain.RequireAuthentication && auth.Result == EmailAuthenticity.Unknown)
                throw new TransientCheckException($"The signature of {senderDomain} could not be checked (DNS did not answer); it will be checked again.");
            else if (domain.RequireAuthentication && auth.Result != EmailAuthenticity.Pass)
                refusal = $"{org} requires a valid signature (DKIM) of {senderDomain}, and this email has none. ({auth.Detail})";
            else if (domain.VerificationPolicy == VerificationPolicies.SpecificSenders && listed == null)
                refusal = $"{email.FromAddress} is not on {org}'s list of senders. Add it under Maintenance → Verified Domains → {domain.Domain}, then press Check again.";
            else
                verified = true;
        }

        // A reply on a thread we already have a case for goes to that case — whoever sent it (they were sent our email, or copied
        // on it) — unless the email is forged: a forged reply must not reach a client's case.
        var forged = email.Authenticity == EmailAuthenticity.Fail;
        var threadCase = forged ? null : await ThreadCaseAsync(email, ct);
        if (threadCase == null && verified && EmailParsing.CaseNumberInSubject(email.Subject) is { } quoted)
            threadCase = await QuotedCaseAsync(quoted, domain!.Id, ct);
        if (threadCase != null)
        {
            await LinkToCaseAsync(email, message, threadCase.Value, verified ? domain : null, refusal, ct);
            return;
        }

        // How the sender stands, for the ticket and the Inbox: verified, not on the domain's list, or not verified (and why).
        var verification = verified ? SenderVerification.Verified
            : domain != null && domain.VerificationPolicy == VerificationPolicies.SpecificSenders && email.VerifiedSenderId == null && !forged
                ? SenderVerification.NotListed : SenderVerification.NotVerified;
        var note = verified ? VerifiedNote(email, domain!)
            : refusal ?? (senderDomain.Length == 0 ? "The email has no sender address."
                : $"{senderDomain} is not a verified domain. Check who sent it before acting on it; add the domain under Maintenance → Verified Domains to mark its senders verified.");

        // A reply on a ticket: added to it while it waits, or to its case once it was assigned. A closed ticket starts a new one.
        // A forged email is never added to someone's ticket or case: it opens its own ticket, marked not verified.
        if (!forged && await ThreadTicketAsync(email, ct) is { } threadTicket)
        {
            if (threadTicket.Status == TicketStatuses.New)
            {
                await LinkToTicketAsync(email, message, threadTicket, verified ? domain : null, verification, note, ct);
                return;
            }
            if (threadTicket is { Status: TicketStatuses.Converted, CaseId: { } ticketCase })
            {
                await LinkToCaseAsync(email, message, ticketCase, verified ? domain : null, refusal, ct);
                return;
            }
        }

        // Anything else is a new request: it becomes a ticket, never a case. A person assigns it (Tickets), which makes the case.
        var mailbox = await db.EmailMailboxes.AsNoTracking().FirstAsync(m => m.Id == email.MailboxId, ct);
        await CreateTicketAsync(email, message, mailbox, verified ? domain : null, verification, note, ct);
    }

    private static string OrganisationOf(EmailVerifiedDomain domain) => domain.Organisation?.Name ?? domain.OrganisationName;

    /// <summary>How the sender was verified, in words for the Inbox.</summary>
    private static string VerifiedNote(InboundEmail email, EmailVerifiedDomain domain)
    {
        var who = email.SenderAlias != null
            ? $"{email.SenderAlias} ({email.FromAddress}) is a listed sender of {OrganisationOf(domain)}."
            : $"{EmailParsing.DomainOf(email.FromAddress)} is verified ({OrganisationOf(domain)}).";
        var proof = email.Authenticity switch
        {
            EmailAuthenticity.Pass => " The email carries the domain's valid signature.",
            EmailAuthenticity.Unknown => " Its signature could not be checked (DNS did not answer).",
            _ => " The email carries no signature of the domain, so the From address itself is not proven.",
        };
        return who + proof;
    }

    private async Task AfterSavedAsync(string what, Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The email's case or ticket was saved, but could not {What}.", what);
        }
    }

    private async Task LinkToCaseAsync(InboundEmail email, MimeMessage message, Guid caseId, EmailVerifiedDomain? domain, string? refusal, CancellationToken ct)
    {
        var systemUser = await SystemUserAsync(ct);
        var excerpt = EmailParsing.Truncate(email.BodyText, 4000);
        db.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId, EventType = EventType.Comment, Channel = "Email", IsInternal = false, CreatedAt = DateTime.UtcNow, UserId = systemUser,
            Module = "EmailIntake", ActionType = "EMAIL_REPLY_RECEIVED",
            Message = $"Email received from {Sender(email)}: {email.Subject}\n\n{excerpt}",
        });
        email.CaseId = caseId;
        email.Status = EmailStatuses.LinkedToCase;
        email.StatusNote = "A reply on an existing case's email thread; added to that case.";
        email.Verification = domain != null ? SenderVerification.Verified : SenderVerification.NotVerified;
        email.VerifiedDomainId = domain?.Id;
        email.VerificationNote = domain != null ? VerifiedNote(email, domain)
            : (refusal ?? "The sender's domain is not verified") + " The email answers this case's thread, so it was added to the case.";
        email.NextAttemptAt = null;
        email.AckStatus = AcknowledgementStatuses.Skipped;   // replies are not acknowledged again
        await db.SaveChangesAsync(ct);
        await AfterSavedAsync("attach the reply's files", () => AddAttachmentsAsync(email, message, caseId, systemUser, ct));
    }

    // ───────────────────────────── tickets ─────────────────────────────

    /// <summary>
    /// Opens a ticket for the email (Tickets mode): who asked, what they asked, its files, the mailbox's suggested classification.
    /// Saved in one transaction with the email's link to it, so there is never a ticket without its email or a second ticket for it.
    /// A verified sender is told the ticket number; anyone else is not answered (we do not mail unknown addresses).
    /// </summary>
    private async Task CreateTicketAsync(InboundEmail email, MimeMessage message, EmailMailbox mailbox, EmailVerifiedDomain? domain, string verification, string note, CancellationToken ct)
    {
        var systemUser = await SystemUserAsync(ct);
        var body = email.BodyText;
        if (body.Length > _options.MaxDescriptionLength)
            body = body[..(_options.MaxDescriptionLength - 120)].TrimEnd() + "\n\n[The email continues; the full email is kept in the Inbox.]";

        var ticket = new Ticket
        {
            Source = TicketSources.Email, MailboxId = mailbox.Id, InboundEmailId = email.Id,
            RequesterName = EmailParsing.Truncate(email.SenderAlias ?? email.FromName ?? email.FromAddress.Split('@')[0], 200),
            RequesterEmail = email.FromAddress,
            Subject = EmailParsing.Truncate(string.IsNullOrWhiteSpace(email.Subject) ? $"Email from {email.FromAddress}" : email.Subject, 500),
            Description = string.IsNullOrWhiteSpace(body) ? "(The email has no text.)" : body,
            // A domain says who wrote, not what about: the mailbox's suggestions are the starting point; the assigner decides.
            CaseType = mailbox.DefaultCaseType,
            DepartmentId = mailbox.DefaultDepartmentId,
            SubCategory = mailbox.DefaultSubCategory,
            Priority = mailbox.DefaultPriority,
            SenderVerification = verification,
            SenderNote = EmailParsing.Truncate(note, 1000),
            ReceivedAt = email.ReceivedAt, CreatedById = systemUser, CreatedAt = DateTime.UtcNow,
        };
        ticket.Attachments.AddRange(await StoreAttachmentsAsync(message, ct));

        email.TicketId = ticket.Id;
        email.Status = EmailStatuses.TicketCreated;
        email.StatusNote = null;
        email.Verification = verification;
        email.VerificationNote = note;
        email.VerifiedDomainId = domain?.Id;
        email.NextAttemptAt = null;
        email.AckStatus = mailbox.SendAcknowledgement && domain != null ? AcknowledgementStatuses.NotSent : AcknowledgementStatuses.Skipped;
        email.UpdatedAt = DateTime.UtcNow;
        await TicketNumbers.AddAsync(db, ticket, ct);   // saves the email's changes with it

        PublishTicket(ticket);
        await Tickets.TicketAlerts.NotifyReceivedAsync(db, notifications, ticket, mailbox.Name, logger, ct);
        if (email.AckStatus == AcknowledgementStatuses.NotSent)
            await AfterSavedAsync("send the acknowledgement", () => SendAcknowledgementAsync(email, ticket.TicketNumber, ct));
    }

    private async Task LinkToTicketAsync(InboundEmail email, MimeMessage message, Ticket ticket, EmailVerifiedDomain? domain, string verification, string note, CancellationToken ct)
    {
        var tracked = await db.Tickets.Include(t => t.Attachments).FirstAsync(t => t.Id == ticket.Id, ct);
        tracked.Attachments.AddRange((await StoreAttachmentsAsync(message, ct)).Select(a => { a.TicketId = tracked.Id; return a; }));
        tracked.Version++;
        tracked.UpdatedAt = DateTime.UtcNow;
        email.TicketId = ticket.Id;
        email.Status = EmailStatuses.LinkedToTicket;
        email.StatusNote = $"A reply on ticket {ticket.TicketNumber}'s email thread; added to that ticket.";
        email.Verification = verification;
        email.VerifiedDomainId = domain?.Id;
        email.VerificationNote = verification == SenderVerification.Verified ? note : note + " The email answers this ticket's thread, so it was added to the ticket.";
        email.NextAttemptAt = null;
        email.AckStatus = AcknowledgementStatuses.Skipped;
        await db.SaveChangesAsync(ct);
        PublishTicket(tracked);
    }

    /// <summary>The ticket of an email this one answers (one we received, or our acknowledgement), or one quoted as [Ticket T-00012] by the same sender.</summary>
    private async Task<Ticket?> ThreadTicketAsync(InboundEmail email, CancellationToken ct)
    {
        var ids = EmailParsing.ThreadIds(email.InReplyTo, email.References);
        Guid? ticketId = null;
        if (ids.Count > 0)
            ticketId = await db.InboundEmails.AsNoTracking()
                .Where(e => e.Id != email.Id && e.TicketId != null && ((e.InternetMessageId != null && ids.Contains(e.InternetMessageId)) || (e.AckMessageId != null && ids.Contains(e.AckMessageId))))
                .OrderBy(e => e.ReceivedAt).Select(e => e.TicketId).FirstOrDefaultAsync(ct);
        if (ticketId == null && EmailParsing.TicketNumberInSubject(email.Subject) is { } quoted)
            ticketId = await db.Tickets.AsNoTracking().Where(t => t.TicketNumber == quoted && t.RequesterEmail == email.FromAddress)
                .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
        if (ticketId == null) return null;
        return await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
    }

    /// <summary>
    /// Keeps the email's files for its ticket (they go to the case on assignment, through the case's own file checks). A file
    /// that cannot be read is left out here; the original email keeps every file.
    /// </summary>
    private async Task<List<TicketAttachment>> StoreAttachmentsAsync(MimeMessage message, CancellationToken ct)
    {
        var stored = new List<TicketAttachment>();
        var index = 0;
        foreach (var entity in EmailParsing.RealAttachments(message))
        {
            var name = EmailParsing.AttachmentName(entity, ++index);
            try
            {
                using var content = new MemoryStream();
                if (entity is MessagePart mp && mp.Message != null) await mp.Message.WriteToAsync(content, ct);
                else if (entity is MimePart part && part.Content != null) await part.Content.DecodeToAsync(content, ct);
                if (content.Length == 0) continue;
                var size = content.Length;
                content.Position = 0;
                var key = await store.SaveAsync(content, Path.GetExtension(name), ct);
                stored.Add(new TicketAttachment { FileName = name, ContentType = entity.ContentType?.MimeType ?? "application/octet-stream", SizeBytes = size, StorageKey = key });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A file of an email could not be kept for its ticket.");
            }
        }
        return stored;
    }

    private void PublishTicket(Ticket t)
    {
        try { live?.Publish(new LiveEvent("tickets", new { ticketId = t.Id, mailboxId = t.MailboxId, status = t.Status })); }
        catch (Exception ex) { logger.LogDebug(ex, "Tickets live update not published."); }
    }

    /// <summary>The case of an email this one answers: one we received, or our acknowledgement of it.</summary>
    private async Task<Guid?> ThreadCaseAsync(InboundEmail email, CancellationToken ct)
    {
        var ids = EmailParsing.ThreadIds(email.InReplyTo, email.References);
        if (ids.Count == 0) return null;
        return await db.InboundEmails.AsNoTracking()
            .Where(e => e.Id != email.Id && e.CaseId != null && ((e.InternetMessageId != null && ids.Contains(e.InternetMessageId)) || (e.AckMessageId != null && ids.Contains(e.AckMessageId))))
            .OrderBy(e => e.ReceivedAt).Select(e => e.CaseId).FirstOrDefaultAsync(ct);
    }

    /// <summary>A case quoted in the subject — only when it was opened from an email of the same verified organisation, so nobody can add to another client's case by naming it.</summary>
    private async Task<Guid?> QuotedCaseAsync(string caseNumber, Guid domainId, CancellationToken ct) =>
        await db.InboundEmails.AsNoTracking()
            .Where(e => (e.Status == EmailStatuses.CaseCreated || e.Status == EmailStatuses.TicketCreated) && e.VerifiedDomainId == domainId && e.Case != null && e.Case.CaseNumber == caseNumber)
            .Select(e => e.CaseId).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Adds the email's files to the case through the same checks as a file uploaded on the case (size, type, content). A file
    /// refused is named on the case's timeline and the email, never silently dropped; the original email keeps every file.
    /// </summary>
    private async Task AddAttachmentsAsync(InboundEmail email, MimeMessage message, Guid caseId, Guid systemUser, CancellationToken ct)
    {
        var refused = new List<string>();
        var index = 0;
        foreach (var entity in EmailParsing.RealAttachments(message))
        {
            var name = EmailParsing.AttachmentName(entity, ++index);
            try
            {
                using var content = new MemoryStream();
                if (entity is MessagePart mp && mp.Message != null) await mp.Message.WriteToAsync(content, ct);
                else if (entity is MimePart part && part.Content != null) await part.Content.DecodeToAsync(content, ct);
                if (content.Length == 0) { refused.Add($"{name} (empty)"); continue; }
                content.Position = 0;
                var file = new FormFile(content, 0, content.Length, "file", name)
                {
                    Headers = new HeaderDictionary(),
                    ContentType = entity.ContentType?.MimeType ?? "application/octet-stream",
                };
                await attachments.UploadAttachmentAsync(caseId, file, "From the email", systemUser, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                refused.Add($"{name} ({EmailParsing.Truncate(ex.Message, 150)})");
            }
        }
        if (refused.Count == 0) return;
        db.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId, EventType = EventType.Other, Channel = "Email", IsInternal = true, CreatedAt = DateTime.UtcNow, UserId = systemUser,
            Module = "EmailIntake", ActionType = "EMAIL_ATTACHMENT_REFUSED",
            Message = "Not added from the email (they stay in the original email in the Inbox): " + string.Join("; ", refused),
        });
        email.StatusNote = EmailParsing.Truncate((email.StatusNote == null ? "" : email.StatusNote + " ") + "Files not added to the case: " + string.Join("; ", refused), 2000);
        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── acknowledgement ─────────────────────────────

    /// <summary>Tells the sender their case number, as a reply on their email's thread.</summary>
    public async Task SendAcknowledgementAsync(InboundEmail email, string caseNumber, CancellationToken ct)
    {
        var mailbox = await db.EmailMailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == email.MailboxId, ct);
        if (mailbox == null || !mailbox.SendAcknowledgement || mailbox.PasswordEncrypted == null)
        {
            email.AckStatus = AcknowledgementStatuses.Skipped;
            email.AckError = mailbox == null ? null : "Acknowledgements are switched off for this mailbox.";
            await db.SaveChangesAsync(ct);
            return;
        }
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        var recent = await db.InboundEmails.CountAsync(e => e.Id != email.Id && e.FromAddress == email.FromAddress
            && e.AckStatus == AcknowledgementStatuses.Sent && e.AckSentAt >= hourAgo, ct);
        if (recent >= _options.MaxAcknowledgementsPerSenderPerHour)
        {
            email.AckStatus = AcknowledgementStatuses.Skipped;
            email.AckError = $"Not sent: {email.FromAddress} was already sent {recent} acknowledgements in the last hour.";
            await db.SaveChangesAsync(ct);
            return;
        }

        var ack = BuildAcknowledgement(mailbox, email, caseNumber);
        // Recorded before sending, so a reply to it is recognised even if saving after the send fails.
        email.AckMessageId = EmailParsing.NormalizeMessageId(ack.MessageId);
        email.AckAttempts++;
        await db.SaveChangesAsync(ct);
        try
        {
            await mail.SendAsync(Connect(mailbox), ack, ct);
            email.AckStatus = AcknowledgementStatuses.Sent;
            email.AckSentAt = DateTime.UtcNow;
            email.AckError = null;
            email.AckNextAttemptAt = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Acknowledgement for email {EmailId} could not be sent.", email.Id);
            email.AckStatus = AcknowledgementStatuses.Failed;
            email.AckError = EmailParsing.Truncate(MailKitMailboxClient.Describe(ex), 1000);
            email.AckNextAttemptAt = email.AckAttempts >= _options.MaxAttempts ? null : DateTime.UtcNow.Add(Backoff(email.AckAttempts));
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public static MimeMessage BuildAcknowledgement(EmailMailbox mailbox, InboundEmail email, string caseNumber)
    {
        var name = string.IsNullOrWhiteSpace(email.FromName) ? "Sir/Madam" : email.FromName;
        var subject = string.IsNullOrWhiteSpace(email.Subject) ? "(no subject)" : email.Subject;
        var signature = string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : mailbox.Name;
        var ack = new MimeMessage();
        ack.From.Add(new MailboxAddress(mailbox.Name, mailbox.Address));
        ack.To.Add(new MailboxAddress(email.FromName ?? "", email.FromAddress));
        ack.Subject = $"{EmailParsing.ReplySubject(subject)} [Ticket {caseNumber}]";
        ack.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(EmailParsing.DomainOf(mailbox.Address) is { Length: > 0 } d ? d : "casemanagement.local");
        if (email.InternetMessageId != null)
        {
            ack.InReplyTo = email.InternetMessageId;
            foreach (var id in EmailParsing.ThreadIds(null, email.References)) ack.References.Add(id);
            ack.References.Add(email.InternetMessageId);
        }
        // Marks it automatic, so the sender's out-of-office does not answer it (and we would not answer theirs anyway).
        ack.Headers.Add(HeaderId.AutoSubmitted, "auto-replied");
        ack.Headers.Add("X-Auto-Response-Suppress", "All");

        var text =
            $"Dear {name},\n\n" +
            "Thank you for contacting us. We have received your email and created a ticket for it.\n\n" +
            $"Ticket ID: {caseNumber}\nSubject: {subject}\n\n" +
            "Our team will get back to you. When you write to us about this request, please keep the ticket ID in the subject line.\n\n" +
            "This is an automated message.\n\n" +
            $"Regards,\n{signature}";
        string H(string s) => System.Net.WebUtility.HtmlEncode(s);
        var html =
            "<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#1f2937;line-height:1.5\">" +
            $"<p>Dear {H(name)},</p>" +
            "<p>Thank you for contacting us. We have received your email and created a ticket for it.</p>" +
            "<table style=\"border-collapse:collapse;margin:12px 0\">" +
            $"<tr><td style=\"padding:4px 16px 4px 0;color:#6b7280\">Ticket ID</td><td style=\"padding:4px 0;font-weight:bold;color:#1d4ed8\">{H(caseNumber)}</td></tr>" +
            $"<tr><td style=\"padding:4px 16px 4px 0;color:#6b7280\">Subject</td><td style=\"padding:4px 0\">{H(subject)}</td></tr></table>" +
            "<p>Our team will get back to you. When you write to us about this request, please keep the ticket ID in the subject line.</p>" +
            "<p style=\"color:#6b7280;font-size:12px\">This is an automated message.</p>" +
            $"<p>Regards,<br>{H(signature)}</p></div>";
        ack.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();
        return ack;
    }

    /// <summary>Sends the acknowledgements that failed earlier and are due again.</summary>
    public async Task RetryAcknowledgementsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.InboundEmails.Where(e => e.AckStatus == AcknowledgementStatuses.Failed && e.AckNextAttemptAt != null && e.AckNextAttemptAt <= now
                && (e.CaseId != null || e.TicketId != null))
            // An email that opened a ticket is acknowledged with the ticket's number (even after the ticket became a case).
            .Select(e => new { e.Id, CaseNumber = e.Status == EmailStatuses.TicketCreated && e.Ticket != null ? e.Ticket.TicketNumber : e.Case!.CaseNumber })
            .Take(50).ToListAsync(ct);
        foreach (var item in due)
        {
            var email = await db.InboundEmails.FirstAsync(e => e.Id == item.Id, ct);
            if (email.AckStatus != AcknowledgementStatuses.Failed || email.AckNextAttemptAt > now) continue;
            // Claim it, so two servers never send the same acknowledgement.
            email.AckNextAttemptAt = now.AddMinutes(10);
            email.Version++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); continue; }
            await SendAcknowledgementAsync(email, item.CaseNumber, ct);
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>
    /// The user Case Management records for what email intake does on its own (creating the case, attaching files). It has no
    /// OmniConnect account, so it can never sign in, and it is inactive so it is never offered for assignment or mentions.
    /// </summary>
    public async Task<Guid> SystemUserAsync(CancellationToken ct)
    {
        var id = await db.Users.Where(u => u.Email == SystemUserEmail).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        if (id is { } existing) return existing;
        var user = new User { Name = SystemUserName, Email = SystemUserEmail, Role = "System", IsActive = false, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(user).State = EntityState.Detached;
            return await db.Users.Where(u => u.Email == SystemUserEmail).Select(u => u.Id).FirstAsync(ct);
        }
        return user.Id;
    }

    private MailboxConnection Connect(EmailMailbox m) => ConnectionFor(m, secrets);

    /// <summary>How to reach a mailbox, with its password decrypted for the call. Shared with <see cref="EmailIntakeIdleWorker"/>.</summary>
    public static MailboxConnection ConnectionFor(EmailMailbox m, IMailboxSecretProtector secrets) => new(
        m.Address, string.IsNullOrWhiteSpace(m.Username) ? m.Address : m.Username!, secrets.Unprotect(m.PasswordEncrypted!),
        m.ImapHost, m.ImapPort, m.ImapSecurity, m.SmtpHost, m.SmtpPort, m.SmtpSecurity);

    private async Task<IReadOnlyCollection<string>> OwnAddressesAsync(CancellationToken ct) =>
        (await db.EmailMailboxes.AsNoTracking().Select(m => m.Address).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<MimeMessage> LoadAsync(InboundEmail email, CancellationToken ct)
    {
        await using var raw = await store.OpenReadAsync(email.RawKey!, ct);
        return await MimeMessage.LoadAsync(raw, ct);
    }

    private static string Sender(InboundEmail e) => string.IsNullOrWhiteSpace(e.FromName) ? e.FromAddress : $"{e.FromName} <{e.FromAddress}>";


    private void Fail(InboundEmail email, string note, bool retry)
    {
        email.Status = EmailStatuses.Failed;
        email.StatusNote = EmailParsing.Truncate(note, 2000);
        email.NextAttemptAt = retry && email.Attempts < _options.MaxAttempts ? DateTime.UtcNow.Add(Backoff(email.Attempts)) : null;
    }

    private static TimeSpan Backoff(int attempt) => attempt switch { <= 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5), 3 => TimeSpan.FromMinutes(30), _ => TimeSpan.FromHours(2) };

    private void Publish(InboundEmail? email)
    {
        try { live?.Publish(new LiveEvent("inbox", new { emailId = email?.Id, status = email?.Status })); }
        catch (Exception ex) { logger.LogDebug(ex, "Inbox live update not published."); }
    }
}

/// <summary>A check that could not be completed now (DNS unavailable) and is worth trying again later.</summary>
public sealed class TransientCheckException(string message) : Exception(message);
