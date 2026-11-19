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
    private readonly RecordingNotificationSink sink = new();
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
            VerificationPolicy = VerificationPolicies.SpecificSenders,
        };
        db.EmailVerifiedDomains.Add(sbi);
        // Ravi is SBI's registered sender: only registered senders are verified (a domain alone verifies nobody).
        db.EmailVerifiedSenders.Add(new EmailVerifiedSender { VerifiedDomainId = sbi.Id, Address = "ravi@sbi.co.in", DisplayName = "Ravi Kumar", CreatedAt = DateTime.UtcNow });
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
            NullLogger<EmailIntakeProcessor>.Instance, notifications: sink);
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
    public async Task An_email_from_a_registered_sender_opens_one_ticket_waiting_to_be_assigned_and_never_a_case()
    {
        var email = await Receive(Email("ravi@sbi.co.in", "Refund not received", "I was charged twice."));

        Assert.Equal(EmailStatuses.TicketCreated, email.Status);
        var ticket = await db.Tickets.SingleAsync();
        Assert.Equal(ticket.Id, email.TicketId);
        Assert.Equal(("T-00001", TicketStatuses.New, TicketSources.Email), (ticket.TicketNumber, ticket.Status, ticket.Source));
        Assert.Equal(("Ravi Kumar", "ravi@sbi.co.in", "Refund not received"), (ticket.RequesterName, ticket.RequesterEmail, ticket.Subject));
        Assert.Equal("I was charged twice.", ticket.Description);
        Assert.Null(ticket.CaseType);   // a mailbox no longer suggests case details: they are chosen when the case is made
        Assert.Equal(SenderMatchKinds.RegisteredSender, email.MatchKind);
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task An_email_from_an_unknown_sender_waits_for_review_opens_no_ticket_and_is_never_answered()
    {
        var email = await Receive(Email("someone@gmail.com", "Refund not received", "I was charged twice."));

        Assert.Equal(EmailStatuses.PendingReview, email.Status);
        Assert.Equal((SenderMatchKinds.Unknown, SenderVerification.NotVerified), (email.MatchKind, email.Verification));
        Assert.Contains("No registered organisation uses gmail.com", email.MatchReason);
        Assert.NotNull(email.ReviewStartedAt);
        Assert.Null(email.OrganisationId);
        Assert.Equal(EmailAuthenticity.NotChecked, email.Authenticity);   // an unknown domain is not checked for a signature
        Assert.Equal(AcknowledgementStatuses.Skipped, email.AckStatus);
        Assert.Empty(db.Tickets);
        Assert.Empty(mail.Sent);   // an unverified sender is never mailed
        // gmail.com can never belong to an organisation: the reviewer may approve this email, but registering is not offered.
        var detail = await Admin().GetAsync(email.Id, default);
        Assert.True(detail.CanReview);
        Assert.True(detail.FromPublicProvider);
        Assert.False(detail.CanRegisterSender);
    }

    [Fact]
    public async Task A_look_alike_of_a_registered_domain_waits_for_review_with_the_resemblance_explained_and_the_organisation_named()
    {
        var email = await Receive(Email("ravi@sbі.co.in"));   // a Cyrillic "і" in place of the Latin "i"

        Assert.Equal((EmailStatuses.PendingReview, SenderMatchKinds.SimilarDomain), (email.Status, email.MatchKind));
        Assert.Contains("look-alike characters", email.MatchReason);
        Assert.Contains("Cyrillic", email.MatchReason);
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        Assert.NotEqual(SenderVerification.Verified, email.Verification);
        Assert.Empty(db.Tickets);
    }

    [Fact]
    public async Task A_waiting_email_is_not_worked_again_by_the_background_cycle_but_check_again_releases_it_once_the_sender_is_registered()
    {
        var email = await Receive(Email("colleague@sbi.co.in"));
        await Processor().RunCycleAsync(default);
        Assert.Equal(EmailStatuses.PendingReview, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);

        db.EmailVerifiedSenders.Add(new EmailVerifiedSender { VerifiedDomainId = sbi.Id, Address = "colleague@sbi.co.in", DisplayName = "Colleague", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        await Processor().ProcessAsync(email.Id, manual: true, default);

        var after = await db.InboundEmails.AsNoTracking().SingleAsync();
        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.Verified), (after.Status, after.Verification));
        Assert.Single(db.Tickets);
    }

    [Fact]
    public async Task An_email_ticket_takes_no_case_details_from_its_mailbox_and_waits_under_the_no_priority_time()
    {
        // A mailbox only says which teams work its emails; a value left on it from before is not copied onto the ticket.
        var rule = db.PrioritySlaRules.Single(r => r.Priority == "High");
        rule.TicketAssignValue = 15;
        rule.TicketAssignMinutes = 15;
        db.EmailMailboxes.Single(m => m.Id == mailbox.Id).DefaultPriority = "High";
        db.SaveChanges();
        db.ChangeTracker.Clear();

        await Receive(Email("ravi@sbi.co.in", "Refund not received", "I was charged twice."));

        var ticket = await db.Tickets.SingleAsync();
        Assert.Null(ticket.Priority);
        Assert.NotEqual(15, ticket.AssignTargetMinutes);
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
        db.EmailVerifiedSenders.Add(new EmailVerifiedSender { VerifiedDomainId = sbi.Id, Address = "it@it.sbi.co.in", DisplayName = "SBI IT", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var first = await Receive(Email("it@it.sbi.co.in", "First"));
        db.EmailVerifiedDomains.Single(x => x.Id == sbi.Id).IncludeSubdomains = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var second = await Receive(Email("it@it.sbi.co.in", "Second"));

        Assert.Equal((EmailStatuses.PendingReview, SenderMatchKinds.SimilarDomain), (first.Status, first.MatchKind));
        Assert.Contains("registered without its sub-domains", first.MatchReason);
        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.Verified), (second.Status, second.Verification));
        Assert.Single(db.Tickets);
    }

    [Fact]
    public async Task An_older_not_verified_email_checked_again_waits_for_review_however_often_it_is_pressed()
    {
        var email = await Stored(Email("ops@hdfc.example"));
        var tracked = await db.InboundEmails.SingleAsync();
        tracked.Status = EmailStatuses.NotVerified;   // how emails were left before every email became a ticket
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Processor().ProcessAsync(email.Id, manual: true, default);
        await Processor().ProcessAsync(email.Id, manual: true, default);

        var after = await db.InboundEmails.AsNoTracking().SingleAsync();
        Assert.Equal((EmailStatuses.PendingReview, SenderMatchKinds.Unknown), (after.Status, after.MatchKind));
        Assert.Empty(db.Tickets);
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
    public async Task An_unregistered_address_at_a_registered_domain_waits_for_review_filed_under_its_organisation()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"));

        var email = await Receive(Email("unknown@sbi.co.in"));

        Assert.Equal(EmailStatuses.PendingReview, email.Status);
        Assert.Equal((SenderMatchKinds.KnownDomain, SenderVerification.NotListed), (email.MatchKind, email.Verification));
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        Assert.Contains("not one of its registered senders", email.MatchReason);
        Assert.Empty(db.Tickets);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task A_domain_still_marked_entire_domain_verifies_nobody_but_its_registered_senders()
    {
        db.EmailVerifiedDomains.Single(x => x.Id == sbi.Id).VerificationPolicy = VerificationPolicies.EntireDomain;
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var email = await Receive(Email("anyone@sbi.co.in"));

        Assert.Equal((EmailStatuses.PendingReview, SenderMatchKinds.KnownDomain), (email.Status, email.MatchKind));
        Assert.Empty(db.Tickets);
    }

    [Fact]
    public async Task A_forged_email_is_held_as_suspicious_even_from_a_listed_sender_opens_no_ticket_and_is_never_answered()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"));
        authResult = new(EmailAuthenticity.Fail, "dkim=none; dmarc=fail p=reject");

        var email = await Receive(Email("tushar@sbi.co.in"));

        Assert.Equal(EmailStatuses.Suspicious, email.Status);
        Assert.Equal(SenderVerification.NotVerified, email.Verification);
        Assert.Equal(EmailAuthenticity.Fail, email.Authenticity);
        Assert.Contains("may be forged", email.VerificationNote);
        Assert.Empty(db.Tickets);
        Assert.Empty(mail.Sent);

        await Processor().RunCycleAsync(default);   // stays held: never worked again automatically
        Assert.Equal(EmailStatuses.Suspicious, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_forged_reply_is_never_added_to_the_ticket_or_case_whose_thread_it_claims_to_answer()
    {
        var first = await Receive(Email("ravi@sbi.co.in", id: "first@sbi.co.in"));
        authResult = new(EmailAuthenticity.Fail, "dkim=fail d=sbi.co.in; dmarc=fail p=reject");

        var forged = await Receive(Email("ravi@sbi.co.in", "Re: Card blocked [Ticket T-00001]", id: "forged@evil.test", inReplyTo: "first@sbi.co.in"));

        Assert.Equal(EmailStatuses.Suspicious, forged.Status);   // held, never added to the thread
        Assert.Null(forged.TicketId);
        Assert.Null(forged.CaseId);
        Assert.Equal(first.TicketId, (await db.Tickets.SingleAsync()).Id);
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

        Assert.Equal((EmailStatuses.PendingReview, SenderVerification.NotVerified), (unsigned.Status, unsigned.Verification));
        Assert.Contains("requires a valid signature", unsigned.VerificationNote);
        Assert.Equal(EmailStatuses.Failed, unchecked_.Status);
        Assert.NotNull(unchecked_.NextAttemptAt);        // tried again later, not refused
        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.Verified), (signed.Status, signed.Verification));
        Assert.Empty(db.Cases);
    }

    // ───────────── sender review ─────────────

    private EmailIntakeAdminService Admin() => new(db, secrets, mail, store);
    private readonly Guid reviewer = Guid.NewGuid();

    private async Task<InboundEmail> Reviewed(InboundEmail email, ReviewEmailDto dto, bool mayRegister = true)
    {
        dto.Version = (await db.InboundEmails.AsNoTracking().SingleAsync(e => e.Id == email.Id)).Version;
        var detail = await Admin().ReviewAsync(email.Id, dto, reviewer, mayRegister, default);
        if (detail.ReviewDecision == ReviewDecisions.Approved) await Processor().ProcessAsync(email.Id, manual: true, default);
        db.ChangeTracker.Clear();
        return await db.InboundEmails.AsNoTracking().SingleAsync(e => e.Id == email.Id);
    }

    [Fact]
    public async Task Approving_without_registering_releases_only_that_email_and_the_senders_next_email_waits_again()
    {
        var first = await Receive(Email("colleague@sbi.co.in", "First"));

        var approved = await Reviewed(first, new ReviewEmailDto { Decision = "approve" });
        var next = await Receive(Email("colleague@sbi.co.in", "Second"));

        Assert.Equal((EmailStatuses.TicketCreated, ReviewDecisions.Approved), (approved.Status, approved.ReviewDecision));
        Assert.Equal(reviewer, approved.ReviewedById);
        Assert.Contains("Approved by a reviewer", (await db.Tickets.SingleAsync()).SenderNote);
        Assert.Equal(AcknowledgementStatuses.Sent, approved.AckStatus);   // an approved email is acknowledged
        Assert.Equal(EmailStatuses.PendingReview, next.Status);
        Assert.False(await db.EmailVerifiedSenders.AnyAsync(x => x.Address == "colleague@sbi.co.in"));
    }

    [Fact]
    public async Task Registering_the_sender_needs_its_own_confirmation_and_permission_and_changes_nothing_without_them()
    {
        var email = await Receive(Email("colleague@sbi.co.in"));
        var register = new ReviewRegisterSenderDto { DisplayName = "Colleague", OrganisationName = "State Bank of India" };

        var unconfirmed = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Reviewed(email, new ReviewEmailDto { Decision = "approve", Register = register }));
        await Assert.ThrowsAsync<EmailReviewForbiddenException>(() =>
            Reviewed(email, new ReviewEmailDto { Decision = "approve", Register = register, ConfirmRegistration = true }, mayRegister: false));

        Assert.Contains(unconfirmed.Errors, e => e.Field == "confirmRegistration");
        db.ChangeTracker.Clear();
        Assert.Equal(EmailStatuses.PendingReview, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);
        Assert.False(await db.EmailVerifiedSenders.AnyAsync(x => x.Address == "colleague@sbi.co.in"));
    }

    [Fact]
    public async Task Approving_and_registering_verifies_the_sender_from_then_on_but_releases_no_other_waiting_email()
    {
        var older = await Receive(Email("colleague@sbi.co.in", "Older"));
        var email = await Receive(Email("colleague@sbi.co.in", "This one"));

        var approved = await Reviewed(email, new ReviewEmailDto
        {
            Decision = "approve", ConfirmRegistration = true,
            Register = new ReviewRegisterSenderDto { DisplayName = "Colleague", OrganisationName = "State Bank of India" },
        });
        var later = await Receive(Email("colleague@sbi.co.in", "Later"));

        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.Verified), (approved.Status, approved.Verification));
        Assert.Equal(sbi.Id, (await db.EmailVerifiedSenders.SingleAsync(x => x.Address == "colleague@sbi.co.in")).VerifiedDomainId);
        Assert.Equal(EmailStatuses.PendingReview, (await db.InboundEmails.AsNoTracking().SingleAsync(e => e.Id == older.Id)).Status);
        Assert.Equal((EmailStatuses.TicketCreated, SenderVerification.Verified), (later.Status, later.Verification));
        var audit = await db.CaseEvents.Where(e => e.Module == "EmailReview" || e.EntityName == "EmailVerifiedSender").Select(e => e.ActionType).ToListAsync();
        Assert.Contains("EMAIL_APPROVED", audit);
        Assert.Contains("SENDER_REGISTERED", audit);
    }

    [Fact]
    public async Task Rejecting_or_marking_suspicious_needs_a_reason_and_the_email_never_becomes_a_ticket()
    {
        var spam = await Receive(Email("promo@offers.example", "Win now"));
        var phish = await Receive(Email("security@sbi-alerts.example", "Verify your account"));

        await Assert.ThrowsAsync<FieldValidationException>(() => Reviewed(spam, new ReviewEmailDto { Decision = "reject" }));
        var rejected = await Reviewed(spam, new ReviewEmailDto { Decision = "reject", Note = "Marketing mail" });
        var suspicious = await Reviewed(phish, new ReviewEmailDto { Decision = "suspicious", Note = "Asks for account details" });
        await Processor().ProcessAsync(spam.Id, manual: true, default);
        await Processor().ProcessAsync(phish.Id, manual: true, default);

        Assert.Equal(EmailStatuses.Rejected, (await db.InboundEmails.AsNoTracking().SingleAsync(e => e.Id == spam.Id)).Status);
        Assert.Equal(EmailStatuses.Suspicious, (await db.InboundEmails.AsNoTracking().SingleAsync(e => e.Id == phish.Id)).Status);
        Assert.Equal("Marketing mail", rejected.ReviewNote);
        Assert.Equal(ReviewDecisions.Suspicious, suspicious.ReviewDecision);
        Assert.Empty(db.Tickets);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Two_reviewers_can_never_both_decide_on_one_email()
    {
        var email = await Receive(Email("colleague@sbi.co.in"));
        var openedAt = email.Version;

        await Reviewed(email, new ReviewEmailDto { Decision = "reject", Note = "Not ours" });
        var late = await Assert.ThrowsAsync<EmailReviewConflictException>(() =>
            Admin().ReviewAsync(email.Id, new ReviewEmailDto { Decision = "approve", Version = openedAt }, Guid.NewGuid(), true, default));

        Assert.Contains("already decided", late.Message);
        Assert.Empty(db.Tickets);
    }

    // ───────────── sender review deadline ─────────────

    private (Guid Lead, Guid Member) ShareMailboxWithATeam(int sla = 30, int reminder = 10, int escalation = 45, string escalateTo = ReviewEscalationTargets.TeamLeads)
    {
        var lead = new User { Name = "Lena Lead", Email = "lena@rma.test", CreatedAt = DateTime.UtcNow };
        var member = new User { Name = "Mo Member", Email = "mo@rma.test", CreatedAt = DateTime.UtcNow };
        var team = new Team { Name = "Intake", Code = "INTAKE", OwnerId = lead.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(lead, member);
        db.Teams.Add(team);
        db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = member.Id, IsActive = true, CreatedAt = DateTime.UtcNow });
        db.EmailMailboxTeams.Add(new EmailMailboxTeam { MailboxId = mailbox.Id, TeamId = team.Id });
        var m = db.EmailMailboxes.Single(x => x.Id == mailbox.Id);
        (m.ReviewSlaMinutes, m.ReviewReminderMinutes, m.ReviewEscalationMinutes, m.ReviewEscalateTo) = (sla, reminder, escalation, escalateTo);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return (lead.Id, member.Id);
    }

    private ReviewSlaMonitor Monitor() => new(db, sink, NullLogger<ReviewSlaMonitor>.Instance);

    private Task<(int Reminders, int Escalations)> MonitorAt(DateTime at) => Monitor().RunAsync(Api.Services.Tickets.TicketSlaClock.RoundTheClock, at, default);

    [Fact]
    public async Task A_waiting_email_takes_its_mailboxs_review_deadline_and_tells_the_mailboxs_teams_without_naming_the_sender()
    {
        var (lead, member) = ShareMailboxWithATeam(sla: 30, reminder: 10, escalation: 45);

        var email = await Receive(Email("someone@gmail.com", "Please call me"));

        var start = email.ReviewStartedAt!.Value;
        Assert.Equal((30, start.AddMinutes(30), start.AddMinutes(10), start.AddMinutes(45)),
            (email.ReviewSlaMinutes, email.ReviewDueAt, email.ReviewReminderAt, email.ReviewEscalateAt));
        var needed = sink.Requests.Single(r => r.Kind == Api.Services.Notifications.CaseNotificationKinds.EmailReviewNeeded);
        Assert.Equal(new[] { lead, member }.Order(), needed.RecipientLocalIds.Order());
        Assert.Equal($"inbox/{email.Id}", needed.Link);
        Assert.DoesNotContain(needed.Values!.Values, v => v != null && (v.Contains("gmail.com") || v.Contains("Please call me")));
    }

    [Fact]
    public async Task The_reminder_and_the_escalation_are_each_sent_once_and_the_escalation_goes_to_the_leads_and_the_audit_trail()
    {
        var (lead, member) = ShareMailboxWithATeam(sla: 30, reminder: 10, escalation: 45);
        var email = await Receive(Email("someone@gmail.com"));
        var start = email.ReviewStartedAt!.Value;
        sink.Requests.Clear();

        var early = await MonitorAt(start.AddMinutes(5));
        var reminded = await MonitorAt(start.AddMinutes(11));
        var again = await MonitorAt(start.AddMinutes(12));
        var escalated = await MonitorAt(start.AddMinutes(46));
        var afterwards = await MonitorAt(start.AddMinutes(90));

        Assert.Equal(((0, 0), (1, 0), (0, 0), (0, 1), (0, 0)), (early, reminded, again, escalated, afterwards));
        Assert.Equal(new[] { lead, member }.Order(),
            sink.Requests.Single(r => r.Kind == Api.Services.Notifications.CaseNotificationKinds.EmailReviewReminder).RecipientLocalIds.Order());
        Assert.Equal(new[] { lead }, sink.Requests.Single(r => r.Kind == Api.Services.Notifications.CaseNotificationKinds.EmailReviewEscalated).RecipientLocalIds);
        Assert.Single(db.CaseEvents.Where(e => e.ActionType == "REVIEW_SLA_ESCALATED"));
        Assert.Equal(EmailStatuses.PendingReview, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);   // escalation never decides for anyone
    }

    [Fact]
    public async Task Escalating_to_the_administrators_names_nobody_so_the_notification_goes_to_its_administrators()
    {
        ShareMailboxWithATeam(escalateTo: ReviewEscalationTargets.Administrators);
        var email = await Receive(Email("someone@gmail.com"));

        await MonitorAt(email.ReviewStartedAt!.Value.AddHours(2));

        Assert.Empty(sink.Requests.Single(r => r.Kind == Api.Services.Notifications.CaseNotificationKinds.EmailReviewEscalated).RecipientLocalIds);
    }

    [Fact]
    public async Task Changing_the_mailboxs_deadline_never_rewrites_a_waiting_email_and_a_late_decision_is_recorded_as_breached()
    {
        ShareMailboxWithATeam(sla: 30, reminder: 10, escalation: 45);
        var email = await Receive(Email("someone@gmail.com"));
        var m = db.EmailMailboxes.Single(x => x.Id == mailbox.Id);
        m.ReviewSlaMinutes = 600;
        var tracked = db.InboundEmails.Single();
        tracked.ReviewDueAt = DateTime.UtcNow.AddMinutes(-1);   // as if 30 minutes had passed
        db.SaveChanges();
        db.ChangeTracker.Clear();

        await Processor().ProcessAsync(email.Id, manual: true, default);   // "Check again": still waiting
        var stillWaiting = await db.InboundEmails.AsNoTracking().SingleAsync();
        await Reviewed(email, new ReviewEmailDto { Decision = "reject", Note = "Not for us" });

        Assert.Equal(30, stillWaiting.ReviewSlaMinutes);
        Assert.Equal(ReviewSla.Breached, (await db.InboundEmails.AsNoTracking().SingleAsync()).ReviewSlaOutcome);
    }

    [Fact]
    public async Task An_email_decided_in_time_is_met_and_is_never_reminded_or_escalated_afterwards()
    {
        ShareMailboxWithATeam(sla: 30, reminder: 10, escalation: 45);
        var email = await Receive(Email("someone@gmail.com"));

        await Reviewed(email, new ReviewEmailDto { Decision = "reject", Note = "Spam" });
        var later = await MonitorAt(email.ReviewStartedAt!.Value.AddHours(2));

        Assert.Equal(ReviewSla.Met, (await db.InboundEmails.AsNoTracking().SingleAsync()).ReviewSlaOutcome);
        Assert.Equal((0, 0), later);
    }

    [Fact]
    public async Task Emails_already_waiting_before_deadlines_existed_get_their_times_but_no_alerts()
    {
        ShareMailboxWithATeam(sla: 30, reminder: 10, escalation: 45);
        db.InboundEmails.Add(new InboundEmail
        {
            MailboxId = mailbox.Id, MessageKey = "old", FromAddress = "someone@gmail.com", Status = EmailStatuses.PendingReview,
            ReviewStartedAt = DateTime.UtcNow.AddHours(-3), ReceivedAt = DateTime.UtcNow.AddHours(-3), CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var raised = await MonitorAt(DateTime.UtcNow);

        Assert.Equal((0, 0), raised);
        Assert.Empty(sink.Requests);
        var old = await db.InboundEmails.AsNoTracking().SingleAsync();
        Assert.NotNull(old.ReviewDueAt);
        Assert.NotNull(old.ReviewEscalatedAt);
    }

    [Fact]
    public async Task A_mailbox_with_no_deadline_never_reminds_or_escalates()
    {
        ShareMailboxWithATeam(sla: 0, reminder: 0, escalation: 0);
        var email = await Receive(Email("someone@gmail.com"));

        var raised = await MonitorAt(DateTime.UtcNow.AddDays(3));

        Assert.Equal((0, null), (email.ReviewSlaMinutes, email.ReviewDueAt));
        Assert.Equal((0, 0), raised);
        Assert.Single(sink.Requests);   // only "waiting for review"
    }

    [Theory]
    [InlineData(30, 30, 45, "reviewReminderMinutes")]   // the reminder must come before the deadline
    [InlineData(30, 10, 20, "reviewEscalationMinutes")] // escalation at or after it
    [InlineData(-5, 0, 0, "reviewSlaMinutes")]
    public async Task A_review_deadline_that_makes_no_sense_is_refused(int sla, int reminder, int escalation, string field)
    {
        var dto = new SaveMailboxDto
        {
            Name = "RMA Support", Address = "support@rma.test", ImapHost = "imap.rma.test", SmtpHost = "smtp.rma.test", IsEnabled = true,
            ReviewSlaMinutes = sla, ReviewReminderMinutes = reminder, ReviewEscalationMinutes = escalation,
        };

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => Admin().UpdateMailboxAsync(mailbox.Id, dto, reviewer, default));

        Assert.Contains(ex.Errors, e => e.Field == field);
    }

    [Fact]
    public async Task A_new_ticket_from_a_registered_sender_tells_the_leads_of_the_mailboxs_teams()
    {
        var (lead, _) = ShareMailboxWithATeam();

        var email = await Receive(Email("ravi@sbi.co.in"));

        var received = sink.Requests.Single(r => r.Kind == Api.Services.Notifications.CaseNotificationKinds.TicketReceived);
        Assert.Equal(new[] { lead }, received.RecipientLocalIds);
        Assert.Equal($"tickets/{email.TicketId}", received.Link);
    }

    [Fact]
    public async Task A_ticket_from_a_registered_sender_is_for_their_organisation_and_one_from_anyone_else_is_not()
    {
        var registered = await Receive(Email("ravi@sbi.co.in", "From Ravi"));
        var approved = await Receive(Email("colleague@sbi.co.in", "From a colleague"));
        await Reviewed(approved, new ReviewEmailDto { Decision = "approve" });   // released, but the sender stays unregistered

        var tickets = await db.Tickets.AsNoTracking().OrderBy(t => t.Sequence).ToListAsync();
        var ravi = await db.EmailVerifiedSenders.SingleAsync(x => x.Address == "ravi@sbi.co.in");
        Assert.Equal((sbiOrg.Id, ravi.Id), (tickets[0].OrganisationId, tickets[0].VerifiedSenderId));
        Assert.Equal((null, null), (tickets[1].OrganisationId, tickets[1].VerifiedSenderId));
    }

    [Fact]
    public async Task The_customers_answer_to_a_reply_sent_from_a_case_goes_back_to_that_case()
    {
        var team = new Team { Name = "Cards", Code = "CDX", IsActive = true, CreatedAt = DateTime.UtcNow };
        var c = new Case { CaseNumber = "C-09001", Title = "Card blocked", TeamId = team.Id, OwnerId = Guid.NewGuid(), ContactEmail = "ravi@sbi.co.in", CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        db.Cases.Add(c);
        db.CaseEmailMessages.Add(new CaseEmailMessage { CaseId = c.Id, ToAddress = "ravi@sbi.co.in", Subject = "Re: Card blocked [Case C-09001]", MessageId = "reply-1@rma.test", Status = CaseEmailStatuses.Sent });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var answer = await Receive(Email("ravi@sbi.co.in", "Re: Card blocked [Case C-09001]", "Thanks, it works now.", inReplyTo: "reply-1@rma.test"));

        Assert.Equal((EmailStatuses.LinkedToCase, (Guid?)c.Id), (answer.Status, answer.CaseId));
        Assert.Empty(db.Tickets);
    }
}
