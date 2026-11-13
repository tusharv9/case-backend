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
/// Email intake: an email from a verified domain becomes exactly one case and its sender is told the case number; anyone else's
/// email is kept and marked "sender not verified".
/// </summary>
/// <remarks>
/// Mail is delivered more than once, servers retry, people press buttons twice and clients reply to our acknowledgement — each of
/// those, done wrong, is a duplicate case or an email loop with a customer. A case must also never exist without the email it came
/// from, and a failure after the case is saved (a file, the acknowledgement) must never undo or hide it.
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
    private Func<CreateCaseDto, Exception?> createFails = _ => null;
    private EmailAuthenticationResult authResult = new(EmailAuthenticity.None, "dkim=none; dmarc=none (no policy published)");
    private readonly EmailVerifiedDomain sbi;
    private readonly EmailOrganisation sbiOrg;
    private int casesCreated;

    public EmailIntakeTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"email-intake-{Guid.NewGuid()}").Options);
        secrets = new MailboxSecretProtector(Options.Create(new EmailIntakeOptions { SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }),
            Options.Create(new CustomerIdentifierOptions()));
        mailbox = new EmailMailbox
        {
            Name = "RMA Support", Address = "support@rma.test", PasswordEncrypted = secrets.Protect("app-password"),
            ImapHost = "imap.rma.test", SmtpHost = "smtp.rma.test", IsEnabled = true, SendAcknowledgement = true, CreatedAt = DateTime.UtcNow,
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
        var cases = new Mock<ICaseService>();
        cases.Setup(c => c.CreateCaseAsync(It.IsAny<CreateCaseDto>(), It.IsAny<Guid>(), It.IsAny<Func<Case, Task>?>()))
            .Returns(async (CreateCaseDto dto, Guid by, Func<Case, Task>? inTx) =>
            {
                if (createFails(dto) is { } ex) throw ex;
                var c = new Case { CaseNumber = $"C-{++casesCreated:D5}", Title = dto.Title, Description = dto.Description, CaseType = dto.CaseType,
                    Severity = dto.Severity, SourceChannel = dto.SourceChannel!, CustomerId = dto.CustomerId, DepartmentId = dto.DepartmentId, OwnerId = by, CreatedAt = DateTime.UtcNow };
                db.Cases.Add(c);
                await db.SaveChangesAsync();
                if (inTx != null) await inTx(c);
                return c;
            });
        var attachments = new Mock<IAttachmentService>();
        attachments.Setup(a => a.UploadAttachmentAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid caseId, IFormFile file, string? _, Guid _, CancellationToken _) =>
            {
                if (file.FileName.EndsWith(".exe")) throw new ArgumentException("This type of file is not allowed.");
                uploads.Add((caseId, file.FileName));
                return Task.FromResult(new CaseAttachmentDto { FileName = file.FileName });
            });
        return new EmailIntakeProcessor(db, cases.Object, attachments.Object, store, mail, secrets, new FakeAuthenticator(() => authResult), Options.Create(new EmailIntakeOptions()),
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

    [Fact]
    public async Task An_email_from_a_verified_domain_becomes_a_case_with_the_domains_details_and_the_sender_is_told_the_ticket_id()
    {
        var email = await Receive(Email("ravi@sbi.co.in"));

        Assert.Equal(EmailStatuses.CaseCreated, email.Status);
        Assert.Equal(SenderVerification.Verified, email.Verification);
        var created = await db.Cases.SingleAsync();
        Assert.Equal(created.Id, email.CaseId);
        Assert.Equal(("Card blocked", "Complaint", "High", "Email"), (created.Title, created.CaseType, created.Severity, created.SourceChannel));
        Assert.Equal("ravi@sbi.co.in", (await db.Customers.SingleAsync(c => c.Id == created.CustomerId)).Email);

        var ack = Assert.Single(mail.Sent);
        Assert.Equal("Re: Card blocked [Ticket C-00001]", ack.Subject);
        Assert.Equal("ravi@sbi.co.in", ack.To.Mailboxes.Single().Address);
        Assert.Equal(email.InternetMessageId, ack.InReplyTo);
        Assert.Contains("C-00001", ack.TextBody);
        Assert.Equal(AcknowledgementStatuses.Sent, email.AckStatus);
    }

    [Fact]
    public async Task The_same_email_delivered_twice_is_stored_once_and_makes_one_case()
    {
        var m = Email("ravi@sbi.co.in");
        mail.Inbox.Add(m);
        mail.Inbox.Add(Email("ravi@sbi.co.in", id: m.MessageId));   // a second delivery of the same message

        await Processor().RunCycleAsync(default);

        Assert.Equal(1, await db.InboundEmails.CountAsync());
        Assert.Equal(1, await db.Cases.CountAsync());
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task An_email_from_an_unknown_domain_is_kept_and_marked_sender_not_verified_with_no_case_and_no_reply()
    {
        var email = await Receive(Email("someone@unknown.example"));

        Assert.Equal(EmailStatuses.NotVerified, email.Status);
        Assert.Equal(SenderVerification.NotVerified, email.Verification);
        Assert.Contains("unknown.example is not a verified domain", email.VerificationNote);
        Assert.Empty(db.Cases);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task A_subdomain_is_verified_only_when_its_domain_accepts_subdomains()
    {
        var first = await Receive(Email("it@it.sbi.co.in"));
        Assert.Equal(EmailStatuses.NotVerified, first.Status);

        var domain = await db.EmailVerifiedDomains.SingleAsync();
        domain.IncludeSubdomains = true;
        await db.SaveChangesAsync();
        await Processor().ProcessAsync(first.Id, manual: true, default);

        Assert.Equal(EmailStatuses.CaseCreated, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Checking_again_after_the_domain_is_verified_creates_the_case_once_however_often_it_is_pressed()
    {
        var email = await Receive(Email("ops@hdfc.example"));
        var orgHDFC = new EmailOrganisation { Name = "HDFC", NameKey = "hdfc", CreatedAt = DateTime.UtcNow };
        db.EmailOrganisations.Add(orgHDFC);
        db.EmailVerifiedDomains.Add(new EmailVerifiedDomain { Domain = "hdfc.example", OrganisationId = orgHDFC.Id, OrganisationName = "HDFC", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await Processor().ProcessAsync(email.Id, manual: true, default);
        await Processor().ProcessAsync(email.Id, manual: true, default);

        Assert.Equal(1, await db.Cases.CountAsync());
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task Auto_replies_bounces_and_our_own_mail_never_become_cases_and_are_never_answered()
    {
        var outOfOffice = await Receive(Email("ravi@sbi.co.in", "Out of office", also: m => m.Headers.Add(HeaderId.AutoSubmitted, "auto-replied")));
        var bounce = await Receive(Email("mailer-daemon@sbi.co.in", "Undeliverable"));
        var ours = await Receive(Email("support@rma.test", "Re: Card blocked [Ticket C-00009]"));

        Assert.All(new[] { outOfOffice, bounce, ours }, e => Assert.Equal(EmailStatuses.Ignored, e.Status));
        Assert.Empty(db.Cases);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task A_reply_to_our_acknowledgement_is_added_to_the_same_case_not_made_a_new_one()
    {
        await Receive(Email("ravi@sbi.co.in"));
        var ackId = mail.Sent.Single().MessageId;

        var reply = await Receive(Email("colleague@sbi.co.in", "Re: Card blocked [Ticket C-00001]", "Thanks, here is more detail.", inReplyTo: ackId));

        Assert.Equal(EmailStatuses.LinkedToCase, reply.Status);
        Assert.Equal(1, await db.Cases.CountAsync());
        Assert.Equal((await db.Cases.SingleAsync()).Id, reply.CaseId);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.ActionType == "EMAIL_REPLY_RECEIVED" && e.Message.Contains("here is more detail")));
        Assert.Single(mail.Sent);   // the reply is not acknowledged again
    }

    [Fact]
    public async Task A_ticket_number_in_the_subject_links_only_for_the_organisation_whose_case_it_is()
    {
        await Receive(Email("ravi@sbi.co.in"));
        var orgOther = new EmailOrganisation { Name = "Other", NameKey = "other", CreatedAt = DateTime.UtcNow };
        db.EmailOrganisations.Add(orgOther);
        db.EmailVerifiedDomains.Add(new EmailVerifiedDomain { Domain = "other.example", OrganisationId = orgOther.Id, OrganisationName = "Other", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var sameOrg = await Receive(Email("new.person@sbi.co.in", "Update on [Ticket C-00001]"));
        var otherOrg = await Receive(Email("x@other.example", "About [Ticket C-00001]"));

        Assert.Equal(EmailStatuses.LinkedToCase, sameOrg.Status);
        Assert.Equal(EmailStatuses.CaseCreated, otherOrg.Status);
        Assert.NotEqual(sameOrg.CaseId, otherOrg.CaseId);
    }

    [Fact]
    public async Task An_email_that_failed_its_domains_sender_check_is_not_verified_even_from_a_verified_domain()
    {
        // The receiving server's "dmarc=fail" is read by EmailAuthenticator (see EmailVerifiedDomainsTests); here, its verdict.
        authResult = new(EmailAuthenticity.Fail, "dkim=none; dmarc=fail (receiving server)");
        var email = await Receive(Email("ceo@sbi.co.in"));

        Assert.Equal(EmailStatuses.NotVerified, email.Status);
        Assert.Contains("may be forged", email.VerificationNote);
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task When_the_case_details_are_refused_the_email_fails_without_a_case_and_is_not_retried_by_itself()
    {
        createFails = _ => new FieldValidationException([new FieldError("subCategory", "Sub-category 'Cards' is invalid.")]);

        var email = await Receive(Email("ravi@sbi.co.in"));

        Assert.Equal(EmailStatuses.Failed, email.Status);
        Assert.Null(email.CaseId);
        Assert.Null(email.NextAttemptAt);
        Assert.Contains("Sub-category 'Cards' is invalid.", email.StatusNote);
        Assert.Empty(mail.Sent);

        createFails = _ => null;
        await Processor().ProcessAsync(email.Id, manual: true, default);
        Assert.Equal(EmailStatuses.CaseCreated, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task An_acknowledgement_that_cannot_be_sent_keeps_the_case_and_is_retried_later()
    {
        mail.SendFails = true;

        var email = await Receive(Email("ravi@sbi.co.in"));

        Assert.Equal(EmailStatuses.CaseCreated, email.Status);
        Assert.Equal(AcknowledgementStatuses.Failed, email.AckStatus);
        Assert.NotNull(email.AckNextAttemptAt);

        mail.SendFails = false;
        var tracked = await db.InboundEmails.SingleAsync();
        tracked.AckNextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        await Processor().RetryAcknowledgementsAsync(default);

        Assert.Equal(AcknowledgementStatuses.Sent, (await db.InboundEmails.AsNoTracking().SingleAsync()).AckStatus);
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task The_emails_files_are_added_to_the_case_and_a_refused_one_is_named_not_dropped()
    {
        var m = Email("ravi@sbi.co.in");
        var body = new BodyBuilder { TextBody = "See attached." };
        body.Attachments.Add("statement.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
        body.Attachments.Add("tool.exe", "MZ"u8.ToArray(), new ContentType("application", "octet-stream"));
        m.Body = body.ToMessageBody();

        var email = await Receive(m);

        Assert.Equal(EmailStatuses.CaseCreated, email.Status);
        Assert.Equal(new[] { "statement.pdf" }, uploads.Select(u => u.Name));
        Assert.Contains("tool.exe", email.StatusNote);
        Assert.Equal(2, email.AttachmentCount);
    }

    [Fact]
    public async Task One_sender_is_not_sent_more_acknowledgements_than_the_hourly_limit()
    {
        for (var i = 0; i < 12; i++) mail.Inbox.Add(Email("ravi@sbi.co.in", $"Issue {i}"));

        await Processor().RunCycleAsync(default);

        Assert.Equal(12, await db.Cases.CountAsync());
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
    public async Task Under_specific_senders_a_listed_sender_is_verified_and_shown_by_the_configured_name()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"), ("mukul@sbi.co.in", "Mukul"));

        var email = await Receive(Email("tushar@sbi.co.in"));

        Assert.Equal(EmailStatuses.CaseCreated, email.Status);
        Assert.Equal(SenderVerification.Verified, email.Verification);
        Assert.Equal("Tushar", email.SenderAlias);
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        Assert.Equal("Tushar", (await db.Customers.SingleAsync(c => c.Email == "tushar@sbi.co.in")).FullName);   // the alias, not the From line's "Ravi Kumar"
    }

    [Fact]
    public async Task Under_specific_senders_an_unlisted_address_at_the_domain_is_not_verified_but_its_organisation_is_still_known()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"));

        var email = await Receive(Email("unknown@sbi.co.in"));

        Assert.Equal(EmailStatuses.NotVerified, email.Status);
        Assert.Equal(SenderVerification.NotListed, email.Verification);
        Assert.Equal(sbiOrg.Id, email.OrganisationId);
        Assert.Contains("not on State Bank of India's list", email.VerificationNote);
        Assert.Equal(0, casesCreated);
    }

    [Fact]
    public async Task Under_entire_domain_any_sender_is_verified_and_named_by_the_email_itself()
    {
        var email = await Receive(Email("anyone@sbi.co.in"));

        Assert.Equal(EmailStatuses.CaseCreated, email.Status);
        Assert.Null(email.SenderAlias);
        Assert.Equal("Ravi Kumar", (await db.Customers.SingleAsync(c => c.Email == "anyone@sbi.co.in")).FullName);
    }

    [Fact]
    public async Task A_forged_email_is_not_verified_even_from_a_listed_sender_and_creates_no_case()
    {
        UseSpecificSenders(("tushar@sbi.co.in", "Tushar"));
        authResult = new(EmailAuthenticity.Fail, "dkim=none; dmarc=fail p=reject");

        var email = await Receive(Email("tushar@sbi.co.in"));

        Assert.Equal(EmailStatuses.NotVerified, email.Status);
        Assert.Equal(SenderVerification.NotVerified, email.Verification);
        Assert.Equal(EmailAuthenticity.Fail, email.Authenticity);
        Assert.Contains("may be forged", email.VerificationNote);
        Assert.Equal(0, casesCreated);
    }

    [Fact]
    public async Task A_forged_reply_is_never_added_to_the_case_whose_thread_it_claims_to_answer()
    {
        var first = await Receive(Email("ravi@sbi.co.in", id: "first@sbi.co.in"));
        authResult = new(EmailAuthenticity.Fail, "dkim=fail d=sbi.co.in; dmarc=fail p=reject");

        var forged = await Receive(Email("ravi@sbi.co.in", id: "forged@evil.test", inReplyTo: "first@sbi.co.in"));

        Assert.NotNull(first.CaseId);
        Assert.Equal(EmailStatuses.NotVerified, forged.Status);
        Assert.Null(forged.CaseId);
    }

    [Fact]
    public async Task A_domain_that_requires_a_signature_refuses_unsigned_mail_and_retries_when_the_signature_cannot_be_checked()
    {
        db.EmailVerifiedDomains.Single(x => x.Id == sbi.Id).RequireAuthentication = true;
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var unsigned = await Receive(Email("ravi@sbi.co.in"));
        authResult = new(EmailAuthenticity.Unknown, "dkim=temperror d=sbi.co.in; dmarc=temperror");
        var unchecked_ = await Receive(Email("ravi@sbi.co.in", subject: "Second"));
        authResult = new(EmailAuthenticity.Pass, "dkim=pass d=sbi.co.in; dmarc=pass");
        var signed = await Receive(Email("ravi@sbi.co.in", subject: "Third"));

        Assert.Equal(EmailStatuses.NotVerified, unsigned.Status);
        Assert.Contains("requires a valid signature", unsigned.VerificationNote);
        Assert.Equal(EmailStatuses.Failed, unchecked_.Status);
        Assert.NotNull(unchecked_.NextAttemptAt);        // tried again later, not refused
        Assert.Equal(EmailStatuses.CaseCreated, signed.Status);
    }

    [Fact]
    public async Task An_email_from_an_unknown_domain_has_no_organisation_and_is_not_checked_for_a_signature()
    {
        var email = await Receive(Email("someone@elsewhere.test"));

        Assert.Equal(EmailStatuses.NotVerified, email.Status);
        Assert.Null(email.OrganisationId);
        Assert.Equal(EmailAuthenticity.NotChecked, email.Authenticity);
    }

    [Fact]
    public async Task An_email_case_takes_its_details_from_case_settings_and_waits_while_case_settings_cannot_supply_them()
    {
        db.CaseTypeConfigs.RemoveRange(db.CaseTypeConfigs);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        CreateCaseDto? used = null;
        createFails = dto => { used = dto; return null; };

        var waiting = await Receive(Email("ravi@sbi.co.in"));
        var (statusBefore, noteBefore, retryAt) = (waiting.Status, waiting.StatusNote, waiting.NextAttemptAt);

        db.CaseTypeConfigs.Add(new CaseTypeConfig { Code = "Complaint", Name = "Complaint", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.PrioritySlaRules.Add(new PrioritySlaRule { Priority = "Critical", DisplayOrder = 0, IsActive = true, CreatedAt = DateTime.UtcNow });
        db.PrioritySlaRules.Add(new PrioritySlaRule { Priority = "Medium", DisplayOrder = 2, IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await Processor().ProcessAsync(waiting.Id, manual: true, default);

        Assert.Equal(EmailStatuses.Failed, statusBefore);
        Assert.Contains("Case Settings", noteBefore);
        Assert.NotNull(retryAt);                                // tried again by itself
        Assert.Equal(EmailStatuses.CaseCreated, (await db.InboundEmails.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(("Complaint", department, "Cards", "Medium", "English"),     // "Medium" preferred over the first priority
            (used!.CaseType, used.DepartmentId, used.Subcategory, used.Severity, used.PreferredLanguage));
    }
}
