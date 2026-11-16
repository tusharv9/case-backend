using CaseManagement.Api.Data;
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
/// The Inbox tabs (Ticket Created / Case Created) and their counts.
/// </summary>
/// <remarks>
/// An email's status records what intake did with it, and it never changes afterwards: an email that opened a ticket stays
/// "TicketCreated" after someone assigns that ticket and it becomes a case (only its case link is filled in). The tabs answer a
/// different question, "where is this email now?", so they must read the case link too. Before that, every email whose ticket
/// became a case stayed counted under Ticket Created and the Case Created tab showed only the emails of the old direct-to-case flow.
/// </remarks>
public class InboxTabsTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly EmailIntakeAdminService admin;
    private readonly EmailMailbox mailbox;

    public InboxTabsTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"inbox-tabs-{Guid.NewGuid()}").Options);
        mailbox = new EmailMailbox { Name = "Support", Address = "support@rma.test", ImapHost = "i", SmtpHost = "s", CreatedAt = DateTime.UtcNow };
        db.EmailMailboxes.Add(mailbox);
        db.SaveChanges();

        var secrets = new MailboxSecretProtector(Options.Create(new EmailIntakeOptions { SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }),
            Options.Create(new CustomerIdentifierOptions()));
        admin = new EmailIntakeAdminService(db, secrets, new NoMail(), new NoStore());
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private void Email(string key, string status, bool hasTicket = false, bool hasCase = false) =>
        db.InboundEmails.Add(new InboundEmail
        {
            MailboxId = mailbox.Id, MessageKey = key, Subject = key, FromAddress = "ravi@gmail.com", Status = status,
            TicketId = hasTicket ? Guid.NewGuid() : null, CaseId = hasCase ? Guid.NewGuid() : null,
            ReceivedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        });

    /// <summary>One email of every shape the tabs must place.</summary>
    private async Task SeedEveryShapeAsync()
    {
        Email("ticket-waiting", EmailStatuses.TicketCreated, hasTicket: true);
        Email("reply-on-waiting-ticket", EmailStatuses.LinkedToTicket, hasTicket: true);
        Email("ticket-became-case", EmailStatuses.TicketCreated, hasTicket: true, hasCase: true);
        Email("reply-on-ticket-that-became-case", EmailStatuses.LinkedToTicket, hasTicket: true, hasCase: true);
        Email("direct-case", EmailStatuses.CaseCreated, hasCase: true);
        Email("reply-on-direct-case", EmailStatuses.LinkedToCase, hasCase: true);
        Email("unverified", EmailStatuses.NotVerified);
        Email("failed", EmailStatuses.Failed);
        await db.SaveChangesAsync();
    }

    private async Task<string[]> KeysOf(string filter) =>
        (await admin.ListAsync(filter, null, null, 1, 100, default)).Items.Select(e => e.Subject).Order().ToArray();

    [Fact]
    public async Task An_email_whose_ticket_became_a_case_is_listed_under_Case_Created_not_Ticket_Created()
    {
        await SeedEveryShapeAsync();

        var cases = await KeysOf("case-created");
        var tickets = await KeysOf("ticket-created");

        Assert.Equal(new[] { "direct-case", "reply-on-direct-case", "reply-on-ticket-that-became-case", "ticket-became-case" }, cases);
        Assert.Equal(new[] { "reply-on-waiting-ticket", "ticket-waiting" }, tickets);
    }

    [Fact]
    public async Task The_tab_counts_agree_with_the_tabs_and_still_add_up_to_All()
    {
        await SeedEveryShapeAsync();

        var counts = await admin.CountsAsync(null, default);

        Assert.Equal((await KeysOf("case-created")).Length, counts.CaseCreated);
        Assert.Equal((await KeysOf("ticket-created")).Length, counts.TicketCreated);
        Assert.Equal(4, counts.CaseCreated);
        Assert.Equal(2, counts.TicketCreated);
        Assert.Equal(1, counts.NotVerified);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(8, counts.All);
        Assert.Equal(counts.All, counts.CaseCreated + counts.TicketCreated + counts.NotVerified + counts.Failed + counts.Other);
    }

    [Fact]
    public async Task Placing_an_email_under_Case_Created_never_rewrites_its_stored_status()
    {
        await SeedEveryShapeAsync();

        await admin.ListAsync("case-created", null, null, 1, 100, default);
        await admin.CountsAsync(null, default);

        // Reply threading matches on the stored status, so the tabs must stay a read-time view of it.
        var stored = await db.InboundEmails.AsNoTracking().SingleAsync(e => e.MessageKey == "ticket-became-case");
        Assert.Equal(EmailStatuses.TicketCreated, stored.Status);
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
