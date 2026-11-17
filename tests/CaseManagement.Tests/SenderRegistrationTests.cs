using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Customer360;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Sender Verification → Senders: registering one sender directly, and the mailbox and team a sender is routed to.
/// </summary>
/// <remarks>
/// A sender can be registered two ways (inside its domain, or on its own), and both must end in the same single record: two rows
/// for one address would give one person two verification states. Registering a sender at an unknown domain must never verify
/// everyone at that domain. And a sender's team is only a suggestion inside the mailbox's own access: a mapping to a team the
/// mailbox is not shared with would hand a client's mail to people not allowed to see it, so it is refused, and a mailbox cannot
/// drop a team that senders still route to.
/// </remarks>
public class SenderRegistrationTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly EmailIntakeAdminService admin;
    private readonly Guid actor = Guid.NewGuid();
    private readonly Team support = new() { Name = "Customer Support", Code = "SUP", IsActive = true, CreatedAt = DateTime.UtcNow };
    private readonly Team complaints = new() { Name = "Complaints", Code = "CMP", IsActive = true, CreatedAt = DateTime.UtcNow };
    private readonly Team closed = new() { Name = "Old Team", Code = "OLD", IsActive = false, CreatedAt = DateTime.UtcNow };
    private readonly EmailMailbox supportBox = new() { Name = "Support", Address = "support@company.example", ImapHost = "imap.x", SmtpHost = "smtp.x", CreatedAt = DateTime.UtcNow };

    public SenderRegistrationTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"senders-{Guid.NewGuid()}").Options);
        db.Teams.AddRange(support, complaints, closed);
        supportBox.Teams.Add(new EmailMailboxTeam { MailboxId = supportBox.Id, TeamId = support.Id });
        supportBox.Teams.Add(new EmailMailboxTeam { MailboxId = supportBox.Id, TeamId = closed.Id });
        db.EmailMailboxes.Add(supportBox);
        db.SaveChanges();

        var secrets = new MailboxSecretProtector(Options.Create(new EmailIntakeOptions { SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }),
            Options.Create(new CustomerIdentifierOptions()));
        admin = new EmailIntakeAdminService(db, secrets, new NoMail(), new NoStore());
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private SaveSenderDto Sender(string address, string organisation = "BSN Organization", Guid? mailbox = null, Guid? team = null) => new()
    {
        Address = address, DisplayName = "Sender One", OrganisationName = organisation, IsActive = true, MailboxId = mailbox, TeamId = team,
    };

    private async Task<List<FieldError>> ErrorsOf(Func<Task> save) => (await Assert.ThrowsAsync<FieldValidationException>(save)).Errors.ToList();

    [Fact]
    public async Task A_sender_at_an_unregistered_domain_registers_the_domain_for_listed_senders_only()
    {
        var saved = await admin.SaveSenderAsync(null, Sender("Sender1@BSN.example", mailbox: supportBox.Id, team: support.Id), actor, default);

        var domain = await db.EmailVerifiedDomains.Include(d => d.Organisation).SingleAsync();
        Assert.Equal("bsn.example", domain.Domain);
        Assert.Equal(VerificationPolicies.SpecificSenders, domain.VerificationPolicy);
        Assert.Equal("BSN Organization", domain.Organisation!.Name);
        Assert.Equal("sender1@bsn.example", saved.Address);
        Assert.Equal("Customer Support", saved.TeamName);
        Assert.Equal(domain.Id, saved.VerifiedDomainId);
    }

    [Fact]
    public async Task Registering_a_second_sender_joins_the_existing_domain_and_its_organisation()
    {
        await admin.SaveSenderAsync(null, Sender("sender1@bsn.example"), actor, default);

        await admin.SaveSenderAsync(null, Sender("sender2@bsn.example", organisation: "  bsn   organization "), actor, default);

        Assert.Single(await db.EmailVerifiedDomains.ToListAsync());
        Assert.Single(await db.EmailOrganisations.ToListAsync());
        Assert.Equal(2, (await admin.SendersAsync(default)).Count);
    }

    [Fact]
    public async Task An_address_registered_through_its_domain_cannot_be_registered_again_directly()
    {
        await admin.SaveDomainAsync(null, new SaveVerifiedDomainDto
        {
            Domain = "bsn.example", OrganisationName = "BSN Organization", VerificationPolicy = VerificationPolicies.SpecificSenders, IsActive = true,
            Senders = [new VerifiedSenderDto { Address = "sender1@bsn.example", DisplayName = "One", IsActive = true }],
        }, actor, default);

        var errors = await ErrorsOf(() => admin.SaveSenderAsync(null, Sender("sender1@bsn.example"), actor, default));

        Assert.Contains(errors, e => e.Field == "address" && e.Message.Contains("already registered"));
        Assert.Single(await db.EmailVerifiedSenders.ToListAsync());
    }

    [Fact]
    public async Task A_sender_at_a_domain_of_another_organisation_is_refused_with_the_owner_named()
    {
        await admin.SaveSenderAsync(null, Sender("sender1@bsn.example"), actor, default);

        var errors = await ErrorsOf(() => admin.SaveSenderAsync(null, Sender("sender2@bsn.example", organisation: "ABC Bank"), actor, default));

        Assert.Contains(errors, e => e.Field == "organisationName" && e.Message.Contains("BSN Organization"));
    }

    [Fact]
    public async Task A_team_the_mailbox_is_not_shared_with_is_refused()
    {
        var errors = await ErrorsOf(() => admin.SaveSenderAsync(null, Sender("sender1@bsn.example", mailbox: supportBox.Id, team: complaints.Id), actor, default));

        Assert.Contains(errors, e => e.Field == "teamId" && e.Message.Contains("is not shared with Complaints"));
        Assert.Empty(await db.EmailVerifiedSenders.ToListAsync());
    }

    [Fact]
    public async Task A_team_without_a_mailbox_and_an_inactive_team_are_refused()
    {
        var noMailbox = await ErrorsOf(() => admin.SaveSenderAsync(null, Sender("sender1@bsn.example", team: support.Id), actor, default));
        var inactive = await ErrorsOf(() => admin.SaveSenderAsync(null, Sender("sender1@bsn.example", mailbox: supportBox.Id, team: closed.Id), actor, default));

        Assert.Contains(noMailbox, e => e.Field == "teamId" && e.Message.Contains("mailbox first"));
        Assert.Contains(inactive, e => e.Field == "teamId" && e.Message.Contains("inactive"));
    }

    [Fact]
    public async Task The_same_mapping_rule_applies_to_senders_saved_inside_their_domain()
    {
        var errors = await ErrorsOf(() => admin.SaveDomainAsync(null, new SaveVerifiedDomainDto
        {
            Domain = "bsn.example", OrganisationName = "BSN", VerificationPolicy = VerificationPolicies.SpecificSenders, IsActive = true,
            Senders = [new VerifiedSenderDto { Address = "sender1@bsn.example", DisplayName = "One", IsActive = true, MailboxId = supportBox.Id, TeamId = complaints.Id }],
        }, actor, default));

        Assert.Contains(errors, e => e.Field == "senders.0.teamId");
    }

    [Fact]
    public async Task A_public_email_provider_address_cannot_be_registered_by_organisation()
    {
        var errors = await ErrorsOf(() => admin.SaveSenderAsync(null, Sender("someone@gmail.com"), actor, default));

        Assert.Contains(errors, e => e.Field == "address" && e.Message.Contains("public email provider"));
    }

    [Fact]
    public async Task Changing_a_senders_address_to_another_domain_is_refused()
    {
        var saved = await admin.SaveSenderAsync(null, Sender("sender1@bsn.example"), actor, default);

        var errors = await ErrorsOf(() => admin.SaveSenderAsync(saved.Id, Sender("sender1@other.example"), actor, default));

        Assert.Contains(errors, e => e.Field == "address" && e.Message.Contains("must stay at bsn.example"));
    }

    [Fact]
    public async Task A_mailbox_cannot_drop_a_team_that_senders_are_still_routed_to()
    {
        await admin.SaveSenderAsync(null, Sender("sender1@bsn.example", mailbox: supportBox.Id, team: support.Id), actor, default);
        var dto = new SaveMailboxDto
        {
            Name = supportBox.Name, Address = supportBox.Address, Username = "support@company.example", Password = "x",
            ImapHost = "imap.x", ImapPort = 993, ImapSecurity = MailSecurity.SslOnConnect, SmtpHost = "smtp.x", SmtpPort = 587, SmtpSecurity = MailSecurity.StartTls,
            IsEnabled = true, TeamIds = [complaints.Id],
        };

        var errors = await ErrorsOf(() => admin.UpdateMailboxAsync(supportBox.Id, dto, actor, default));

        Assert.Contains(errors, e => e.Field == "teamIds" && e.Message.Contains("sender1@bsn.example"));
    }

    [Fact]
    public async Task Registering_changing_and_removing_a_sender_are_each_audited_with_the_mapping()
    {
        var saved = await admin.SaveSenderAsync(null, Sender("sender1@bsn.example", mailbox: supportBox.Id, team: support.Id), actor, default);
        await admin.SaveSenderAsync(saved.Id, Sender("sender1@bsn.example", mailbox: supportBox.Id), actor, default);
        await admin.DeleteSenderAsync(saved.Id!.Value, actor, default);

        var actions = await db.CaseEvents.Where(e => e.EntityName == "EmailVerifiedSender").OrderBy(e => e.CreatedAt).Select(e => e.ActionType).ToListAsync();
        Assert.Equal(["SENDER_REGISTERED", "SENDER_UPDATED", "SENDER_DELETED"], actions);
        var change = await db.CaseEvents.SingleAsync(e => e.ActionType == "SENDER_UPDATED");
        Assert.Contains("Customer Support", change.OldValue);
        Assert.DoesNotContain("Customer Support", change.NewValue);
    }

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
