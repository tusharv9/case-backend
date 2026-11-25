using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.EmailIntake;
using CaseManagement.Api.Services.Meetings;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>Schedule Meeting: a meeting saved on the case, the customer emailed a calendar invite, and updates that replace it.</summary>
/// <remarks>
/// <para>The invitation goes to a customer outside the bank, so what it says must be right: the time in the platform's zone,
/// stored and sent in UTC; a second click must not send a second invite; an update or cancellation must carry the same calendar
/// identity with a higher sequence so the customer's calendar moves or removes the one entry instead of adding another.</para>
/// <para>Times are given as a date and times in the platform zone (here Asia/Kuala_Lumpur, UTC+8, no daylight saving).</para>
/// </remarks>
public class CaseMeetingTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Team team;
    private readonly User agent, other, supervisor;
    private readonly Case c;
    private readonly SlaClock clock = new(new BusinessCalendar(TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur"),
        Enum.GetValues<DayOfWeek>().Select(d => new BusinessHour { DayOfWeek = d, DayName = d.ToString(), IsEnabled = true, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(24) }),
        Array.Empty<(DateTime, string)>()), null);

    public CaseMeetingTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"meetings-{Guid.NewGuid()}").Options);
        User U(string n) => new() { Id = Guid.NewGuid(), Name = n, Email = n.Replace(' ', '.') + "@x.test", Role = "Agent", ExternalUserId = n };
        agent = U("Agent Ann"); other = U("Other Ola"); supervisor = U("Super Sam");
        team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", IsActive = true };
        var mailbox = new EmailMailbox { Id = Guid.NewGuid(), Name = "Support", Address = "support@bank.example", IsEnabled = true, PasswordEncrypted = "x" };
        c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-01300", Title = "Card blocked", TeamId = team.Id, OwnerId = agent.Id, Status = CaseStatus.InProgress,
            Severity = "High", ContactEmail = "komal@client.example", SlaStartTime = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        };
        db.AddRange(agent, other, supervisor, team, mailbox, c);
        db.Add(new EmailMailboxTeam { MailboxId = mailbox.Id, TeamId = team.Id });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private sealed class Caller(Guid id, params string[] permissions) : ICurrentUserAccessor
    {
        public Guid? LocalUserId => id;
        public HostPrincipal? Principal => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
        public bool Has(string p) => Permissions.Contains("*") || Permissions.Contains(p);
    }

    private sealed class FixedClock(SlaClock clock) : ISlaClockProvider
    {
        public Task<SlaClock> GetAsync(CancellationToken ct = default) => Task.FromResult(clock);
    }

    private CaseMeetingService As(User u, params string[] permissions) => new(db, new FixedClock(clock), new Caller(u.Id, permissions));

    /// <summary>Tomorrow (Kuala Lumpur), at the given local time.</summary>
    private static string Tomorrow => DateTime.UtcNow.AddHours(8).AddDays(1).ToString("yyyy-MM-dd");

    private static SaveCaseMeetingDto Form(string start = "10:00", string end = "10:30", string? date = null) => new()
    {
        Title = "Review of your documents", Date = date ?? Tomorrow, StartTime = start, EndTime = end,
        JoinUrl = "https://meet.example.com/abc-defg", AttendeeEmail = "Komal@Client.example", AttendeeName = "Komal", Agenda = "Go through the forms, then sign.",
    };

    private Task<CaseEmailMessage> LastEmail() => db.CaseEmailMessages.AsNoTracking().OrderByDescending(m => m.CreatedAt).FirstAsync();

    [Fact]
    public async Task Scheduling_saves_the_meeting_in_UTC_records_it_and_queues_an_invite()
    {
        var meeting = await As(agent).ScheduleAsync(c.Id, Form());

        var expectedStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(Tomorrow + "T10:00:00"), TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur"));
        Assert.Equal(expectedStart, meeting.StartUtc);
        Assert.Equal("komal@client.example", meeting.AttendeeEmail);
        Assert.Contains("10:00–10:30 (GMT+8)", meeting.When);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.MeetingId == meeting.Id && e.ActionType == "MEETING_SCHEDULED"));

        var email = await LastEmail();
        Assert.Equal("komal@client.example", email.ToAddress);
        Assert.Equal("Meeting: Review of your documents [Case C-01300]", email.Subject);
        Assert.Equal("REQUEST", email.CalendarMethod);
        Assert.Contains("METHOD:REQUEST", email.CalendarIcs);
        Assert.Contains($"DTSTART:{expectedStart:yyyyMMdd'T'HHmmss'Z'}", email.CalendarIcs);
        Assert.Contains("Join: https://meet.example.com/abc-defg", email.BodyText);
        Assert.Contains("Case reference: C-01300", email.BodyText);
    }

    [Fact]
    public async Task The_invite_email_carries_the_calendar_attachment()
    {
        await As(agent).ScheduleAsync(c.Id, Form());
        var email = await LastEmail();
        var mailbox = await db.EmailMailboxes.AsNoTracking().SingleAsync();

        var mime = CaseEmailSender.Build(email, mailbox);

        var invite = mime.Attachments.OfType<MimeKit.MimePart>().Single();
        Assert.Equal("text/calendar", invite.ContentType.MimeType);
        Assert.Equal("REQUEST", invite.ContentType.Parameters["method"]);
    }

    [Fact]
    public async Task Bad_times_links_and_addresses_are_refused_with_the_field_named()
    {
        var past = await Assert.ThrowsAsync<FieldValidationException>(() => As(agent).ScheduleAsync(c.Id, Form(date: DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd"))));
        var backwards = await Assert.ThrowsAsync<FieldValidationException>(() => As(agent).ScheduleAsync(c.Id, Form("11:00", "10:00")));
        var form = Form(); form.JoinUrl = "javascript:alert(1)"; form.AttendeeEmail = "not-an-email";
        var bad = await Assert.ThrowsAsync<FieldValidationException>(() => As(agent).ScheduleAsync(c.Id, form));

        Assert.Contains(past.Errors, e => e.Field == "startTime");
        Assert.Contains(backwards.Errors, e => e.Field == "endTime");
        Assert.Contains(bad.Errors, e => e.Field == "joinUrl");
        Assert.Contains(bad.Errors, e => e.Field == "attendeeEmail");
        Assert.Empty(db.CaseMeetings);
    }

    [Fact]
    public async Task The_same_meeting_twice_is_refused_not_sent_twice()
    {
        await As(agent).ScheduleAsync(c.Id, Form());

        await Assert.ThrowsAsync<FieldValidationException>(() => As(agent).ScheduleAsync(c.Id, Form()));
        Assert.Equal(1, await db.CaseEmailMessages.CountAsync());
    }

    [Fact]
    public async Task Moving_a_meeting_updates_the_same_calendar_entry()
    {
        var meeting = await As(agent).ScheduleAsync(c.Id, Form());
        var uid = (await db.CaseMeetings.SingleAsync()).IcsUid;

        var moved = await As(agent).RescheduleAsync(c.Id, meeting.Id, Form("14:00", "15:00"));

        Assert.Contains("14:00–15:00", moved.When);
        var email = await LastEmail();
        Assert.StartsWith("Updated:", email.Subject);
        Assert.Contains($"UID:{uid}", email.CalendarIcs);
        Assert.Contains("SEQUENCE:1", email.CalendarIcs);
    }

    [Fact]
    public async Task Cancelling_sends_a_cancellation_of_the_same_entry()
    {
        var meeting = await As(agent).ScheduleAsync(c.Id, Form());

        var cancelled = await As(agent).CancelAsync(c.Id, meeting.Id, new CancelCaseMeetingDto { Reason = "Customer asked to move it" });

        Assert.Equal("Cancelled", cancelled.Status);
        var email = await LastEmail();
        Assert.Equal("CANCEL", email.CalendarMethod);
        Assert.Contains("STATUS:CANCELLED", email.CalendarIcs);
        Assert.Contains("Reason: Customer asked to move it", email.BodyText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => As(agent).CancelAsync(c.Id, meeting.Id, new CancelCaseMeetingDto()));
    }

    [Fact]
    public async Task Only_the_organizer_the_holder_or_a_supervisor_may_change_it()
    {
        var meeting = await As(agent).ScheduleAsync(c.Id, Form());

        await Assert.ThrowsAsync<CaseLockedException>(() => As(other).RescheduleAsync(c.Id, meeting.Id, Form("12:00", "12:30")));
        var bySupervisor = await As(supervisor, Permissions.CasesSupervise).RescheduleAsync(c.Id, meeting.Id, Form("12:00", "12:30"));

        Assert.Contains("12:00–12:30", bySupervisor.When);
    }

    [Fact]
    public async Task The_form_starts_from_the_case_address_else_the_email_it_came_from_else_the_customer_record()
    {
        var fromCase = await As(agent).OptionsAsync(c.Id);

        var tracked = await db.Cases.SingleAsync(x => x.Id == c.Id);
        tracked.ContactEmail = null;
        db.InboundEmails.Add(new InboundEmail { MailboxId = (await db.EmailMailboxes.SingleAsync()).Id, CaseId = c.Id, FromAddress = "Sender@Client.example", Subject = "s", ReceivedAt = DateTime.UtcNow, MessageKey = "k" });
        db.SaveChanges();
        var fromEmail = await As(agent).OptionsAsync(c.Id);

        db.InboundEmails.RemoveRange(db.InboundEmails);
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Komal", PhoneNumber = "1", Email = "komal@record.example" };
        db.Add(customer);
        tracked.CustomerId = customer.Id;
        db.SaveChanges();
        var fromRecord = await As(agent).OptionsAsync(c.Id);

        customer.Email = null;
        db.SaveChanges();
        var none = await As(agent).OptionsAsync(c.Id);

        Assert.Equal(("komal@client.example", "case"), (fromCase.CustomerEmail, fromCase.CustomerEmailSource));
        Assert.Equal(("sender@client.example", "email"), (fromEmail.CustomerEmail, fromEmail.CustomerEmailSource));
        Assert.Equal(("komal@record.example", "customer"), (fromRecord.CustomerEmail, fromRecord.CustomerEmailSource));
        Assert.Null(none.CustomerEmail);
    }

    [Fact]
    public void Invite_text_is_escaped_and_folded_as_the_calendar_standard_requires()
    {
        Assert.Equal(@"a\, b\; c\nd".Replace(@"\\", @"\"), MeetingInvite.Escape("a, b; c\nd"));
        var folded = MeetingInvite.Fold("DESCRIPTION:" + new string('x', 200));
        Assert.All(folded.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75));
    }

    // ------------------------------------------------------------------------------------------------ choosing the mailbox

    /// <summary>A second mailbox the case's team works, so the invitation's mailbox must be chosen.</summary>
    private async Task<EmailMailbox> SecondTeamMailboxAsync()
    {
        var second = new EmailMailbox { Id = Guid.NewGuid(), Name = "Cards desk", Address = "cards@bank.example", IsEnabled = true, PasswordEncrypted = "x" };
        db.Add(second);
        db.Add(new EmailMailboxTeam { MailboxId = second.Id, TeamId = team.Id });
        await db.SaveChangesAsync();
        return second;
    }

    [Fact]
    public async Task With_several_team_mailboxes_the_form_offers_them_instead_of_refusing_the_invitation()
    {
        await SecondTeamMailboxAsync();

        var options = await As(agent).OptionsAsync(c.Id);

        Assert.True(options.CanEmail);
        Assert.Null(options.Reason);
        Assert.Equal(new[] { "cards@bank.example", "support@bank.example" }, options.Mailboxes!.Select(m => m.Address));
    }

    [Fact]
    public async Task The_invitation_goes_from_the_chosen_mailbox_and_its_update_from_the_same_one_without_asking_again()
    {
        var second = await SecondTeamMailboxAsync();
        var unchosen = await Assert.ThrowsAsync<FieldValidationException>(() => As(agent).ScheduleAsync(c.Id, Form()));
        var form = Form();
        form.MailboxId = second.Id;

        var meeting = await As(agent).ScheduleAsync(c.Id, form);
        var invite = await LastEmail();
        await As(agent).RescheduleAsync(c.Id, meeting.Id, Form("11:00", "11:30"));
        var update = await LastEmail();

        Assert.Contains(unchosen.Errors, e => e.Field == "mailboxId" && e.Message.StartsWith("Choose the mailbox"));
        Assert.Equal(second.Id, invite.MailboxId);
        Assert.Equal(second.Id, update.MailboxId);   // the customer's calendar sees the same organizer
    }

    [Fact]
    public async Task A_mailbox_the_cases_team_does_not_work_cannot_be_chosen()
    {
        await SecondTeamMailboxAsync();
        var elsewhere = new EmailMailbox { Id = Guid.NewGuid(), Name = "Loans", Address = "loans@bank.example", IsEnabled = true, PasswordEncrypted = "x" };
        db.Add(elsewhere);
        await db.SaveChangesAsync();
        var form = Form();
        form.MailboxId = elsewhere.Id;

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => As(agent).ScheduleAsync(c.Id, form));

        Assert.Contains(ex.Errors, e => e.Field == "mailboxId" && e.Message.Contains("this case's team works"));
    }
}
