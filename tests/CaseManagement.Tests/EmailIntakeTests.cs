using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Customer360;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Email intake: every new email opens exactly one ticket (never a case); the sender's domain, its policy (entire domain or listed
/// senders) and the email's signature decide whether the ticket is marked verified; a verified sender is told the ticket number;
/// replies go to the ticket, or to its case once it was assigned — unless they are forged.
/// </summary>
/// <remarks>
/// Mail is delivered more than once, servers retry, people press buttons twice and clients reply to our acknowledgement — each of
/// those, done wrong, is a duplicate ticket or an email loop with a customer. And anyone can write any From line: a forged email
/// must never be marked verified, answered, or added to a client's ticket or case.
/// </remarks>
public class EmailIntakeTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly FakeMailbox mail = new();
    private readonly MemoryStore store = new();
    private readonly List<(Guid CaseId, string Name)> uploads = new();
    private readonly Guid department = Guid.NewGuid();
    private readonly EmailMailbox mailbox;
    private readonly IMailboxSecretProtector secrets;
    private EmailAuthenticationResult authResult = new(EmailAuthenticity.None, "dkim=none; dmarc=none (no policy published)");
    private readonly EmailVerifiedDomain sbi;
    private readonly EmailOrganisation sbiOrg;

    public EmailIntakeTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"email-intake-{Guid.NewGuid()}").Options);
        secrets = new MailboxSecretProtector(Options.Create(new EmailIntakeOptions { SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }),
            Options.Create(new CustomerIdentifierOptions()));
        mailbox = new EmailMailbox
        {
            Name = "RMA Support", Address = "support@rma.test", PasswordEncrypted = secrets.Protect("app-password"),
            ImapHost = "imap.rma.test", SmtpHost = "smtp.rma.test", IsEnabled = true, SendAcknowledgement = true, CreatedAt = DateTime.UtcNow,
            DefaultCaseType = "Enquiry",
        };
        db.EmailMailboxes.Add(mailbox);
        sbiOrg = new EmailOrganisation { Name = "State Bank of India", NameKey = "state bank of india", CreatedAt = DateTime.UtcNow };
        db.EmailOrganisations.Add(sbiOrg);
        sbi = new EmailVerifiedDomain
        {
            Domain = "sbi.co.in", OrganisationId = sbiOrg.Id, OrganisationName = "State Bank of India", CreatedAt = DateTime.UtcNow,
        };
        db.EmailVerifiedDomains.Add(sbi);
        // Case Settings, which email cases take their case type, department, sub-category, priority and language from.
        var team = new Team { Name = "Cards Team", Code = "CARDS", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        db.Departments.Add(new Department { Id = department, Name = "Cards", Code = "CARDS", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamDepartments.Add(new TeamDepartment { TeamId = team.Id, DepartmentId = department });
        db.DepartmentSubCategories.Add(new DepartmentSubCategory { DepartmentId = department, Name = "Cards", Code = "CARDS", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.CaseTypeConfigs.Add(new CaseTypeConfig { Code = "Complaint", Name = "Complaint", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.PrioritySlaRules.Add(new PrioritySlaRule { Priority = "High", DisplayOrder = 1, IsActive = true, CreatedAt = DateTime.UtcNow });
        db.LookupValues.Add(new LookupValue { TypeCode = "PREFERRED_LANGUAGE", Value = "English", Label = "English", IsActive = true });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    // ───────────── fakes ─────────────

    private EmailIntakeProcessor Processor()
    {
        var attachments = new Mock<IAttachmentService>();
        attachments.Setup(a => a.UploadAttachmentAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid caseId, IFormFile file, string? _, Guid _, CancellationToken _) =>
            {
                if (file.FileName.EndsWith(".exe")) throw new ArgumentException("This type of file is not allowed.");
                uploads.Add((caseId, file.FileName));
                return Task.FromResult(new CaseAttachmentDto { FileName = file.FileName });
            });
        return new EmailIntakeProcessor(db, attachments.Object, store, mail, secrets, new FakeAuthenticator(() => authResult), Options.Create(new EmailIntakeOptions()),
            NullLogger<EmailIntakeProcessor>.Instance);
    }

    private sealed class FakeAuthenticator(Func<EmailAuthenticationResult> result) : IEmailAuthenticator
    {
        public Task<EmailAuthenticationResult> CheckAsync(MimeMessage message, string fromDomain, CancellationToken ct) => Task.FromResult(result());
    }

    private void UseSpecificSenders(params (string Address, string Alias)[] senders)
    {
        var d = db.EmailVerifiedDomains.Single(x => x.Id == sbi.Id);
        d.VerificationPolicy = VerificationPolicies.SpecificSenders;
        foreach (var (address, alias) in senders)
            db.EmailVerifiedSenders.Add(new EmailVerifiedSender { VerifiedDomainId = d.Id, Address = address, DisplayName = alias, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private sealed class FakeMailbox : IMailboxClient
    {
        public List<MimeMessage> Inbox { get; } = new();
        public List<MimeMessage> Sent { get; } = new();
        public bool SendFails { get; set; }

        public async Task<MailboxCursor> ReadNewAsync(MailboxConnection mailbox, MailboxCursor? cursor, DateTime? since, int max, long maxBytes,
            Func<IncomingMail, MailboxCursor, Task> handle, CancellationToken ct)
        {
            var position = cursor ?? new MailboxCursor(1, 0);
            for (var uid = position.LastUid + 1; uid <= Inbox.Count; uid++)
            {
                position = position with { LastUid = uid };
                await handle(new IncomingMail(uid, Inbox[(int)uid - 1], 1000, null), position);
            }
            return position;
        }

        public Task SendAsync(MailboxConnection mailbox, MimeMessage message, CancellationToken ct)
        {
            if (SendFails) throw new TimeoutException();
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task<MailboxTestResult> TestAsync(MailboxConnection mailbox, CancellationToken ct) => Task.FromResult(new MailboxTestResult(true, null, true, null));
    }

    private sealed class MemoryStore : IAttachmentStore
    {
        private readonly Dictionary<string, byte[]> files = new();
        public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            var key = Guid.NewGuid().ToString("N") + extension;
            files[key] = copy.ToArray();
            return key;
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(files[key]));
        public Task DeleteAsync(string key, CancellationToken ct = default) { files.Remove(key); return Task.CompletedTask; }
    }

    private static MimeMessage Email(string from, string subject = "Card blocked", string body = "My card ending 1234 is blocked.", string? id = null,
        string? inReplyTo = null, Action<MimeMessage>? also = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Ravi Kumar", from));
        m.To.Add(new MailboxAddress("RMA Support", "support@rma.test"));
        m.Subject = subject;
        m.MessageId = id ?? MimeKit.Utils.MimeUtils.GenerateMessageId("sender.test");
        if (inReplyTo != null) m.InReplyTo = inReplyTo;
        m.Body = new TextPart("plain") { Text = body };
        also?.Invoke(m);
        return m;
    }

    private async Task<InboundEmail> Receive(MimeMessage m)
    {
        mail.Inbox.Add(m);
        await Processor().RunCycleAsync(default);
        db.ChangeTracker.Clear();
        return await db.InboundEmails.SingleAsync(e => e.InternetMessageId == EmailParsing.NormalizeMessageId(m.MessageId));
    }

    // ───────────── tests ─────────────

    /// <summary>Stores an email without deciding what it is (as a read does), so a test can put it in an older state.</summary>
    private async Task<InboundEmail> Stored(MimeMessage m)
    {
        mail.Inbox.Add(m);
        await Processor().SyncMailboxAsync(mailbox.Id, default);
        db.ChangeTracker.Clear();
        return await db.InboundEmails.SingleAsync(e => e.InternetMessageId == EmailParsing.NormalizeMessageId(m.MessageId));
    }

    [Fact]
    public async Task An_email_from_anyone_opens_one_ticket_waiting_to_be_assigned_and_never_a_case()
    {
        var email = await Receive(Email("someone@gmail.com", "Refund not received", "I was charged twice."));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(ticket.Id, email.TicketId);
        Assert.Equal(("T-00001", TicketStatuses.New, TicketSources.Email), (ticket.TicketNumber, ticket.Status, ticket.Source));
        Assert.Equal(("Ravi Kumar", "someone@gmail.com", "Refund not received"), (ticket.RequesterName, ticket.RequesterEmail, ticket.Subject));
        Assert.Equal("I was charged twice.", ticket.Description);
        Assert.Equal(SenderVerification.NotVerified, ticket.SenderVerification);
        Assert.Equal("Enquiry", ticket.CaseType);   // the mailbox's suggestion
        Assert.Null(email.OrganisationId);
        Assert.Equal(EmailAuthenticity.NotChecked, email.Authenticity);   // an unknown domain is not checked for a signature
        Assert.Empty(db.Cases);
        Assert.Empty(mail.Sent);   // an unverified sender is never mailed
    }

    [Fact]
    public async Task An_email_from_a_verified_domain_opens_a_verified_ticket_not_a_case_and_the_sender_is_told_its_number()
    {
        var email = await Receive(Email("ravi@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(SenderVerification.Verified, ticket.SenderVerification);
        Assert.Contains("State Bank of India", ticket.SenderNote);
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        Assert.Empty(db.Cases);
        var ack = Assert.Single(mail.Sent);
        Assert.Equal("Re: Card blocked [Ticket T-00001]", ack.Subject);
        Assert.Equal("ravi@sbi.co.in", ack.To.Mailboxes.Single().Address);
        Assert.Equal(email.InternetMessageId, ack.InReplyTo);
        Assert.Equal(AcknowledgementStatuses.Sent, email.AckStatus);
    }

    [Fact]
    public async Task The_same_email_delivered_twice_is_stored_once_and_opens_one_ticket()
    {
        var m = Email("ravi@sbi.co.in");
        mail.Inbox.Add(m);
        mail.Inbox.Add(Email("ravi@sbi.co.in", id: m.MessageId));   // a second delivery of the same message

        await Processor().RunCycleAsync(default);

        Assert.Equal(1, await db.InboundEmails.CountAsync());
        Assert.Equal(1, await db.Tickets.CountAsync());
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task A_reply_on_a_waiting_tickets_thread_or_quoting_its_number_is_added_to_that_ticket_not_opened_as_another()
    {
        var first = await Receive(Email("ravi@sbi.co.in"));
        var ackId = mail.Sent.Single().MessageId;

        var reply = await Receive(Email("ravi@sbi.co.in", "Re: Card blocked [Ticket T-00001]", "Any update?", inReplyTo: ackId));
        var quoted = await Receive(Email("ravi@sbi.co.in", "Again about [Ticket T-00001]", "Still waiting."));

        Assert.Equal(1, await db.Tickets.CountAsync());
        Assert.All(new[] { reply, quoted }, e => Assert.Equal((EmailStatuses.LinkedToTicket, first.TicketId), (e.Status, e.TicketId)));
        Assert.Single(mail.Sent);   // replies are not acknowledged again
    }

    [Fact]
    public async Task Once_a_ticket_became_a_case_a_reply_goes_to_that_case_not_to_a_new_ticket()
    {
        var first = await Receive(Email("ravi@sbi.co.in"));
        var ackId = mail.Sent.Single().MessageId;
        // What assigning does (TicketService): the ticket is converted and its emails point at the case.
        var c = new Case { CaseNumber = "C-00042", Title = "Card blocked", CaseType = "Complaint", Severity = "High", SourceChannel = "Email", CreatedAt = DateTime.UtcNow };
        db.Cases.Add(c);
        var ticket = await db.Tickets.SingleAsync();
        ticket.Status = TicketStatuses.Converted;
        ticket.CaseId = c.Id;
        (await db.InboundEmails.SingleAsync(e => e.Id == first.Id)).CaseId = c.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reply = await Receive(Email("colleague@sbi.co.in", "Re: Card blocked [Ticket T-00001]", "Here is more detail.", inReplyTo: ackId));
        var quoted = await Receive(Email("ravi@sbi.co.in", "About [Ticket T-00001]", "One more thing."));

        Assert.All(new[] { reply, quoted }, e => Assert.Equal((EmailStatuses.LinkedToCase, (Guid?)c.Id), (e.Status, e.CaseId)));
        Assert.Equal(1, await db.Tickets.CountAsync());
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.ActionType == "EMAIL_REPLY_RECEIVED" && e.Message.Contains("Here is more detail")));
    }

    [Fact]
    public async Task A_subdomain_is_verified_only_when_its_domain_accepts_subdomains()
    {
        await Receive(Email("it@it.sbi.co.in", "First"));
        db.EmailVerifiedDomains.Single(x => x.Id == sbi.Id).IncludeSubdomains = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Receive(Email("it@it.sbi.co.in", "Second"));

        var tickets = await db.Tickets.OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal(new[] { SenderVerification.NotVerified, SenderVerification.Verified }, tickets.Select(t => t.SenderVerification));
    }

    [Fact]
    public async Task An_older_not_verified_email_becomes_one_ticket_on_check_again_however_often_it_is_pressed()
    {
        var email = await Stored(Email("ops@hdfc.example"));
        var tracked = await db.InboundEmails.SingleAsync();
        tracked.Status = EmailStatuses.NotVerified;   // how emails were left before every email became a ticket
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Processor().ProcessAsync(email.Id, manual: true, default);
        await Processor().ProcessAsync(email.Id, manual: true, default);

        Assert.Equal(EmailStatuses.TicketCreated, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await db.Tickets.CountAsync());
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task Auto_replies_bounces_and_our_own_mail_never_open_tickets_and_are_never_answered()
    {
        var outOfOffice = await Receive(Email("ravi@sbi.co.in", "Out of office", also: m => m.Headers.Add(HeaderId.AutoSubmitted, "auto-replied")));
        var bounce = await Receive(Email("mailer-daemon@sbi.co.in", "Undeliverable"));
        var ours = await Receive(Email("support@rma.test", "Re: Card blocked [Ticket T-00009]"));

        Assert.All(new[] { outOfOffice, bounce, ours }, e => Assert.Equal(EmailStatuses.Ignored, e.Status));
        Assert.Empty(db.Tickets);
        Assert.Empty(db.Cases);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task An_acknowledgement_that_cannot_be_sent_keeps_the_ticket_and_is_retried_later_with_the_ticket_number()
    {
        mail.SendFails = true;

        var email = await Receive(Email("ravi@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        Assert.Equal(AcknowledgementStatuses.Failed, email.AckStatus);
        Assert.NotNull(email.AckNextAttemptAt);

        mail.SendFails = false;
        var tracked = await db.InboundEmails.SingleAsync();
        tracked.AckNextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        await Processor().RetryAcknowledgementsAsync(default);

        Assert.Equal(AcknowledgementStatuses.Sent, (await db.InboundEmails.AsNoTracking().SingleAsync()).AckStatus);
        Assert.Contains("[Ticket T-00001]", Assert.Single(mail.Sent).Subject);
    }

    [Fact]
    public async Task The_emails_files_are_kept_on_its_ticket_for_the_case_to_check_when_assigned()
    {
        var m = Email("ravi@sbi.co.in");
        var body = new BodyBuilder { TextBody = "See attached." };
        body.Attachments.Add("statement.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
        body.Attachments.Add("tool.exe", "MZ"u8.ToArray(), new ContentType("application", "octet-stream"));
        m.Body = body.ToMessageBody();

        var email = await Receive(m);

        Assert.Equal(2, email.AttachmentCount);
        var files = await db.TicketAttachments.Select(a => a.FileName).OrderBy(n => n).ToListAsync();
        Assert.Equal(new[] { "statement.pdf", "tool.exe" }, files);
        Assert.Empty(uploads);   // nothing reaches a case until a person assigns the ticket
    }

    [Fact]
    public async Task One_sender_is_not_sent_more_acknowledgements_than_the_hourly_limit()
    {
        for (var i = 0; i < 12; i++) mail.Inbox.Add(Email("ravi@sbi.co.in", $"Issue {i}"));

        await Processor().RunCycleAsync(default);

        Assert.Equal(12, await db.Tickets.CountAsync());
        Assert.Empty(db.Cases);
        Assert.Equal(10, mail.Sent.Count);
    }

    [Fact]
    public void A_mailbox_password_is_stored_encrypted_and_reads_back()
    {
        var stored = secrets.Protect("s3cret-app-password");

        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain("s3cret", stored);
        Assert.Equal("s3cret-app-password", secrets.Unprotect(stored));
    }

    [Theory]
    [InlineData("Re: Card blocked [Ticket C-01248]", "C-01248")]
    [InlineData("Fwd: [S-00044] statement", "S-00044")]
    [InlineData("C-01248 without brackets", null)]
    public void A_ticket_number_is_read_from_a_subject_only_in_the_form_we_write_it(string subject, string? expected) =>
        Assert.Equal(expected, EmailParsing.CaseNumberInSubject(subject));

    [Theory]
    [InlineData("@SBI.co.in", "sbi.co.in")]
    [InlineData("www.sbi.co.in", "sbi.co.in")]
    [InlineData("https://sbi.co.in", "")]
    [InlineData("localhost", "")]
    public void A_domain_is_stored_in_one_form_and_nonsense_is_refused(string input, string expected) =>
        Assert.Equal(expected, EmailParsing.NormalizeDomain(input));

    // ───────────── verification policies, sender identity and authenticity ─────────────

    [Fact]
    public async Task Under_specific_senders_a_listed_sender_is_verified_and_the_ticket_shows_the_configured_name()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"), ("mukul@sbi.co.in", "Mukul"));

        var email = await Receive(Email("tushar@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        Assert.Equal(SenderVerification.Verified, email.Verification);
        Assert.Equal("Tushar", email.SenderAlias);
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(("Tushar", SenderVerification.Verified), (ticket.RequesterName, ticket.SenderVerification));   // the alias, not the From line's "Ravi Kumar"
    }

    [Fact]
    public async Task Under_specific_senders_an_unlisted_address_opens_a_not_listed_ticket_and_its_organisation_is_still_known()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"));

        var email = await Receive(Email("unknown@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        Assert.Equal(SenderVerification.NotListed, email.Verification);
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(SenderVerification.NotListed, ticket.SenderVerification);
        Assert.Contains("not on State Bank of India's list", ticket.SenderNote);
        Assert.Empty(mail.Sent);
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task Under_entire_domain_any_sender_is_verified_and_named_by_the_email_itself()
    {
        var email = await Receive(Email("anyone@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        Assert.Null(email.SenderAlias);
        Assert.Equal("Ravi Kumar", (await db.Tickets.SingleAsync()).RequesterName);
    }

    [Fact]
    public async Task A_forged_email_opens_a_not_verified_ticket_even_from_a_listed_sender_and_is_never_answered()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"));
        authResult = new(EmailAuthenticity.Fail, "dkim=none; dmarc=fail p=reject");

        var email = await Receive(Email("tushar@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        Assert.Equal(SenderVerification.NotVerified, email.Verification);
        Assert.Equal(EmailAuthenticity.Fail, email.Authenticity);
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(SenderVerification.NotVerified, ticket.SenderVerification);
        Assert.Contains("may be forged", ticket.SenderNote);
        Assert.Empty(mail.Sent);
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task A_forged_reply_is_never_added_to_the_ticket_or_case_whose_thread_it_claims_to_answer()
    {
        var first = await Receive(Email("ravi@sbi.co.in", id: "first@sbi.co.in"));
        authResult = new(EmailAuthenticity.Fail, "dkim=fail d=sbi.co.in; dmarc=fail p=reject");

        var forged = await Receive(Email("ravi@sbi.co.in", "Re: Card blocked [Ticket T-00001]", id: "forged@evil.test", inReplyTo: "first@sbi.co.in"));

        Assert.Equal(EmailStatuses.TicketCreated, forged.Status);   // its own ticket, marked not verified
        Assert.NotEqual(first.TicketId, forged.TicketId);
        Assert.Null(forged.CaseId);
        Assert.Equal(SenderVerification.NotVerified, (await db.Tickets.SingleAsync(t => t.Id == forged.TicketId)).SenderVerification);
    }

    [Fact]
    public async Task A_domain_that_requires_a_signature_marks_unsigned_mail_not_verified_and_retries_when_the_signature_cannot_be_checked()
    {
        db.EmailVerifiedDomains.Single(x => x.Id == sbi.Id).RequireAuthentication = true;
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var unsigned = await Receive(Email("ravi@sbi.co.in"));
        authResult = new(EmailAuthenticity.Unknown, "dkim=temperror d=sbi.co.in; dmarc=temperror");
        var unchecked_ = await Receive(Email("ravi@sbi.co.in", subject: "Second"));
        authResult = new(EmailAuthenticity.Pass, "dkim=pass d=sbi.co.in; dmarc=pass");
        var signed = await Receive(Email("ravi@sbi.co.in", subject: "Third"));

        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.NotVerified), (unsigned.Status, unsigned.Verification));
        Assert.Contains("requires a valid signature", unsigned.VerificationNote);
        Assert.Equal(EmailStatuses.Failed, unchecked_.Status);
        Assert.NotNull(unchecked_.NextAttemptAt);        // tried again later, not refused
        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.Verified), (signed.Status, signed.Verification));
        Assert.Empty(db.Cases);
    }
}
