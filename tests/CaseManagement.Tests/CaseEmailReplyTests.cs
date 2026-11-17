using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Customer360;
using CaseManagement.Api.Services.EmailIntake;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Customer Reply by email: who it goes to, from which mailbox, how it threads into the customer's conversation, and how sending
/// fails and retries.
/// </summary>
/// <remarks>
/// This is the first time an agent's words leave the system. A reply sent to the wrong address, from a mailbox the case's team does
/// not work, or twice from two servers, reaches a real customer; one that fails silently looks answered when it was not. Threading
/// matters both ways: the customer must see the reply in their conversation, and their answer must come back to the same case.
/// </remarks>
public class CaseEmailReplyTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly MailboxSecretProtector secrets;
    private readonly RecordingMail mail = new();
    private readonly Team team = new() { Name = "Cards", Code = "CD", IsActive = true, CreatedAt = DateTime.UtcNow };
    private readonly EmailMailbox support;
    private readonly Case c;
    private readonly Guid agent = Guid.NewGuid();

    public CaseEmailReplyTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"case-email-{Guid.NewGuid()}").Options);
        secrets = new MailboxSecretProtector(Options.Create(new EmailIntakeOptions { SecretKey = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()) }),
            Options.Create(new CustomerIdentifierOptions()));
        support = Mailbox("Support", "support@rma.test");
        db.Teams.Add(team);
        c = new Case
        {
            CaseNumber = "C-01263", Title = "Card blocked", CaseType = "Complaint", Severity = "High", TeamId = team.Id, OwnerId = agent,
            SourceChannel = "Email", PreferredCommunicationChannel = "Email", ContactEmail = "ravi@sbi.co.in", CreatedAt = DateTime.UtcNow,
        };
        db.Cases.Add(c);
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private EmailMailbox Mailbox(string name, string address, bool sharedWithTeam = true)
    {
        var m = new EmailMailbox { Name = name, Address = address, ImapHost = "i", SmtpHost = "s", PasswordEncrypted = secrets.Protect("pw"), IsEnabled = true, CreatedAt = DateTime.UtcNow };
        if (sharedWithTeam) m.Teams.Add(new EmailMailboxTeam { MailboxId = m.Id, TeamId = team.Id });
        db.EmailMailboxes.Add(m);
        db.SaveChanges();
        return m;
    }

    private CaseEmailSender Sender(int maxAttempts = 5) => new(db, mail, secrets, Options.Create(new EmailIntakeOptions { MaxAttempts = maxAttempts }), NullLogger<CaseEmailSender>.Instance);

    private async Task<CaseEmailMessage> StageAsync(string body = "We have unblocked your card.")
    {
        var tracked = await db.Cases.FirstAsync(x => x.Id == c.Id);
        var mailbox = await CaseEmailOutbox.ResolveMailboxAsync(db, tracked, null, default);
        var ev = new CaseEvent { CaseId = c.Id, EventType = EventType.Comment, Message = body, IsInternal = false, CreatedAt = DateTime.UtcNow, UserId = agent };
        db.CaseEvents.Add(ev);
        var staged = await CaseEmailOutbox.StageReplyAsync(db, tracked, mailbox, ev.Id, body, agent, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return staged;
    }

    [Fact]
    public async Task A_reply_answers_the_customers_latest_email_in_their_own_conversation_and_names_the_case()
    {
        db.InboundEmails.Add(new InboundEmail
        {
            MailboxId = support.Id, MessageKey = "first", InternetMessageId = "first@sbi.co.in", References = "older@sbi.co.in", Subject = "Card blocked",
            FromAddress = "ravi@sbi.co.in", CaseId = c.Id, ReceivedAt = DateTime.UtcNow.AddHours(-2), CreatedAt = DateTime.UtcNow,
        });
        db.InboundEmails.Add(new InboundEmail
        {
            MailboxId = support.Id, MessageKey = "second", InternetMessageId = "second@sbi.co.in", Subject = "Re: Card blocked",
            FromAddress = "ravi@sbi.co.in", CaseId = c.Id, ReceivedAt = DateTime.UtcNow.AddHours(-1), CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var staged = await StageAsync();

        Assert.Equal(("ravi@sbi.co.in", "Re: Card blocked [Case C-01263]", "second@sbi.co.in"), (staged.ToAddress, staged.Subject, staged.InReplyTo));
        Assert.Equal("older@sbi.co.in first@sbi.co.in second@sbi.co.in", staged.References);
        Assert.Equal(support.Id, (await db.Cases.AsNoTracking().SingleAsync()).ReplyMailboxId);   // the case remembers its mailbox
    }

    [Fact]
    public async Task A_second_reply_answers_the_first_one()
    {
        var first = await StageAsync("First answer.");
        var second = await StageAsync("Second answer.");

        Assert.Equal(first.MessageId, second.InReplyTo);
        Assert.Equal("Re: Card blocked [Case C-01263]", second.Subject);
    }

    [Fact]
    public async Task No_address_or_no_clear_mailbox_refuses_the_reply_before_anything_is_saved()
    {
        var noAddress = new Case { CaseNumber = "C-2", TeamId = team.Id, OwnerId = agent, ContactEmail = null };
        Mailbox("Complaints", "complaints@rma.test");   // the team now works two mailboxes

        var missing = await Assert.ThrowsAsync<FieldValidationException>(() => CaseEmailOutbox.ResolveMailboxAsync(db, noAddress, null, default));
        var ambiguous = await Assert.ThrowsAsync<FieldValidationException>(() => CaseEmailOutbox.ResolveMailboxAsync(db, c, null, default));
        var chosen = await CaseEmailOutbox.ResolveMailboxAsync(db, c, support.Id, default);

        Assert.Contains("no customer email address", Assert.Single(missing.Errors).Message);
        Assert.Contains("Choose the mailbox", Assert.Single(ambiguous.Errors).Message);
        Assert.Equal(support.Id, chosen.Id);
    }

    [Fact]
    public async Task A_mailbox_the_cases_team_does_not_work_can_never_be_chosen()
    {
        var other = Mailbox("Other", "other@rma.test", sharedWithTeam: false);

        await Assert.ThrowsAsync<FieldValidationException>(() => CaseEmailOutbox.ResolveMailboxAsync(db, c, other.Id, default));
    }

    [Fact]
    public async Task The_email_is_sent_once_from_the_mailbox_threaded_and_marked_sent()
    {
        var staged = await StageAsync();

        await Sender().SendForCaseAsync(c.Id, default);
        await Sender().SendDueAsync(default);   // nothing left: never sent twice

        var sent = Assert.Single(mail.Sent);
        Assert.Equal(("support@rma.test", "ravi@sbi.co.in", staged.MessageId), (sent.From.Mailboxes.Single().Address, sent.To.Mailboxes.Single().Address, sent.MessageId));
        Assert.Contains("We have unblocked your card.", sent.TextBody);
        var stored = await db.CaseEmailMessages.AsNoTracking().SingleAsync();
        Assert.Equal((CaseEmailStatuses.Sent, null), (stored.Status, stored.NextAttemptAt));
    }

    [Fact]
    public async Task A_failure_is_kept_and_retried_and_after_the_last_attempt_the_case_says_so_and_Retry_starts_over()
    {
        await StageAsync();
        mail.Fail = true;

        await Sender(maxAttempts: 2).SendForCaseAsync(c.Id, default);
        var afterFirst = await db.CaseEmailMessages.AsNoTracking().SingleAsync();
        var due = db.CaseEmailMessages.Single();
        due.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        await Sender(maxAttempts: 2).SendDueAsync(default);
        var afterLast = await db.CaseEmailMessages.AsNoTracking().SingleAsync();
        mail.Fail = false;
        var retried = await Sender(maxAttempts: 2).RetryAsync(c.Id, afterLast.Id, default);

        Assert.Equal(CaseEmailStatuses.Failed, afterFirst.Status);
        Assert.NotNull(afterFirst.NextAttemptAt);                 // will be tried again
        Assert.Contains("refused", afterFirst.Error);
        Assert.Null(afterLast.NextAttemptAt);                     // out of attempts
        Assert.Single(db.CaseEvents.Where(e => e.ActionType == "EMAIL_REPLY_FAILED"));
        Assert.Equal(CaseEmailStatuses.Sent, retried.Status);
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task The_reply_box_shows_where_a_reply_would_go_or_why_it_cannot_be_emailed()
    {
        var ready = await CaseEmailOutbox.OptionsAsync(db, c, default);
        Mailbox("Complaints", "complaints@rma.test");
        var choose = await CaseEmailOutbox.OptionsAsync(db, c, default);
        var voice = await CaseEmailOutbox.OptionsAsync(db, new Case { CaseNumber = "C-3", TeamId = team.Id, PreferredCommunicationChannel = "Phone", ContactEmail = "x@y.com" }, default);

        Assert.Equal((true, "ravi@sbi.co.in", "Re: Card blocked [Case C-01263]", "support@rma.test"), (ready.CanEmail, ready.To, ready.Subject, ready.Mailbox?.Address));
        Assert.Equal((true, null, 2), (choose.CanEmail, choose.Mailbox, choose.Mailboxes.Count));
        Assert.False(voice.CanEmail);
        Assert.Contains("not Email", voice.Reason);
    }

    private sealed class RecordingMail : IMailboxClient
    {
        public List<MimeMessage> Sent { get; } = new();
        public bool Fail { get; set; }
        public Task<MailboxCursor> ReadNewAsync(MailboxConnection m, MailboxCursor? c, DateTime? s, int max, long maxBytes, Func<IncomingMail, MailboxCursor, Task> h, CancellationToken ct) => throw new NotSupportedException();
        public Task SendAsync(MailboxConnection m, MimeMessage message, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("The mail server refused the message.");
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task<MailboxTestResult> TestAsync(MailboxConnection m, CancellationToken ct) => throw new NotSupportedException();
    }
}
