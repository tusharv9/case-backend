using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Notifications;
using CaseManagement.Api.Services.Tickets;
using CaseManagement.Api.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// The ticket SLA: how long a ticket may wait before someone assigns it. It is the queue's own clock, kept apart from the
/// case's SLA, which now starts only when the case is made.
/// </summary>
/// <remarks>
/// Before this, a case made from a ticket inherited the ticket's waiting time. A High case (2 h) made from an email that sat
/// unassigned for 3 h reached its team already breached. The waiting belongs to whoever triages the queue, so it is measured
/// and reported here, in working time only, and its alert goes to the leads of the mailbox's teams exactly once.
/// </remarks>
public class TicketSlaTests : IDisposable
{
    // Monday 16 November 2026; Saturday 14 November is the weekend before.
    private static readonly DateTime Monday10 = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Saturday10 = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    private readonly AppDbContext db;
    private readonly RecordingNotificationSink sink = new();

    public TicketSlaTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"ticket-sla-{Guid.NewGuid()}").Options);
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    /// <summary>Monday to Friday, 09:00 to 17:00 UTC.</summary>
    private static BusinessCalendar OfficeHours() => new(
        TimeZoneInfo.Utc,
        Enum.GetValues<DayOfWeek>().Select(d => new BusinessHour
        {
            DayOfWeek = d, DayName = d.ToString(), IsEnabled = d is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            StartTime = TimeSpan.FromHours(9), EndTime = TimeSpan.FromHours(17),
        }),
        Array.Empty<(DateTime, string)>());

    private static TicketSlaClock Clock(decimal? approachingPercent = null) => new(OfficeHours(), approachingPercent);

    // ───────────── the clock ─────────────

    [Fact]
    public void A_ticket_inside_its_target_is_healthy_and_says_when_it_must_be_assigned()
    {
        var snapshot = Clock().Evaluate(Monday10, 30, stoppedAtUtc: null, nowUtc: Monday10.AddMinutes(10));

        Assert.Equal((TicketSlaStates.Healthy, true), (snapshot.State, snapshot.IsRunning));
        Assert.Equal((30, 10d, 20d), (snapshot.TargetMinutes, snapshot.ConsumedMinutes, snapshot.RemainingMinutes));
        Assert.Equal(Monday10.AddMinutes(30), snapshot.DueAt);
    }

    [Fact]
    public void A_waiting_ticket_that_reaches_its_target_reads_breached()
    {
        var atTarget = Clock().Evaluate(Monday10, 30, null, Monday10.AddMinutes(30));
        var past = Clock().Evaluate(Monday10, 30, null, Monday10.AddHours(2));

        Assert.Equal(TicketSlaStates.Breached, atTarget.State);
        Assert.Equal((TicketSlaStates.Breached, -90d), (past.State, past.RemainingMinutes));
    }

    [Fact]
    public void A_ticket_reads_approaching_from_the_configured_percent_and_never_without_one()
    {
        Assert.Equal(TicketSlaStates.Approaching, Clock(75).Evaluate(Monday10, 30, null, Monday10.AddMinutes(23)).State);
        Assert.Equal(TicketSlaStates.Healthy, Clock(75).Evaluate(Monday10, 30, null, Monday10.AddMinutes(20)).State);
        Assert.Equal(TicketSlaStates.Healthy, Clock(null).Evaluate(Monday10, 30, null, Monday10.AddMinutes(29)).State);
    }

    [Fact]
    public void Only_working_hours_count_so_a_weekend_email_is_not_overdue_early_on_monday()
    {
        var mondayMorning = Clock().Evaluate(Saturday10, 30, null, new DateTime(2026, 11, 16, 9, 20, 0, DateTimeKind.Utc));
        var sunday = Clock().Evaluate(Saturday10, 30, null, new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal((TicketSlaStates.Healthy, 20d, false), (mondayMorning.State, mondayMorning.ConsumedMinutes, mondayMorning.IsOutsideHours));
        Assert.Equal(new DateTime(2026, 11, 16, 9, 30, 0, DateTimeKind.Utc), mondayMorning.DueAt);
        Assert.Equal((0d, true), (sunday.ConsumedMinutes, sunday.IsOutsideHours));
    }

    [Fact]
    public void A_target_of_zero_means_the_ticket_has_no_assign_time()
    {
        var snapshot = Clock().Evaluate(Monday10, 0, null, Monday10.AddDays(3));

        Assert.Equal((TicketSlaStates.None, (DateTime?)null), (snapshot.State, snapshot.DueAt));
        Assert.Null(Clock().DueAt(Monday10, 0));
    }

    [Fact]
    public void Stopping_records_the_working_minutes_waited_and_whether_the_target_was_met()
    {
        var late = new Ticket { ReceivedAt = Monday10 };
        var quick = new Ticket { ReceivedAt = Monday10 };
        var noTarget = new Ticket { ReceivedAt = Monday10 };

        Clock().Stop(late, 30, Monday10.AddMinutes(50));
        Clock().Stop(quick, 30, Monday10.AddMinutes(20));
        Clock().Stop(noTarget, 0, Monday10.AddMinutes(50));

        Assert.Equal((50d, "Breached", 30), (late.AssignedWithinMinutes, late.SlaOutcome, late.AssignTargetMinutes));
        Assert.Equal((20d, "Met"), (quick.AssignedWithinMinutes, quick.SlaOutcome));
        Assert.Equal((50d, (string?)null), (noTarget.AssignedWithinMinutes, noTarget.SlaOutcome));
        var handled = Clock().Evaluate(Monday10, 30, Monday10.AddMinutes(50), Monday10.AddDays(1));
        Assert.Equal((TicketSlaStates.Breached, false, (DateTime?)null), (handled.State, handled.IsRunning, handled.DueAt));
        Assert.Equal(TicketSlaStates.Met, Clock().Evaluate(Monday10, 30, Monday10.AddMinutes(20), Monday10.AddDays(1)).State);
    }

    // ───────────── where the target comes from ─────────────

    [Fact]
    public async Task The_target_comes_from_the_tickets_priority_whatever_its_spelling_and_a_snapshot_wins()
    {
        db.PrioritySlaRules.Add(new PrioritySlaRule { Priority = "High", TicketAssignValue = 15, TicketAssignUnit = "Minutes", TicketAssignMinutes = 15, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var targets = await TicketSlaTargets.LoadAsync(db, default);

        Assert.Equal((15, 15), (targets.For("high"), targets.For(" High ")));
        Assert.Equal(5, targets.Of(new Ticket { Priority = "High", AssignTargetMinutes = 5 }));
        Assert.Equal(15, targets.Of(new Ticket { Priority = "High" }));
    }

    [Fact]
    public async Task A_ticket_without_a_known_priority_takes_the_configured_default_or_thirty_minutes()
    {
        var unset = await TicketSlaTargets.LoadAsync(db, default);
        Assert.Equal((30, 30), (unset.For(null), unset.For("Unknown")));

        db.BusinessCalendarSettings.Add(new BusinessCalendarSetting { TimeZoneId = "UTC", TicketDefaultAssignValue = 45, TicketDefaultAssignMinutes = 45, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        Assert.Equal(45, (await TicketSlaTargets.LoadAsync(db, default)).For(null));
    }

    // ───────────── the monitor ─────────────

    private sealed record Queue(User Lead, EmailMailbox Mailbox);

    private async Task<Queue> QueueLedBySomeone()
    {
        var lead = new User { Name = "Lina Lead", Email = "lina@rma.test", CreatedAt = DateTime.UtcNow };
        var team = new Team { Name = "Support", OwnerId = lead.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
        var mailbox = new EmailMailbox { Name = "Support", Address = "support@rma.test", ImapHost = "i", SmtpHost = "s", CreatedAt = DateTime.UtcNow };
        mailbox.Teams.Add(new EmailMailboxTeam { MailboxId = mailbox.Id, TeamId = team.Id });
        db.Users.Add(lead);
        db.Teams.Add(team);
        db.EmailMailboxes.Add(mailbox);
        await db.SaveChangesAsync();
        return new Queue(lead, mailbox);
    }

    private async Task<Ticket> TicketIn(EmailMailbox mailbox, DateTime receivedAt, int? target, string status = TicketStatuses.New, string? priority = null)
    {
        var ticket = new Ticket
        {
            MailboxId = mailbox.Id, RequesterName = "Ravi", RequesterEmail = "ravi@gmail.com", Subject = "Refund", Description = "Charged twice.",
            ReceivedAt = receivedAt, Status = status, Priority = priority, AssignTargetMinutes = target, CreatedAt = DateTime.UtcNow,
        };
        await CaseManagement.Api.Services.EmailIntake.TicketNumbers.AddAsync(db, ticket, default);
        db.ChangeTracker.Clear();
        return ticket;
    }

    private TicketSlaMonitor Monitor() => new(db, sink, NullLogger<TicketSlaMonitor>.Instance);

    [Fact]
    public async Task A_late_ticket_is_reported_once_to_the_leads_of_its_mailbox_teams_without_changing_its_version()
    {
        var queue = await QueueLedBySomeone();
        var received = DateTime.UtcNow.AddHours(-2);
        var ticket = await TicketIn(queue.Mailbox, received, 30);

        await Monitor().RunAsync(TicketSlaClock.RoundTheClock, DateTime.UtcNow, default);
        await Monitor().RunAsync(TicketSlaClock.RoundTheClock, DateTime.UtcNow, default);

        Assert.Equal([(CaseNotificationKinds.TicketSlaBreached, queue.Lead.Id)], sink.Sent.ToArray());
        var stored = await db.Tickets.AsNoTracking().SingleAsync();
        Assert.True(stored.SlaBreachNotified);
        Assert.Equal(received.AddMinutes(30), stored.SlaBreachedAt);
        Assert.Equal(ticket.Version, stored.Version);   // an assigner's open drawer is not invalidated by the monitor
    }

    [Fact]
    public async Task A_late_ticket_in_a_mailbox_no_team_leads_alerts_nobody_and_does_not_fail()
    {
        var mailbox = new EmailMailbox { Name = "General", Address = "hello@rma.test", ImapHost = "i", SmtpHost = "s", CreatedAt = DateTime.UtcNow };
        db.EmailMailboxes.Add(mailbox);
        await db.SaveChangesAsync();
        await TicketIn(mailbox, DateTime.UtcNow.AddHours(-2), 30);

        await Monitor().RunAsync(TicketSlaClock.RoundTheClock, DateTime.UtcNow, default);

        Assert.Empty(sink.Sent);
        Assert.True((await db.Tickets.AsNoTracking().SingleAsync()).SlaBreachNotified);
    }

    [Fact]
    public async Task An_older_ticket_without_a_target_gets_its_prioritys_target_and_due_time()
    {
        var queue = await QueueLedBySomeone();
        db.PrioritySlaRules.Add(new PrioritySlaRule { Priority = "High", TicketAssignValue = 15, TicketAssignMinutes = 15, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var received = DateTime.UtcNow.AddMinutes(-5);
        await TicketIn(queue.Mailbox, received, target: null, priority: "High");

        await Monitor().RunAsync(TicketSlaClock.RoundTheClock, DateTime.UtcNow, default);

        var stored = await db.Tickets.AsNoTracking().SingleAsync();
        Assert.Equal((15, received.AddMinutes(15), false), (stored.AssignTargetMinutes, stored.AssignDueAt, stored.SlaBreachNotified));
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public async Task Handled_tickets_and_tickets_without_a_target_are_never_reported()
    {
        var queue = await QueueLedBySomeone();
        var longAgo = DateTime.UtcNow.AddDays(-2);
        await TicketIn(queue.Mailbox, longAgo, 30, TicketStatuses.Converted);
        await TicketIn(queue.Mailbox, longAgo, 30, TicketStatuses.Closed);
        await TicketIn(queue.Mailbox, longAgo, 0);

        await Monitor().RunAsync(TicketSlaClock.RoundTheClock, DateTime.UtcNow, default);

        Assert.Empty(sink.Sent);
    }

    // ───────────── configuration ─────────────

    private static UpdateSlaRoutingConfigRequestDto RequestWithTicketTarget(int? value, string? unit) => new()
    {
        PriorityRules =
        [
            new PrioritySlaRuleDto
            {
                Priority = "High", FirstResponseValue = 30, FirstResponseUnit = "Minutes", InternalResolutionValue = 2, InternalResolutionUnit = "Hours",
                ExternalResolutionValue = 4, ExternalResolutionUnit = "Hours", TicketAssignValue = value, TicketAssignUnit = unit,
            },
        ],
    };

    private static string[] TicketErrors(UpdateSlaRoutingConfigRequestDto request) =>
        new UpdateSlaRoutingConfigRequestDtoValidator().Validate(request).Errors
            .Where(e => e.PropertyName.Contains("Ticket")).Select(e => e.ErrorMessage).ToArray();

    [Fact]
    public void An_assign_within_target_may_be_zero_or_left_out_but_never_negative_longer_than_a_week_or_in_another_unit()
    {
        Assert.Empty(TicketErrors(RequestWithTicketTarget(0, "Minutes")));
        Assert.Empty(TicketErrors(RequestWithTicketTarget(null, null)));
        Assert.Empty(TicketErrors(RequestWithTicketTarget(168, "Hours")));
        Assert.NotEmpty(TicketErrors(RequestWithTicketTarget(-1, "Minutes")));
        Assert.NotEmpty(TicketErrors(RequestWithTicketTarget(169, "Hours")));
        Assert.NotEmpty(TicketErrors(RequestWithTicketTarget(10, "Days")));

        var badDefault = RequestWithTicketTarget(null, null);
        badDefault.TicketDefaultAssignValue = -5;
        badDefault.TicketDefaultAssignUnit = "Minutes";
        Assert.NotEmpty(TicketErrors(badDefault));
    }
}
