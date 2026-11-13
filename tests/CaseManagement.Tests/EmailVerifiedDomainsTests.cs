using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Customer360;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Verified Domains (organisations, verification policies, sender lists, the change audit) and the email authenticity check.
/// </summary>
/// <remarks>
/// These decide whose email becomes a case for which client. An organisation typed twice splits one client's mail in two; a sender
/// address listed under two domains gives one person two identities; an audit entry with a mailbox password in it leaks the
/// password to everyone who can read the audit. And a From line proves nothing: only the domain's signature does, so the check is
/// exercised with real DKIM signatures, a tampered email and DNS failing.
/// </remarks>
public class EmailVerifiedDomainsTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly EmailIntakeAdminService admin;
    private readonly Guid department = Guid.NewGuid();
    private readonly Guid actor = Guid.NewGuid();

    public EmailVerifiedDomainsTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"email-domains-{Guid.NewGuid()}").Options);
        var team = new Team { Name = "Cards Team", Code = "CARDS", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        db.Departments.Add(new Department { Id = department, Name = "Cards", Code = "CARDS", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamDepartments.Add(new TeamDepartment { TeamId = team.Id, DepartmentId = department });
        db.DepartmentSubCategories.Add(new DepartmentSubCategory { DepartmentId = department, Name = "Card blocked", Code = "BLOCKED", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.CaseTypeConfigs.Add(new CaseTypeConfig { Code = "Complaint", Name = "Complaint", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.PrioritySlaRules.Add(new PrioritySlaRule { Priority = "High", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.LookupValues.Add(new LookupValue { TypeCode = "PREFERRED_LANGUAGE", Value = "English", Label = "English", IsActive = true });
        db.SaveChanges();

        var secrets = new MailboxSecretProtector(Options.Create(new EmailIntakeOptions { SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }),
            Options.Create(new CustomerIdentifierOptions()));
        admin = new EmailIntakeAdminService(db, secrets, new NoMail(), new NoStore());
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private SaveVerifiedDomainDto Domain(string domain, string organisation, string policy = VerificationPolicies.EntireDomain, params (string Address, string Alias)[] senders) => new()
    {
        Domain = domain, OrganisationName = organisation, VerificationPolicy = policy, IsActive = true,
        Senders = senders.Select(s => new VerifiedSenderDto { Address = s.Address, DisplayName = s.Alias, IsActive = true }).ToList(),
    };

    private async Task<List<FieldError>> ErrorsOf(Func<Task> save) => (await Assert.ThrowsAsync<FieldValidationException>(save)).Errors.ToList();

    // ───────────── organisations ─────────────

    [Fact]
    public async Task Two_domains_of_one_organisation_share_one_organisation_record_however_its_name_is_typed()
    {
        var first = await admin.SaveDomainAsync(null, Domain("bsn.com.in", "BSN"), actor, default);
        var second = await admin.SaveDomainAsync(null, Domain("bsn.my", "  bsn "), actor, default);

        Assert.Equal(first.OrganisationId, second.OrganisationId);
        Assert.Equal("BSN", second.OrganisationName);
        Assert.Single(await db.EmailOrganisations.ToListAsync());
        Assert.Equal(2, (await admin.OrganisationsAsync(default)).Single().DomainCount);
    }

    // ───────────── sender lists ─────────────

    [Fact]
    public async Task A_specific_senders_domain_keeps_its_listed_senders_normalised()
    {
        var saved = await admin.SaveDomainAsync(null, Domain("bsn.com.in", "BSN", VerificationPolicies.SpecificSenders,
            (" Tushar@BSN.com.in ", "Tushar"), ("mukul@bsn.com.in", "  Mukul  Garg ")), actor, default);

        Assert.Equal(VerificationPolicies.SpecificSenders, saved.VerificationPolicy);
        Assert.Equal(new[] { "mukul@bsn.com.in", "tushar@bsn.com.in" }, saved.Senders.Select(s => s.Address).OrderBy(a => a));
        Assert.Contains(saved.Senders, s => s.DisplayName == "Mukul Garg");
    }

    [Fact]
    public async Task Specific_senders_needs_at_least_one_active_sender()
    {
        var errors = await ErrorsOf(() => admin.SaveDomainAsync(null, Domain("bsn.com.in", "BSN", VerificationPolicies.SpecificSenders), actor, default));

        Assert.Contains(errors, e => e.Field == "senders");
    }

    [Fact]
    public async Task A_sender_must_be_an_address_at_the_domain_listed_once_and_not_belong_to_another_domain()
    {
        await admin.SaveDomainAsync(null, Domain("other.com", "Other", VerificationPolicies.SpecificSenders, ("taken@other.com", "Taken")), actor, default);

        var errors = await ErrorsOf(() => admin.SaveDomainAsync(null, Domain("bsn.com.in", "BSN", VerificationPolicies.SpecificSenders,
            ("tushar@gmail.com", "Wrong domain"), ("mukul@bsn.com.in", "Mukul"), ("MUKUL@bsn.com.in", "Twice"), ("ok@bsn.com.in", "")), actor, default));

        Assert.Contains(errors, e => e.Field == "senders.0.address" && e.Message.Contains("bsn.com.in"));
        Assert.Contains(errors, e => e.Field == "senders.2.address" && e.Message.Contains("twice"));
        Assert.Contains(errors, e => e.Field == "senders.3.displayName");

        var taken = await ErrorsOf(() => admin.SaveDomainAsync(null, Domain("other.com.my", "Other", VerificationPolicies.SpecificSenders,
            ("taken@other.com", "Taken")), actor, default));
        Assert.Contains(taken, e => e.Field == "senders.0.address");
    }

    [Fact]
    public async Task Saving_the_list_again_keeps_unchanged_senders_and_removes_the_ones_left_out()
    {
        var saved = await admin.SaveDomainAsync(null, Domain("bsn.com.in", "BSN", VerificationPolicies.SpecificSenders,
            ("tushar@bsn.com.in", "Tushar"), ("mukul@bsn.com.in", "Mukul")), actor, default);
        var tusharId = saved.Senders.Single(s => s.Address == "tushar@bsn.com.in").Id;

        var again = Domain("bsn.com.in", "BSN", VerificationPolicies.SpecificSenders, ("tushar@bsn.com.in", "Tushar V"), ("new@bsn.com.in", "New"));
        var resaved = await admin.SaveDomainAsync(saved.Id, again, actor, default);

        Assert.Equal(tusharId, resaved.Senders.Single(s => s.Address == "tushar@bsn.com.in").Id);
        Assert.Equal("Tushar V", resaved.Senders.Single(s => s.Address == "tushar@bsn.com.in").DisplayName);
        Assert.DoesNotContain(resaved.Senders, s => s.Address == "mukul@bsn.com.in");
        Assert.Equal(2, await db.EmailVerifiedSenders.CountAsync());
    }

    // ───────────── audit ─────────────

    [Fact]
    public async Task Every_maintenance_change_is_audited_with_who_and_what_and_never_the_password()
    {
        var mailbox = await admin.CreateMailboxAsync(new SaveMailboxDto
        {
            Name = "Support", Address = "support@company.test", Password = "Sup3r-Secret!", ImapHost = "mail.company.test", ImapPort = 993,
            ImapSecurity = MailSecurity.SslOnConnect, SmtpHost = "mail.company.test", SmtpPort = 465, SmtpSecurity = MailSecurity.SslOnConnect, IsEnabled = true,
        }, actor, default);
        var domain = await admin.SaveDomainAsync(null, Domain("bsn.com.in", "BSN"), actor, default);
        await admin.DeleteDomainAsync(domain.Id, actor, default);

        var audit = await db.CaseEvents.Where(e => e.Module == "EmailMaintenance").OrderBy(e => e.CreatedAt).ToListAsync();

        Assert.Equal(new[] { "MAILBOX_CREATED", "DOMAIN_CREATED", "DOMAIN_DELETED" }, audit.Select(a => a.ActionType));
        Assert.All(audit, a => Assert.Equal(actor, a.UserId));
        Assert.All(audit, a => Assert.DoesNotContain("Sup3r-Secret!", (a.OldValue ?? "") + (a.NewValue ?? "")));
        Assert.Contains("\"PasswordChanged\":true", audit[0].NewValue);
        Assert.Equal(mailbox.Id, (await db.EmailMailboxes.SingleAsync()).Id);
    }

    // ───────────── authenticity (real DKIM signatures) ─────────────

    private static readonly AsymmetricCipherKeyPair Key = NewKey();

    private static AsymmetricCipherKeyPair NewKey()
    {
        var generator = new Org.BouncyCastle.Crypto.Generators.RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        return generator.GenerateKeyPair();
    }

    private static string PublicKeyRecord() =>
        "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(Key.Public).GetEncoded());

    private sealed class FakeDns(Dictionary<string, string[]> records, bool down = false) : IDnsTxtResolver
    {
        public Task<DnsTxtAnswer> TxtAsync(string name, CancellationToken ct) => Task.FromResult(
            down ? DnsTxtAnswer.Failed : new DnsTxtAnswer(true, records.TryGetValue(name, out var r) ? r : Array.Empty<string>()));
    }

    /// <summary>An email from <paramref name="from"/>, signed by <paramref name="signingDomain"/> when given, as it arrives (written out and read back).</summary>
    private static MimeMessage Arrived(string from, string? signingDomain, Action<MimeMessage>? afterSigning = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Tushar", from));
        m.To.Add(new MailboxAddress("Support", "support@company.test"));
        m.Subject = "Card blocked";
        m.Date = DateTimeOffset.UtcNow;
        m.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("sender.test");
        m.Body = new TextPart("plain") { Text = "My card ending 1234 is blocked.\r\n" };
        if (signingDomain != null)
        {
            var signer = new DkimSigner(Key.Private, signingDomain, "sel", DkimSignatureAlgorithm.RsaSha256)
            {
                HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
            };
            m.Prepare(EncodingConstraint.SevenBit);
            signer.Sign(m, new[] { HeaderId.From, HeaderId.To, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId });
        }
        afterSigning?.Invoke(m);
        using var raw = new MemoryStream();
        m.WriteTo(raw);
        raw.Position = 0;
        return MimeMessage.Load(raw);
    }

    private static Dictionary<string, string[]> Dns(string? dmarc = null, string keyDomain = "bsn.com.in")
    {
        var d = new Dictionary<string, string[]> { [$"sel._domainkey.{keyDomain}"] = new[] { PublicKeyRecord() } };
        if (dmarc != null) d["_dmarc.bsn.com.in"] = new[] { dmarc };
        return d;
    }

    [Fact]
    public async Task A_valid_signature_of_the_sender_domain_passes()
    {
        var result = await new EmailAuthenticator(new FakeDns(Dns("v=DMARC1; p=reject"))).CheckAsync(Arrived("tushar@bsn.com.in", "bsn.com.in"), "bsn.com.in", default);

        Assert.Equal(EmailAuthenticity.Pass, result.Result);
        Assert.Contains("dkim=pass d=bsn.com.in", result.Detail);
    }

    [Fact]
    public async Task An_email_altered_after_signing_fails()
    {
        var tampered = Arrived("tushar@bsn.com.in", "bsn.com.in", m => m.Subject = "Please wire the money to account 999");

        var result = await new EmailAuthenticator(new FakeDns(Dns())).CheckAsync(tampered, "bsn.com.in", default);

        Assert.Equal(EmailAuthenticity.Fail, result.Result);
        Assert.Contains("dkim=fail", result.Detail);
    }

    [Fact]
    public async Task An_unsigned_email_fails_when_the_domain_publishes_reject_and_is_merely_unproven_when_it_publishes_nothing()
    {
        var rejecting = await new EmailAuthenticator(new FakeDns(Dns("v=DMARC1; p=reject"))).CheckAsync(Arrived("tushar@bsn.com.in", null), "bsn.com.in", default);
        var silent = await new EmailAuthenticator(new FakeDns(Dns())).CheckAsync(Arrived("tushar@bsn.com.in", null), "bsn.com.in", default);

        Assert.Equal(EmailAuthenticity.Fail, rejecting.Result);
        Assert.Equal(EmailAuthenticity.None, silent.Result);
    }

    [Fact]
    public async Task A_valid_signature_of_another_domain_proves_nothing_about_the_sender_domain()
    {
        var result = await new EmailAuthenticator(new FakeDns(Dns("v=DMARC1; p=none", keyDomain: "evil.test")))
            .CheckAsync(Arrived("tushar@bsn.com.in", "evil.test"), "bsn.com.in", default);

        Assert.Equal(EmailAuthenticity.None, result.Result);
        Assert.Contains("dkim=pass d=evil.test", result.Detail);
    }

    [Fact]
    public async Task When_dns_cannot_be_reached_the_result_is_unknown_never_pass()
    {
        var result = await new EmailAuthenticator(new FakeDns(new(), down: true)).CheckAsync(Arrived("tushar@bsn.com.in", "bsn.com.in"), "bsn.com.in", default);

        Assert.Equal(EmailAuthenticity.Unknown, result.Result);
    }

    [Fact]
    public async Task A_dmarc_fail_recorded_by_the_receiving_server_is_honoured_but_a_pass_written_by_the_sender_is_not()
    {
        var claimsPass = Arrived("tushar@bsn.com.in", null, m => m.Headers.Add("Authentication-Results", "mx.company.test; dmarc=pass header.from=bsn.com.in"));
        var failed = Arrived("tushar@bsn.com.in", null, m => m.Headers.Add("Authentication-Results", "mx.company.test; dmarc=fail header.from=bsn.com.in"));

        Assert.Equal(EmailAuthenticity.None, (await new EmailAuthenticator(new FakeDns(Dns())).CheckAsync(claimsPass, "bsn.com.in", default)).Result);
        Assert.Equal(EmailAuthenticity.Fail, (await new EmailAuthenticator(new FakeDns(Dns())).CheckAsync(failed, "bsn.com.in", default)).Result);
    }

    [Theory]
    [InlineData("bsn.com.in", "bsn.com.in", true)]
    [InlineData("bsn.com.in", "mail.bsn.com.in", true)]
    [InlineData("mail.bsn.com.in", "bsn.com.in", true)]
    [InlineData("evil.test", "bsn.com.in", false)]
    [InlineData("notbsn.com.in", "bsn.com.in", false)]
    public void Alignment_accepts_the_domain_and_its_sub_domains_only(string signer, string from, bool aligned) =>
        Assert.Equal(aligned, EmailAuthenticator.Aligned(signer, from));

    // ───────────── fakes the admin service needs but these tests never use ─────────────

    private sealed class NoMail : IMailboxClient
    {
        public Task<MailboxCursor> ReadNewAsync(MailboxConnection m, MailboxCursor? c, DateTime? s, int max, long maxBytes, Func<IncomingMail, MailboxCursor, Task> h, CancellationToken ct) => throw new NotSupportedException();
        public Task SendAsync(MailboxConnection m, MimeMessage message, CancellationToken ct) => throw new NotSupportedException();
        public Task<MailboxTestResult> TestAsync(MailboxConnection m, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoStore : IAttachmentStore
    {
        public Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
