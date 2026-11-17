namespace CaseManagement.Api.Services.EmailIntake;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

/// <summary>
/// Customer Reply by email: prepares the email for a reply (recipient, sending mailbox, subject, thread headers) in the same save
/// as its timeline entry, and <see cref="CaseEmailSender"/> sends it. The customer's answer threads back into the case through the
/// recorded Message-ID (<see cref="EmailIntakeProcessor"/> matches it).
/// </summary>
public static class CaseEmailOutbox
{
    /// <summary>Whether a Customer Reply on this case goes out by email: its channel (or the case's preferred one) is Email.</summary>
    public static bool IsEmailReply(Case c, string? channel) =>
        string.Equals((channel ?? c.PreferredCommunicationChannel)?.Trim(), "Email", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The mailbox replies go from: the case's (the one its email came to), else the one chosen, else the only enabled mailbox
    /// shared with the case's team. Refuses — before anything is saved — when there is no address to write to or no mailbox.
    /// </summary>
    public static async Task<EmailMailbox> ResolveMailboxAsync(AppDbContext db, Case c, Guid? chosenMailboxId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.ContactEmail))
            throw new FieldValidationException([new FieldError("message", "This case has no customer email address, so the reply cannot be emailed.")]);

        static bool Usable(EmailMailbox m) => m.IsEnabled && m.PasswordEncrypted != null;
        if (c.ReplyMailboxId is { } own && await db.EmailMailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == own, ct) is { } ownBox)
            return Usable(ownBox) ? ownBox
                : throw new FieldValidationException([new FieldError("mailboxId", $"{ownBox.Address}, which this case's emails go from, is switched off. Turn it on under Email Configuration.")]);

        var shared = await db.EmailMailboxes.AsNoTracking()
            .Where(m => m.IsEnabled && m.PasswordEncrypted != null && m.Teams.Any(t => t.TeamId == c.TeamId)).OrderBy(m => m.Name).ToListAsync(ct);
        if (chosenMailboxId is { } chosen)
            return shared.FirstOrDefault(m => m.Id == chosen)
                ?? throw new FieldValidationException([new FieldError("mailboxId", "Choose a mailbox this case's team works.")]);
        return shared.Count switch
        {
            1 => shared[0],
            0 => throw new FieldValidationException([new FieldError("mailboxId", "No mailbox to send from: share a mailbox with this case's team under Email Configuration → Inbound Mailboxes.")]),
            _ => throw new FieldValidationException([new FieldError("mailboxId", $"Choose the mailbox to send from: {string.Join(", ", shared.Select(m => m.Address))}.")]),
        };
    }

    /// <summary>
    /// What the Customer Reply box shows for an email case: the recipient, the subject, and the mailbox — or the mailboxes to choose
    /// from — and, when the reply cannot be emailed, why. Nothing is sent or saved.
    /// </summary>
    public static async Task<ReplyOptionsDto> OptionsAsync(AppDbContext db, Case c, CancellationToken ct)
    {
        if (!IsEmailReply(c, null)) return new ReplyOptionsDto(false, null, null, null, [], "The case's preferred channel is not Email: replies are recorded, not emailed.");
        if (string.IsNullOrWhiteSpace(c.ContactEmail)) return new ReplyOptionsDto(false, null, null, null, [], "This case has no customer email address, so replies cannot be emailed.");
        var (subject, _, _) = await ThreadAsync(db, c, ct);
        static MailboxOptionDto Of(EmailMailbox m) => new(m.Id, m.Name, m.Address);
        if (c.ReplyMailboxId is { } own && await db.EmailMailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.Id == own, ct) is { } ownBox)
            return ownBox.IsEnabled && ownBox.PasswordEncrypted != null
                ? new ReplyOptionsDto(true, c.ContactEmail, subject, Of(ownBox), [], null)
                : new ReplyOptionsDto(false, c.ContactEmail, subject, Of(ownBox), [], $"{ownBox.Address}, which this case's emails go from, is switched off. Turn it on under Email Configuration.");
        var shared = await db.EmailMailboxes.AsNoTracking()
            .Where(m => m.IsEnabled && m.PasswordEncrypted != null && m.Teams.Any(t => t.TeamId == c.TeamId)).OrderBy(m => m.Name).ToListAsync(ct);
        return shared.Count switch
        {
            0 => new ReplyOptionsDto(false, c.ContactEmail, subject, null, [], "No mailbox to send from: share a mailbox with this case's team under Email Configuration → Inbound Mailboxes."),
            1 => new ReplyOptionsDto(true, c.ContactEmail, subject, Of(shared[0]), [], null),
            _ => new ReplyOptionsDto(true, c.ContactEmail, subject, null, shared.Select(Of).ToList(), null),
        };
    }

    /// <summary>
    /// Adds the email for a Customer Reply to the context (not saved): to the case's address, from <paramref name="mailbox"/>, answering
    /// the latest message of the case's email conversation so it threads in the customer's mail program. The case remembers the mailbox.
    /// </summary>
    public static async Task<CaseEmailMessage> StageReplyAsync(AppDbContext db, Case c, EmailMailbox mailbox, Guid caseEventId, string body, Guid userId, CancellationToken ct)
    {
        var (subject, inReplyTo, references) = await ThreadAsync(db, c, ct);
        var domain = EmailParsing.DomainOf(mailbox.Address) is { Length: > 0 } d ? d : "casemanagement.local";
        var message = new CaseEmailMessage
        {
            CaseId = c.Id, CaseEventId = caseEventId, MailboxId = mailbox.Id, ToAddress = c.ContactEmail!.Trim().ToLowerInvariant(),
            Subject = subject, BodyText = body,
            MessageId = EmailParsing.NormalizeMessageId(MimeKit.Utils.MimeUtils.GenerateMessageId(domain))!,
            InReplyTo = inReplyTo, References = references,
            Status = CaseEmailStatuses.Pending, NextAttemptAt = DateTime.UtcNow, SentById = userId == Guid.Empty ? null : userId,
        };
        db.CaseEmailMessages.Add(message);
        c.ReplyMailboxId ??= mailbox.Id;
        return message;
    }

    /// <summary>
    /// The next reply's subject ("Re: {first email's subject} [Case C-…]") and thread headers: it answers the latest message of the
    /// case's email conversation (received or sent), with the conversation's ids as references.
    /// </summary>
    public static async Task<(string Subject, string? InReplyTo, string? References)> ThreadAsync(AppDbContext db, Case c, CancellationToken ct)
    {
        // The conversation so far: the emails received on the case and the replies already sent, oldest first.
        var received = await db.InboundEmails.AsNoTracking().Where(e => e.CaseId == c.Id && e.InternetMessageId != null)
            .Select(e => new { Id = e.InternetMessageId!, At = e.ReceivedAt, e.Subject, e.References }).ToListAsync(ct);
        var sent = await db.CaseEmailMessages.AsNoTracking().Where(m => m.CaseId == c.Id)
            .Select(m => new { Id = m.MessageId, At = m.CreatedAt, m.Subject }).ToListAsync(ct);
        var thread = received.Select(r => (r.Id, r.At)).Concat(sent.Select(s => (s.Id, s.At))).OrderBy(x => x.At).ToList();
        var references = EmailParsing.ThreadIds(null, received.OrderBy(r => r.At).FirstOrDefault()?.References)
            .Concat(thread.Select(t => t.Id)).Distinct().TakeLast(20).ToList();

        var firstSubject = received.OrderBy(r => r.At).FirstOrDefault()?.Subject;
        var subject = EmailParsing.ReplySubject(string.IsNullOrWhiteSpace(firstSubject) ? c.Title : firstSubject);
        if (EmailParsing.CaseNumberInSubject(subject) != c.CaseNumber) subject = $"{subject} [Case {c.CaseNumber}]";
        return (EmailParsing.Truncate(subject, 500), thread.Count == 0 ? null : thread[^1].Id, references.Count == 0 ? null : string.Join(' ', references));
    }
}

/// <summary>
/// Sends the Customer Reply emails: straight after a reply is saved, and again later for any that failed (from the email intake
/// worker's cycle). Each send is claimed with a version check first, so two servers never send the same email.
/// </summary>
public sealed class CaseEmailSender(AppDbContext db, IMailboxClient mail, IMailboxSecretProtector secrets, IOptions<EmailIntakeOptions> options,
    ILogger<CaseEmailSender> logger)
{
    private readonly EmailIntakeOptions _options = options.Value;

    /// <summary>Sends the case's emails waiting to go (right after a reply). Never throws: a failure is recorded on the email.</summary>
    public async Task SendForCaseAsync(Guid caseId, CancellationToken ct)
    {
        var ids = await db.CaseEmailMessages.AsNoTracking().Where(m => m.CaseId == caseId && m.Status == CaseEmailStatuses.Pending)
            .Select(m => m.Id).ToListAsync(ct);
        foreach (var id in ids) await SendAsync(id, ct);
    }

    /// <summary>Everything due: new emails not sent yet, failures due a retry, and sends abandoned half-way (their lease ran out).</summary>
    public async Task SendDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = await db.CaseEmailMessages.AsNoTracking()
            .Where(m => (m.Status == CaseEmailStatuses.Pending || m.Status == CaseEmailStatuses.Failed || m.Status == CaseEmailStatuses.Sending)
                && m.NextAttemptAt != null && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt).Select(m => m.Id).Take(50).ToListAsync(ct);
        foreach (var id in ids) await SendAsync(id, ct);
    }

    /// <summary>A person pressing Retry on a failed email: it gets a fresh set of attempts and is sent now.</summary>
    public async Task<CaseEmailMessage> RetryAsync(Guid caseId, Guid messageId, CancellationToken ct)
    {
        var message = await db.CaseEmailMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.CaseId == caseId, ct)
                      ?? throw new KeyNotFoundException("Email not found.");
        if (message.Status != CaseEmailStatuses.Failed) return message;
        message.Status = CaseEmailStatuses.Pending;
        message.Attempts = 0;
        message.NextAttemptAt = DateTime.UtcNow;
        message.Version++;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await SendAsync(messageId, ct);
        return await db.CaseEmailMessages.AsNoTracking().FirstAsync(m => m.Id == messageId, ct);
    }

    private async Task SendAsync(Guid id, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var message = await db.CaseEmailMessages.Include(m => m.Mailbox).Include(m => m.Case).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message == null || message.Status == CaseEmailStatuses.Sent) return;
        if (message.NextAttemptAt == null || message.NextAttemptAt > DateTime.UtcNow) return;   // done, or someone is sending it now

        // Claim it: a second claimant fails the version check and leaves it alone. The lease covers a crash half-way.
        message.Status = CaseEmailStatuses.Sending;
        message.Attempts++;
        message.NextAttemptAt = DateTime.UtcNow.AddMinutes(5);
        message.Version++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return; }

        try
        {
            var mailbox = message.Mailbox ?? throw new InvalidOperationException("The mailbox this email was to be sent from was deleted.");
            if (!mailbox.IsEnabled || mailbox.PasswordEncrypted == null)
                throw new InvalidOperationException($"{mailbox.Address} is switched off or has no password.");
            await mail.SendAsync(EmailIntakeProcessor.ConnectionFor(mailbox, secrets), Build(message, mailbox), ct);
            message.Status = CaseEmailStatuses.Sent;
            message.SentAt = DateTime.UtcNow;
            message.Error = null;
            message.NextAttemptAt = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Customer reply email {MessageId} for case {CaseId} could not be sent.", message.Id, message.CaseId);
            var finalAttempt = message.Attempts >= _options.MaxAttempts;
            message.Status = CaseEmailStatuses.Failed;
            message.Error = EmailParsing.Truncate(MailKitMailboxClient.Describe(ex), 1000);
            message.NextAttemptAt = finalAttempt ? null : DateTime.UtcNow.AddMinutes(Math.Min(60, 2 * message.Attempts * message.Attempts));
            if (finalAttempt)
                db.CaseEvents.Add(new CaseEvent
                {
                    CaseId = message.CaseId, EventType = EventType.Other, IsInternal = true, CreatedAt = DateTime.UtcNow,
                    Module = "CustomerReply", ActionType = "EMAIL_REPLY_FAILED",
                    Message = $"The reply to {message.ToAddress} could not be sent after {message.Attempts} attempts: {message.Error} Press Retry on the reply to send it again.",
                });
        }
        message.Version++;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The email as the customer receives it: from the mailbox, threaded into their conversation, text and simple HTML.</summary>
    public static MimeMessage Build(CaseEmailMessage m, EmailMailbox mailbox)
    {
        var email = new MimeMessage();
        email.From.Add(new MailboxAddress(mailbox.Name, mailbox.Address));
        email.To.Add(MailboxAddress.Parse(m.ToAddress));
        email.Subject = m.Subject;
        email.MessageId = m.MessageId;
        if (!string.IsNullOrWhiteSpace(m.InReplyTo)) email.InReplyTo = m.InReplyTo;
        foreach (var id in (m.References ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) email.References.Add(id);
        var html = "<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#1f2937;line-height:1.5\">"
            + string.Join("", m.BodyText.Replace("\r\n", "\n").Split("\n\n").Select(p => $"<p>{System.Net.WebUtility.HtmlEncode(p).Replace("\n", "<br>")}</p>"))
            + "</div>";
        email.Body = new BodyBuilder { TextBody = m.BodyText, HtmlBody = html }.ToMessageBody();
        return email;
    }
}

public sealed record MailboxOptionDto(Guid Id, string Name, string Address);

/// <summary>
/// The Customer Reply box for an email case: <see cref="CanEmail"/>, the recipient and subject, the mailbox it goes from (or
/// <see cref="Mailboxes"/> to choose from when the case's team works several), and <see cref="Reason"/> when it cannot be emailed.
/// </summary>
public sealed record ReplyOptionsDto(bool CanEmail, string? To, string? Subject, MailboxOptionDto? Mailbox, List<MailboxOptionDto> Mailboxes, string? Reason);
