using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Calendar;
using CaseManagement.Api.Services.Statuses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>The Calendar: dated events of the cases a person may see, with SLA deadlines from the SLA engine itself.</summary>
/// <remarks>
/// <para>The calendar stores nothing; if it worked deadlines out on its own they would drift from the case drawer. A deadline is
/// the SLA engine's (pauses included), a paused case has none, and an agent sees only the events of their own and their
/// collaboration cases — the same rule as the case list.</para>
/// </remarks>
public class CaseCalendarTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Team team;
    private readonly User ann, tom;
    private readonly SlaClock clock = new(BusinessCalendar.RoundTheClock, null);
    private readonly DateTime now = DateTime.UtcNow;

    public CaseCalendarTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"calendar-{Guid.NewGuid()}").Options);
        ann = new User { Id = Guid.NewGuid(), Name = "Agent Ann", Email = "ann@x.test", Role = "Agent", ExternalUserId = "ann" };
        tom = new User { Id = Guid.NewGuid(), Name = "Team Tom", Email = "tom@x.test", Role = "Agent", ExternalUserId = "tom" };
        team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", IsActive = true };
        db.AddRange(ann, tom, team);
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

    private CaseCalendarService As(User u, params string[] permissions)
    {
        var caller = new Caller(u.Id, permissions);
        return new CaseCalendarService(db, new FixedClock(clock), new CaseAccessScope(db, caller), caller,
            new CaseStatusCatalog(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions()))));
    }

    private Case AddCase(User owner, DateTime start, int targetMinutes, DateTime? pausedAt = null)
    {
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..5], Title = "t", TeamId = team.Id, OwnerId = owner.Id,
            Status = pausedAt != null ? CaseStatus.WaitingOnCustomer : CaseStatus.InProgress, Severity = "High", SlaStartTime = start,
            InternalResolutionTargetMinutes = targetMinutes, ExternalResolutionTargetMinutes = targetMinutes * 2, CreatedAt = start, SlaPausedAt = pausedAt,
            FirstResponseActualAt = start, FirstResponseStatus = "Met",
        };
        db.Cases.Add(c);
        db.SaveChanges();
        return c;
    }

    private CalendarQuery Range(params string[] types) => new(now.AddDays(-3), now.AddDays(10), types, null, null, null, null, false);

    [Fact]
    public async Task The_resolution_deadline_is_the_SLA_engines_own()
    {
        var c = AddCase(ann, now.AddHours(-2), 600);

        var result = await As(ann, Permissions.CasesRead).GetAsync(Range(CalendarEventTypes.SlaDue));

        var due = Assert.Single(result.Events);
        Assert.Equal(c.Id, due.CaseId);
        var engine = clock.Evaluate(new SlaInputs { Status = c.Status, StartUtc = c.SlaStartTime, InternalTargetMinutes = 600, ExternalTargetMinutes = 1200 }, now);
        Assert.Equal(engine.Internal.DueAt!.Value, due.Start, TimeSpan.FromSeconds(1));
        Assert.False(due.IsBreached);
    }

    [Fact]
    public async Task A_paused_case_has_no_deadline_and_is_counted_as_paused()
    {
        AddCase(ann, now.AddHours(-2), 600, pausedAt: now.AddHours(-1));

        var result = await As(ann, Permissions.CasesRead).GetAsync(Range(CalendarEventTypes.SlaDue));

        Assert.Empty(result.Events);
        Assert.Equal(1, result.PausedCases);
    }

    [Fact]
    public async Task A_passed_deadline_is_one_breach_event_never_also_a_due_one()
    {
        AddCase(ann, now.AddHours(-5), 60);

        var all = (await As(ann, Permissions.CasesRead).GetAsync(Range(CalendarEventTypes.SlaDue, CalendarEventTypes.Breach))).Events;

        var breach = Assert.Single(all);
        Assert.Equal(CalendarEventTypes.Breach, breach.Type);
        Assert.True(breach.IsBreached);
        Assert.EndsWith("SLA breached", breach.Title);
    }

    [Fact]
    public async Task Escalations_follow_ups_resolutions_and_reminders_come_from_the_cases_own_history()
    {
        var c = AddCase(ann, now.AddHours(-2), 600);
        db.CaseEvents.AddRange(
            new CaseEvent { CaseId = c.Id, EventType = EventType.Escalate, ActionType = "AUTO_ESCALATION", NewValue = "Level 1", Message = "[Automatic Escalation] Reached Level 1.", CreatedAt = now.AddHours(-1) },
            new CaseEvent { CaseId = c.Id, EventType = EventType.Comment, ActionType = "EMAIL_REPLY_RECEIVED", Message = "Email received from k@x.test: Re: help", CreatedAt = now.AddMinutes(-50) },
            new CaseEvent { CaseId = c.Id, EventType = EventType.Resolve, Message = "Case resolved: fixed.", CreatedAt = now.AddMinutes(-40) },
            new CaseEvent { CaseId = c.Id, EventType = EventType.Note, Message = "an ordinary note", CreatedAt = now.AddMinutes(-30) });
        db.CaseSlaAlerts.Add(new CaseSlaAlert { CaseId = c.Id, CycleStart = now.AddHours(-2), NotifiedAt = now.AddHours(-1), Reminders = 2, LastReminderAt = now.AddMinutes(-20) });
        db.SaveChanges();

        var events = (await As(ann, Permissions.CasesRead).GetAsync(Range(CalendarEventTypes.Breach, CalendarEventTypes.FollowUp,
            CalendarEventTypes.Resolved, CalendarEventTypes.Reminder))).Events;

        Assert.Contains(events, e => e.Type == CalendarEventTypes.Breach && e.Title.Contains("escalated (Level 1)"));
        Assert.Contains(events, e => e.Type == CalendarEventTypes.FollowUp);
        Assert.Contains(events, e => e.Type == CalendarEventTypes.Resolved);
        Assert.Contains(events, e => e.Type == CalendarEventTypes.Reminder && e.Detail!.StartsWith("Reminder 2"));
        Assert.Equal(4, events.Count);   // the ordinary note is not an event
        Assert.Equal(events.Count, events.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task An_agent_sees_only_their_own_cases_events()
    {
        AddCase(ann, now.AddHours(-1), 600);
        AddCase(tom, now.AddHours(-1), 600);

        var annSees = await As(ann, Permissions.CasesRead).GetAsync(Range());
        var supervisorSees = await As(tom, Permissions.CasesRead, Permissions.CasesViewAll).GetAsync(Range(CalendarEventTypes.Created));

        Assert.All(annSees.Events, e => Assert.Equal(ann.Id, e.OwnerId));
        Assert.Equal(2, supervisorSees.Events.Count);
        Assert.False(annSees.CanSeeOthers);
        Assert.True(supervisorSees.CanSeeOthers);
    }

    [Fact]
    public async Task Meetings_show_with_their_link_and_cancelled_ones_without_it()
    {
        var c = AddCase(ann, now.AddHours(-1), 600);
        db.CaseMeetings.AddRange(
            new CaseMeeting { CaseId = c.Id, Title = "Review", StartUtc = now.AddDays(1), EndUtc = now.AddDays(1).AddMinutes(30), JoinUrl = "https://meet.example/x", OrganizerUserId = ann.Id, AttendeeEmail = "k@x.test", IcsUid = "a" },
            new CaseMeeting { CaseId = c.Id, Title = "Old", StartUtc = now.AddDays(2), EndUtc = now.AddDays(2).AddMinutes(30), JoinUrl = "https://meet.example/y", OrganizerUserId = ann.Id, AttendeeEmail = "k@x.test", IcsUid = "b", Status = CaseMeetingStatuses.Cancelled });
        db.SaveChanges();

        var events = (await As(ann, Permissions.CasesRead).GetAsync(Range(CalendarEventTypes.Meeting))).Events;

        Assert.Equal(2, events.Count);
        Assert.Equal("https://meet.example/x", events.Single(e => e.Title == "Review").JoinUrl);
        Assert.Null(events.Single(e => e.Title == "Old").JoinUrl);
        Assert.Equal("Agent Ann", events[0].OrganizerName);
    }

    [Fact]
    public async Task More_than_two_months_at_once_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => As(ann, Permissions.CasesRead).GetAsync(new CalendarQuery(now, now.AddDays(90), null, null, null, null, null, false)));
    }
}
