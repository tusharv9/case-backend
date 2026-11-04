using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.HostIntegration;
using Microsoft.Extensions.Options;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// The SLA engine: business-time arithmetic, the one SLA clock (pause/resume/stop), escalation triggers and targets,
/// and the monitor that applies them. Pure tests need no database; the monitor tests run on real PostgreSQL.
/// </summary>
public class BusinessCalendarTests
{
    // Mon–Fri 09:00–17:00 in Kuala Lumpur (UTC+8, no DST); Sat/Sun closed. 2026-10-05 is a Monday.
    internal static BusinessCalendar Office(params (DateTime Date, string Name)[] holidays)
    {
        var hours = Enum.GetValues<DayOfWeek>().Select(d => new BusinessHour
        {
            DayOfWeek = d,
            IsEnabled = d != DayOfWeek.Saturday && d != DayOfWeek.Sunday,
            StartTime = TimeSpan.FromHours(9),
            EndTime = TimeSpan.FromHours(17),
        });
        return new BusinessCalendar(TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur"), hours, holidays);
    }

    /// <summary>A local Kuala Lumpur wall-clock time (October 2026) expressed as UTC.</summary>
    internal static DateTime Kl(int day, int hour, int minute = 0) =>
        DateTime.SpecifyKind(new DateTime(2026, 10, day, hour, minute, 0).AddHours(-8), DateTimeKind.Utc);

    [Fact]
    public void AddBusinessMinutes_StaysInsideTheWorkingDay()
    {
        var due = Office().AddBusinessMinutes(Kl(5, 10), 120);
        Assert.Equal(Kl(5, 12), due);
    }

    [Fact]
    public void AddBusinessMinutes_RollsOverTheEveningAndTheWeekend()
    {
        // Friday 16:00 + 120 min: one hour left on Friday, the rest on Monday morning.
        var due = Office().AddBusinessMinutes(Kl(9, 16), 120);
        Assert.Equal(Kl(12, 10), due);
    }

    [Fact]
    public void AddBusinessMinutes_StartsTheClockAtOpeningWhenCreatedOutOfHours()
    {
        Assert.Equal(Kl(6, 10), Office().AddBusinessMinutes(Kl(5, 20), 60));   // after closing -> next morning
        Assert.Equal(Kl(5, 10), Office().AddBusinessMinutes(Kl(5, 6), 60));    // before opening -> opening time
        Assert.Equal(Kl(12, 10), Office().AddBusinessMinutes(Kl(10, 12), 60)); // Saturday -> Monday
    }

    [Fact]
    public void Holidays_AreSkipped()
    {
        var cal = Office((new DateTime(2026, 10, 6), "Test Holiday"));   // Tuesday
        Assert.Equal(Kl(7, 10), cal.AddBusinessMinutes(Kl(5, 16), 120));  // Mon 16:00 + 2h -> Wed 10:00
        Assert.Equal("Test Holiday", cal.HolidayOn(Kl(6, 12)));
        Assert.Null(cal.HolidayOn(Kl(7, 12)));
    }

    [Fact]
    public void Elapsed_IsTheInverseOfAdd()
    {
        var cal = Office();
        foreach (var (start, minutes) in new[] { (Kl(5, 10), 45), (Kl(9, 16), 600), (Kl(10, 12), 30), (Kl(5, 8), 480) })
        {
            var due = cal.AddBusinessMinutes(start, minutes);
            Assert.Equal(minutes, cal.ElapsedBusinessMinutes(start, due), 3);
        }
    }

    [Fact]
    public void Elapsed_CountsOnlyWorkingTime()
    {
        Assert.Equal(0, Office().ElapsedBusinessMinutes(Kl(10, 10), Kl(11, 18)));      // a closed weekend
        Assert.Equal(480, Office().ElapsedBusinessMinutes(Kl(5, 9), Kl(5, 23)));        // a whole day
    }

    [Fact]
    public void IsWorkingTime_RespectsTheConfiguredTimeZone()
    {
        var cal = Office();
        Assert.True(cal.IsWorkingTime(Kl(5, 9)));
        Assert.False(cal.IsWorkingTime(Kl(5, 17)));          // closing time is exclusive
        Assert.False(cal.IsWorkingTime(Kl(10, 12)));         // Saturday
        // The same schedule in another zone is a different set of UTC moments.
        var london = new BusinessCalendar(TimeZoneInfo.FindSystemTimeZoneById("Europe/London"),
            Enum.GetValues<DayOfWeek>().Select(d => new BusinessHour { DayOfWeek = d, IsEnabled = true, StartTime = TimeSpan.FromHours(9), EndTime = TimeSpan.FromHours(17) }),
            Array.Empty<(DateTime, string)>());
        Assert.True(london.IsWorkingTime(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc).AddHours(-1)));  // 09:00 BST = 08:00 UTC
    }

    [Fact]
    public void ACalendarWithNoWorkingTime_IsRejected()
    {
        var closed = Enum.GetValues<DayOfWeek>().Select(d => new BusinessHour { DayOfWeek = d, IsEnabled = false });
        Assert.Throws<InvalidOperationException>(() => new BusinessCalendar(TimeZoneInfo.Utc, closed, Array.Empty<(DateTime, string)>()));
    }

    [Fact]
    public void ACalendarMissingADay_IsRejected()
    {
        var sixDays = Enum.GetValues<DayOfWeek>().Skip(1).Select(d => new BusinessHour { DayOfWeek = d, IsEnabled = true, StartTime = TimeSpan.FromHours(9), EndTime = TimeSpan.FromHours(17) });
        Assert.Throws<InvalidOperationException>(() => new BusinessCalendar(TimeZoneInfo.Utc, sixDays, Array.Empty<(DateTime, string)>()));
    }
}

public class SlaClockTests
{
    private static SlaClock Clock(decimal? approaching = 70) => new(BusinessCalendarTests.Office(), approaching);
    private static DateTime Kl(int day, int hour, int minute = 0) => BusinessCalendarTests.Kl(day, hour, minute);

    private static PrioritySlaRule Rule(int fr = 30, int internalMin = 120, int external = 240) => new()
    {
        Priority = "High", FirstResponseMinutes = fr, InternalResolutionMinutes = internalMin, ExternalResolutionMinutes = external, Version = 3,
    };

    private static Case NewCase(SlaClock clock, DateTime start)
    {
        var c = new Case { Status = CaseStatus.Open };
        clock.Start(c, Rule(), start);
        return c;
    }

    [Fact]
    public void Start_SnapshotsTheRuleAndComputesBusinessDueDates()
    {
        var clock = Clock();
        var c = NewCase(clock, Kl(9, 16));    // Friday 16:00

        Assert.Equal(120, c.InternalResolutionTargetMinutes);
        Assert.Equal(240, c.ExternalResolutionTargetMinutes);
        Assert.Equal(30, c.FirstResponseTargetMinutes);
        Assert.Equal(4, c.SlaTargetHours);
        Assert.Equal(3, c.SlaConfigVersion);
        Assert.Equal(Kl(12, 10), c.InternalResolutionDueAt);   // 1h Friday + 1h Monday
        Assert.Equal(Kl(12, 12), c.ExternalResolutionDueAt);
    }

    [Fact]
    public void Evaluate_ReportsConsumptionInBusinessMinutes()
    {
        var clock = Clock();
        var c = NewCase(clock, Kl(5, 9));

        var s = clock.Evaluate(SlaInputs.From(c), Kl(5, 10));   // 60 of 120 internal, 60 of 240 external
        Assert.Equal(50, s.Internal.ConsumedPercent);
        Assert.Equal(25, s.External.ConsumedPercent);
        Assert.Equal(60, s.Internal.RemainingMinutes);
        Assert.Equal(SlaHealth.Healthy, s.Health);
        Assert.True(s.IsClockRunning);
    }

    [Fact]
    public void TheClockDoesNotRunOvernightOrAtWeekends()
    {
        var clock = Clock();
        var c = NewCase(clock, Kl(9, 16));                       // Friday 16:00, 1h consumed by closing

        var monday = clock.Evaluate(SlaInputs.From(c), Kl(12, 9));
        Assert.Equal(60, monday.Internal.ConsumedMinutes);       // nothing counted over the weekend
        Assert.False(clock.Evaluate(SlaInputs.From(c), Kl(10, 12)).IsClockRunning);
    }

    [Fact]
    public void Health_MovesFromHealthyToApproachingToBreached()
    {
        var clock = Clock(70);
        var c = NewCase(clock, Kl(5, 9));

        Assert.Equal(SlaHealth.Healthy, clock.Evaluate(SlaInputs.From(c), Kl(5, 9, 30)).Health);
        // Past the INTERNAL target but within the external one: approaching, not yet breached.
        Assert.Equal(SlaHealth.Approaching, clock.Evaluate(SlaInputs.From(c), Kl(5, 11, 30)).Health);
        Assert.Equal(SlaHealth.Breached, clock.Evaluate(SlaInputs.From(c), Kl(5, 13, 30)).Health);
    }

    [Fact]
    public void Pause_FreezesTheClockAndResumePostponesEveryDueDate()
    {
        var clock = Clock();
        var c = NewCase(clock, Kl(5, 10));                       // Monday 10:00; internal due 12:00

        clock.Pause(c, Kl(5, 11));                               // 60 internal minutes used
        var paused = clock.Evaluate(SlaInputs.From(c), Kl(8, 9));  // days later: still 60 consumed
        Assert.True(paused.IsPaused);
        Assert.Equal(SlaHealth.Paused, paused.Health);
        Assert.Equal(60, paused.Internal.ConsumedMinutes);
        Assert.False(paused.Internal.IsBreached);

        clock.Resume(c, Kl(6, 9));                               // Tuesday 09:00: paused through Mon 11:00-17:00 = 360 business minutes
        Assert.Equal(360, c.SlaTotalPausedMinutes);
        Assert.Null(c.SlaPausedAt);
        Assert.Equal(Kl(6, 10), c.InternalResolutionDueAt);      // the 60 minutes still owed start counting Tuesday 09:00

        var resumed = clock.Evaluate(SlaInputs.From(c), Kl(6, 9, 30));
        Assert.Equal(90, resumed.Internal.ConsumedMinutes);
        Assert.Equal(SlaHealth.Healthy, resumed.Health);
    }

    [Fact]
    public void Resolve_StopsTheClockAndTheVerdictIsPermanent()
    {
        var clock = Clock();
        var onTime = NewCase(clock, Kl(5, 9));
        onTime.Status = CaseStatus.Resolved; onTime.ResolvedAt = Kl(5, 12); clock.Stop(onTime, Kl(5, 12));
        Assert.Equal(SlaHealth.Met, clock.Evaluate(SlaInputs.From(onTime), Kl(19, 12)).Health);   // weeks later, still Met

        var late = NewCase(clock, Kl(5, 9));
        late.Status = CaseStatus.Resolved; late.ResolvedAt = Kl(5, 15); clock.Stop(late, Kl(5, 15));  // 6h > 4h external
        Assert.Equal(SlaHealth.Breached, clock.Evaluate(SlaInputs.From(late), Kl(19, 12)).Health);
    }

    [Fact]
    public void Resolving_AWaitingCase_EndsItsPause()
    {
        var clock = Clock();
        var c = NewCase(clock, Kl(5, 9));
        clock.Pause(c, Kl(5, 10));
        c.Status = CaseStatus.Resolved; c.ResolvedAt = Kl(5, 13);
        clock.Stop(c, Kl(5, 13));
        Assert.Null(c.SlaPausedAt);
        Assert.Equal(180, c.SlaTotalPausedMinutes);
    }

    [Fact]
    public void FirstResponse_IsJudgedInBusinessTime()
    {
        var clock = Clock();
        var met = NewCase(clock, Kl(5, 9));
        clock.RecordFirstResponse(met, Kl(5, 9, 20));
        Assert.Equal("Met", met.FirstResponseStatus);

        var missed = NewCase(clock, Kl(5, 9));
        clock.RecordFirstResponse(missed, Kl(5, 10));
        Assert.Equal("Breached", missed.FirstResponseStatus);

        // Opened Friday after hours: the 30 minutes start Monday morning, so a 09:20 answer is on time.
        var overnight = NewCase(clock, Kl(9, 20));
        clock.RecordFirstResponse(overnight, Kl(12, 9, 20));
        Assert.Equal("Met", overnight.FirstResponseStatus);

        // The first answer is the one that counts.
        clock.RecordFirstResponse(met, Kl(7, 9));
        Assert.Equal("Met", met.FirstResponseStatus);
    }

    [Fact]
    public void ApplyRule_ChangesTargetsButKeepsTheStartAndThePausedTime()
    {
        var clock = Clock();
        var c = NewCase(clock, Kl(5, 9));
        c.SlaTotalPausedMinutes = 30;
        var start = c.SlaStartTime;

        clock.ApplyRule(c, Rule(10, 60, 90));

        Assert.Equal(start, c.SlaStartTime);
        Assert.Equal(60, c.InternalResolutionTargetMinutes);
        Assert.Equal(30, c.SlaTotalPausedMinutes);
        Assert.Equal(Kl(5, 10, 30), c.InternalResolutionDueAt);   // 60 target + 30 paused after 09:00
    }
}

/// <summary>Automatic escalation runs on SLA consumption only, between 1 and 100%.</summary>
public class EscalationTriggerTests
{
    [Theory]
    [InlineData("SlaPercentage", 70, true)]
    [InlineData("SlaPercentage", 100, true)]
    [InlineData("SlaPercentage", null, false)]      // a threshold is required
    [InlineData("SlaPercentage", 0, false)]
    [InlineData("SlaPercentage", 101, false)]       // more than all of the SLA is not a consumption threshold
    [InlineData("SlaBreached", null, false)]        // older trigger kinds are no longer executed
    [InlineData("SlaPostBreachHours", 12, false)]
    [InlineData("ManualOnly", null, false)]
    public void Only_an_SLA_consumption_percentage_is_a_trigger(string type, int? value, bool valid)
    {
        Assert.Equal(valid, EscalationTriggers.Validate(type, value) == null);
    }

    [Fact]
    public void The_description_is_derived_from_the_threshold()
    {
        Assert.Equal("SLA consumption reaches 90%", EscalationTriggers.Describe("SlaPercentage", 90m));
        Assert.Equal(string.Empty, EscalationTriggers.Describe("SlaBreached", null));
    }
}

/// <summary>
/// What makes a level usable, and what makes a matrix savable. An incomplete level must never run, a role may sit on only
/// one level, and thresholds must rise with the level — otherwise a later level could fire before an earlier one.
/// </summary>
public class EscalationLevelRulesTests
{
    private static EscalationLevelRules.Input L(int n, string? role, decimal? pct, string? why = "Needs attention", bool notify = true) =>
        new(n, role, "SlaPercentage", pct, why, notify, false, false, "High");

    [Fact]
    public void An_incomplete_level_names_every_missing_field()
    {
        var missing = EscalationLevelRules.MissingFields(null, "SlaPercentage", null, " ", false, false, false, "High");

        Assert.Equal(new[] { EscalationLevelRules.RoleField, EscalationLevelRules.ThresholdField, EscalationLevelRules.DescriptionField, EscalationLevelRules.NotificationField }, missing);
    }

    [Fact]
    public void A_complete_matrix_with_rising_thresholds_and_distinct_roles_is_accepted()
    {
        var errors = EscalationLevelRules.Validate(new[] { L(1, "Team Lead", 50), L(2, "Supervisor", 75), L(3, "Manager", 90) },
            new[] { "Team Lead", "Supervisor", "Manager" });

        Assert.Empty(errors);
    }

    [Fact]
    public void A_role_used_on_two_levels_is_refused()
    {
        var errors = EscalationLevelRules.Validate(new[] { L(1, "Team Lead", 50), L(2, "team lead", 75) }, null);

        Assert.Contains(errors, e => e.Field == "Level 2" && e.Message.Contains("already assigned to Level 1"));
    }

    [Fact]
    public void Thresholds_must_rise_with_the_level()
    {
        var errors = EscalationLevelRules.Validate(new[] { L(1, "Team Lead", 75), L(2, "Supervisor", 75) }, null);

        Assert.Contains(errors, e => e.Field == "Level 2" && e.Message.Contains("must be higher than Level 1"));
    }

    [Fact]
    public void A_new_level_is_refused_until_it_is_complete()
    {
        var errors = EscalationLevelRules.Validate(new[] { L(1, "Team Lead", 50), L(2, null, null, why: null, notify: false) }, null);

        Assert.Contains(errors, e => e.Field == "Level 2" && e.Message.StartsWith("Level 2 is incomplete"));
    }

    [Fact]
    public void A_role_without_Case_Management_access_is_refused_unless_the_level_already_had_it()
    {
        var allowed = new[] { "Team Lead" };

        var fresh = EscalationLevelRules.Validate(new[] { L(1, "Team Lead", 50), L(2, "Teller", 75) }, allowed);
        var kept = EscalationLevelRules.Validate(new[] { L(1, "Team Lead", 50), L(2, "Teller", 80) }, allowed,
            new Dictionary<int, string> { [2] = "Teller" });

        Assert.Contains(fresh, e => e.Field == "Level 2" && e.Message.Contains("does not have access to Case Management"));
        Assert.Empty(kept);
    }
}

public class EscalationDecisionTests
{
    private static EscalationLevelConfig Level(int n, decimal value) => new()
    {
        LevelNumber = n, Name = $"Level {n}", TriggerType = "SlaPercentage", TriggerValue = value, IsActive = true, TargetRole = "Role" + n,
        ActionDescription = "Why", NotifyRoleHolders = true, NotificationPriority = "High",
    };

    private static EscalationDecision? Decide(Case c, SlaSnapshot s, params EscalationLevelConfig[] levels) =>
        new EscalationService(null!, null!).FindDueLevel(c, s, new EscalationPolicy(levels), DateTime.UtcNow);

    private static SlaSnapshot Snapshot(double internalPct = 0, bool paused = false, bool stopped = false)
    {
        SlaTargetState T(double pct, bool b) => new(100, pct, 100 - pct, pct, null, b);
        return new SlaSnapshot(SlaHealth.Healthy, paused, stopped, !paused, T(internalPct, internalPct >= 100), T(internalPct / 2, false),
            T(0, false), "Pending", DateTime.UtcNow);
    }

    [Fact]
    public void A_new_case_reaches_Level_1_at_its_threshold()
    {
        var c = new Case { EscalationLevel = 0 };
        var levels = new[] { Level(1, 50), Level(2, 75) };

        Assert.Null(Decide(c, Snapshot(49), levels));
        Assert.Equal(1, Decide(c, Snapshot(50), levels)!.Level.LevelNumber);
    }

    [Fact]
    public void When_several_thresholds_are_met_the_highest_is_reached()
    {
        var c = new Case { EscalationLevel = 0 };

        Assert.Equal(3, Decide(c, Snapshot(95), Level(1, 50), Level(2, 75), Level(3, 90), Level(4, 100))!.Level.LevelNumber);
    }

    [Fact]
    public void Levels_the_case_already_reached_never_fire_again()
    {
        var c = new Case { EscalationLevel = 2 };

        Assert.Null(Decide(c, Snapshot(80), Level(1, 50), Level(2, 75), Level(3, 90)));
    }

    [Fact]
    public void Paused_and_resolved_cases_never_escalate()
    {
        var c = new Case { EscalationLevel = 0 };

        Assert.Null(Decide(c, Snapshot(200, paused: true), Level(1, 50)));
        Assert.Null(Decide(c, Snapshot(200, stopped: true), Level(1, 50)));
    }
}

/// <summary>
/// Who a case can be escalated to at a level: people holding the level's role, narrowed to the case's team, then to the
/// teams serving the case's department, else everyone in the role — and never the caller or the current holder.
/// </summary>
public class EscalationRecipientTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("esc_" + Guid.NewGuid()).Options);

    private static EscalationService Service(AppDbContext db) =>
        new(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), new FakeOrgDirectory(db));

    private static User U(string name, string role, bool active = true) =>
        new() { Id = Guid.NewGuid(), Name = name, Email = name + "@x.test", Role = role, IsActive = active };

    private static EscalationLevelConfig Level(string role) => new() { LevelNumber = 1, Name = "Level 1", TargetRole = role };

    [Fact]
    public async Task Holders_of_the_role_in_the_cases_own_team_are_offered_first()
    {
        using var db = NewDb();
        var owner = U("Owner", "Agent");
        var ourLead = U("Ours", "Team Lead");
        var theirLead = U("Theirs", "Team Lead");
        var team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", OwnerId = ourLead.Id };
        var other = new Team { Id = Guid.NewGuid(), Name = "Loans", Code = "LNS", OwnerId = theirLead.Id };
        db.AddRange(owner, ourLead, theirLead, team, other);
        await db.SaveChangesAsync();

        var c = new Case { OwnerId = owner.Id, TeamId = team.Id };
        var people = await Service(db).RecipientsAsync(c, Level("team lead"), owner.Id);

        Assert.Equal(new[] { ourLead.Id }, people.Select(p => p.User.Id));
        Assert.True(people[0].IsCaseTeam);
    }

    [Fact]
    public async Task A_team_serving_the_cases_department_comes_next_then_everyone_in_the_role()
    {
        using var db = NewDb();
        var owner = U("Owner", "Agent");
        var deptSupervisor = U("Dept", "Supervisor");
        var elsewhere = U("Elsewhere", "Supervisor");
        var caseTeam = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD" };
        var servingTeam = new Team { Id = Guid.NewGuid(), Name = "Disputes", Code = "DSP", IsActive = true };
        var department = new Department { Id = Guid.NewGuid(), Name = "Retail", Code = "RTL" };
        db.AddRange(owner, deptSupervisor, elsewhere, caseTeam, servingTeam, department);
        db.TeamDepartments.Add(new TeamDepartment { TeamId = servingTeam.Id, DepartmentId = department.Id });
        db.TeamMembers.Add(new TeamMember { TeamId = servingTeam.Id, UserId = deptSupervisor.Id });
        await db.SaveChangesAsync();

        var inDepartment = await Service(db).RecipientsAsync(new Case { OwnerId = owner.Id, TeamId = caseTeam.Id, DepartmentId = department.Id }, Level("Supervisor"), owner.Id);
        var anywhere = await Service(db).RecipientsAsync(new Case { OwnerId = owner.Id, TeamId = caseTeam.Id }, Level("Supervisor"), owner.Id);

        Assert.Equal(new[] { deptSupervisor.Id }, inDepartment.Select(p => p.User.Id));
        Assert.Equal(new[] { deptSupervisor.Id, elsewhere.Id }.OrderBy(x => x), anywhere.Select(p => p.User.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task The_caller_the_holder_and_inactive_people_are_never_offered()
    {
        using var db = NewDb();
        var owner = U("Owner", "Manager");
        var me = U("Me", "Manager");
        var gone = U("Gone", "Manager", active: false);
        var other = U("Other", "Manager");
        db.Users.AddRange(owner, me, gone, other);
        await db.SaveChangesAsync();

        var people = await Service(db).RecipientsAsync(new Case { OwnerId = owner.Id, TeamId = Guid.NewGuid() }, Level("Manager"), me.Id);

        Assert.Equal(new[] { other.Id }, people.Select(p => p.User.Id));
    }
}

/// <summary>The monitor and the SLA routing configuration, on a real migrated database.</summary>
public class SlaMonitorTests
{
    private sealed class World : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public Guid Dept, Owner, Lead, Supervisor;

        public AppDbContext Ctx() => Db.NewContext();

        public static async Task<World> CreateAsync()
        {
            var w = new World { Db = await TempDatabase.CreateAsync() };
            await using (var setup = w.Db.NewContext())
            {
                await setup.Database.MigrateAsync();
                DbSeeder.Run(setup, SeedMode.Bootstrap, adoptedLegacyDatabase: false);

                // A week-round calendar (UTC) keeps these tests independent of when they run.
                setup.BusinessHours.ExecuteDelete();
                foreach (var d in Enum.GetValues<DayOfWeek>())
                    setup.BusinessHours.Add(new BusinessHour { DayOfWeek = d, DayName = d.ToString(), IsEnabled = true, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(24), CreatedAt = DateTime.UtcNow });
                setup.BusinessCalendarSettings.ExecuteUpdate(s => s.SetProperty(x => x.TimeZoneId, "UTC"));

                w.Dept = Guid.NewGuid(); w.Owner = Guid.NewGuid(); w.Lead = Guid.NewGuid(); w.Supervisor = Guid.NewGuid();
                setup.Teams.Add(new Team { Id = w.Dept, Name = "Support", Code = "SUP", CreatedAt = DateTime.UtcNow });
                await setup.SaveChangesAsync();
                setup.Users.AddRange(
                    new User { Id = w.Owner, Name = "Owner", Email = "o@x.test", Role = "Agent", CreatedAt = DateTime.UtcNow },
                    new User { Id = w.Lead, Name = "Lead", Email = "l@x.test", Role = "Team Lead", CreatedAt = DateTime.UtcNow },
                    new User { Id = w.Supervisor, Name = "Sup", Email = "s@x.test", Role = "CX Supervisor", CreatedAt = DateTime.UtcNow });
                await setup.SaveChangesAsync();

                // Escalation levels are never seeded (each names a Host role an administrator chooses).
                EscalationLevelConfig L(int n, string role, decimal pct, bool owner = false) => new()
                {
                    Id = Guid.NewGuid(), LevelNumber = n, Name = $"Level {n}", AssignmentType = "Role", TargetRole = role, TriggerType = "SlaPercentage",
                    TriggerValue = pct, ActionDescription = $"Escalate to {role}", NotifyRoleHolders = true, NotifyCaseOwner = owner,
                    NotificationPriority = "High", DisplayOrder = n, IsActive = true, CreatedAt = DateTime.UtcNow,
                };
                setup.EscalationLevelConfigs.AddRange(L(1, "Team Lead", 70, owner: true), L(2, "CX Supervisor", 90), L(3, "Head of Customer Experience", 100));
                await setup.SaveChangesAsync();
            }
            return w;
        }

        public SlaMonitorService Monitor(AppDbContext ctx, INotificationService? notifications = null)
        {
            var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
            var escalation = new EscalationService(ctx, cache, new FakeOrgDirectory(ctx));
            var clock = new SlaClockProvider(new BusinessTimeService(ctx, cache), escalation);
            return new SlaMonitorService(ctx, clock, escalation, notifications ?? new NotificationService(ctx, new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions())), Options.Create(new NotificationOptions())), NullLogger<SlaMonitorService>.Instance);
        }

        /// <summary>An open case that started <paramref name="ageMinutes"/> ago with 100 internal / 200 external target minutes.</summary>
        public async Task<Guid> AddCaseAsync(int ageMinutes, CaseStatus status = CaseStatus.InProgress, DateTime? pausedAt = null)
        {
            await using var ctx = Ctx();
            var customer = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60" + Random.Shared.NextInt64(1000000000, 1999999999) };
            ctx.Customers.Add(customer);
            var start = DateTime.UtcNow.AddMinutes(-ageMinutes);
            var c = new Case
            {
                Id = Guid.NewGuid(), CaseNumber = "T-" + Guid.NewGuid().ToString("N")[..8], Title = "t", CustomerId = customer.Id, TeamId = Dept, OwnerId = Owner,
                Status = status, Severity = "High", SlaStartTime = start, SlaPausedAt = pausedAt,
                FirstResponseTargetMinutes = 30, InternalResolutionTargetMinutes = 100, ExternalResolutionTargetMinutes = 200,
                FirstResponseActualAt = start.AddMinutes(1), FirstResponseStatus = "Met", EscalationLevel = 0, CreatedAt = start,
            };
            ctx.Cases.Add(c);
            await ctx.SaveChangesAsync();
            return c.Id;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    [PostgresFact]
    public async Task Reaching_a_level_notifies_the_people_it_names_once()
    {
        await using var w = await World.CreateAsync();
        var young = await w.AddCaseAsync(ageMinutes: 50);     // 50% of 100
        var old = await w.AddCaseAsync(ageMinutes: 75);       // 75% >= 70: Level 1

        await using (var ctx = w.Ctx()) { var r1 = await w.Monitor(ctx).RunCycleAsync(); Assert.True(r1.Ran); Assert.Equal(1, r1.Escalations); }
        await using (var ctx = w.Ctx()) { var r2 = await w.Monitor(ctx).RunCycleAsync(); Assert.Equal(0, r2.Escalations); }

        await using var check = w.Ctx();
        Assert.Equal(1, (await check.Cases.FindAsync(old))!.EscalationLevel);
        Assert.Equal(0, (await check.Cases.FindAsync(young))!.EscalationLevel);
        var told = await check.Notifications.Where(n => n.CaseId == old && n.Type == "CASE_AUTOMATICALLY_ESCALATED").Select(n => n.RecipientUserId).ToListAsync();
        Assert.Equal(new[] { w.Lead, w.Owner }.OrderBy(x => x), told.OrderBy(x => x));   // the role's holder and, as configured, the case's holder
    }

    [PostgresFact]
    public async Task Changing_a_levels_threshold_changes_when_it_is_reached()
    {
        await using var w = await World.CreateAsync();
        await using (var ctx = w.Ctx())
            await ctx.EscalationLevelConfigs.Where(l => l.LevelNumber == 1).ExecuteUpdateAsync(u => u.SetProperty(l => l.TriggerValue, 40m));
        await w.AddCaseAsync(ageMinutes: 50);

        await using var run = w.Ctx();
        Assert.Equal(1, (await w.Monitor(run).RunCycleAsync()).Escalations);
    }

    [PostgresFact]
    public async Task Automatic_escalation_only_notifies_the_holder_and_status_stay()
    {
        await using var w = await World.CreateAsync();
        var id = await w.AddCaseAsync(ageMinutes: 95);        // 95%: Level 2 (90%)

        await using (var ctx = w.Ctx()) Assert.Equal(1, (await w.Monitor(ctx).RunCycleAsync()).Escalations);

        await using var check = w.Ctx();
        var c = (await check.Cases.FindAsync(id))!;
        Assert.Equal(2, c.EscalationLevel);
        Assert.Equal(CaseStatus.InProgress, c.Status);
        Assert.Equal(w.Owner, c.OwnerId);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.CaseId == id && n.Type == "CASE_AUTOMATICALLY_ESCALATED" && n.RecipientUserId == w.Supervisor));
        Assert.Equal(1, await check.CaseEvents.CountAsync(e => e.CaseId == id && e.ActionType == "AUTO_ESCALATION"));
    }

    [PostgresFact]
    public async Task A_breached_case_records_when_it_breached_and_reaches_the_full_consumption_level()
    {
        await using var w = await World.CreateAsync();
        var id = await w.AddCaseAsync(ageMinutes: 130);       // internal breached, external (200) not yet

        await using (var ctx = w.Ctx()) await w.Monitor(ctx).RunCycleAsync();

        await using var check = w.Ctx();
        var c = (await check.Cases.FindAsync(id))!;
        Assert.NotNull(c.SlaBreachedAt);
        Assert.Equal(3, c.EscalationLevel);
    }

    [PostgresFact]
    public async Task APausedCase_IsNeverEscalatedOrBreached()
    {
        await using var w = await World.CreateAsync();
        // Waiting on the customer since 10 minutes after it started — only 10% consumed, however long it waits.
        var id = await w.AddCaseAsync(ageMinutes: 600, CaseStatus.WaitingOnCustomer, pausedAt: DateTime.UtcNow.AddMinutes(-590));

        await using (var ctx = w.Ctx()) { var r = await w.Monitor(ctx).RunCycleAsync(); Assert.Equal(0, r.Escalations + r.Breaches); }

        await using var check = w.Ctx();
        var c = (await check.Cases.FindAsync(id))!;
        Assert.Equal(0, c.EscalationLevel);
        Assert.Equal(CaseStatus.WaitingOnCustomer, c.Status);
        Assert.Null(c.SlaBreachedAt);
    }

    [PostgresFact]
    public async Task Inactive_and_incomplete_levels_are_not_executed()
    {
        await using var w = await World.CreateAsync();
        await using (var ctx = w.Ctx())
        {
            await ctx.EscalationLevelConfigs.Where(l => l.LevelNumber == 2).ExecuteUpdateAsync(u => u.SetProperty(l => l.IsActive, false));
            await ctx.EscalationLevelConfigs.Where(l => l.LevelNumber == 3).ExecuteUpdateAsync(u => u.SetProperty(l => l.ActionDescription, ""));
        }
        var id = await w.AddCaseAsync(ageMinutes: 130);       // would have reached Level 3

        await using (var ctx = w.Ctx()) await w.Monitor(ctx).RunCycleAsync();
        await using var check = w.Ctx();
        Assert.Equal(1, (await check.Cases.FindAsync(id))!.EscalationLevel);
    }

    [PostgresFact]
    public async Task OnlyOneInstanceMonitorsAtATime()
    {
        await using var w = await World.CreateAsync();
        await w.AddCaseAsync(ageMinutes: 50);

        // Another instance is mid-cycle (holds the advisory lock on its own connection).
        await using var other = new Npgsql.NpgsqlConnection(w.Db.ConnectionString);
        await other.OpenAsync();
        await using (var cmd = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_lock({SlaMonitorService.LockKey})", other)) await cmd.ExecuteNonQueryAsync();

        await using (var ctx = w.Ctx()) Assert.False((await w.Monitor(ctx).RunCycleAsync()).Ran);

        await using (var cmd = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_unlock({SlaMonitorService.LockKey})", other)) await cmd.ExecuteNonQueryAsync();
        await using (var ctx = w.Ctx()) Assert.True((await w.Monitor(ctx).RunCycleAsync()).Ran);
    }

    [PostgresFact]
    public async Task OneBadCaseDoesNotStopTheOthers()
    {
        await using var w = await World.CreateAsync();
        var good = await w.AddCaseAsync(ageMinutes: 75);
        await using (var ctx = w.Ctx())
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"Cases\" SET \"InternalResolutionTargetMinutes\" = 0 WHERE \"Id\" <> {0}", good);   // a degenerate snapshot
        await w.AddCaseAsync(ageMinutes: 10);

        await using var run = w.Ctx();
        var report = await w.Monitor(run).RunCycleAsync();
        Assert.True(report.Ran);
        Assert.Equal(1, report.Escalations);
    }

    [PostgresFact]
    public async Task NotificationEventKeys_PreventDuplicatesButAllowDistinctEvents()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var svc = new NotificationService(ctx, new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions())), Options.Create(new NotificationOptions()));
        var caseId = Guid.NewGuid();

        await svc.CreateNotificationAsync(w.Lead, "CASE_AUTOMATICALLY_ESCALATED", "L2", "m", null, "C-1", "Critical", 0, default, "escalation:x:2");
        await svc.CreateNotificationAsync(w.Lead, "CASE_AUTOMATICALLY_ESCALATED", "L2 again", "m", null, "C-1", "Critical", 0, default, "escalation:x:2");
        await svc.CreateNotificationAsync(w.Lead, "CASE_AUTOMATICALLY_ESCALATED", "L3", "m", null, "C-1", "Critical", 0, default, "escalation:x:3");   // same type, same minute: a different event

        Assert.Equal(2, await ctx.Notifications.CountAsync(n => n.RecipientUserId == w.Lead));
    }

    [PostgresFact]
    public async Task EscalationConfiguration_IsValidatedAndDescriptionsAreDerived()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
        var directory = new FakeOrgDirectory(ctx, caseRoles: new[] { "Team Lead", "CX Supervisor", "Head of Customer Experience", "Director" });
        var svc = new SlaRoutingService(ctx, NullLogger<SlaRoutingService>.Instance, new EscalationService(ctx, cache, directory), cache, directory);
        await ctx.EscalationLevelConfigs.Where(l => l.LevelNumber == 3).ExecuteDeleteAsync();

        // Incomplete, a role already used, a role without access, a threshold not above Level 2's.
        await Assert.ThrowsAsync<FieldValidationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = 95, TargetRole = "Director" }, w.Owner));
        await Assert.ThrowsAsync<FieldValidationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = 95, TargetRole = "Team Lead", ActionDescription = "x" }, w.Owner));
        await Assert.ThrowsAsync<FieldValidationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = 95, TargetRole = "Teller", ActionDescription = "x" }, w.Owner));
        await Assert.ThrowsAsync<FieldValidationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = 85, TargetRole = "Director", ActionDescription = "x" }, w.Owner));

        var created = await svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = 95, TargetRole = "Director", ActionDescription = "Director review", NotifyCaseOwner = true }, w.Owner);
        Assert.Equal("SLA consumption reaches 95%", created.TriggerDescription);
        Assert.Empty(created.MissingFields);

        var updated = await svc.UpdateEscalationLevelAsync(created.Id!.Value, new Api.DTOs.UpdateEscalationLevelDto { TriggerValue = 100, IsActive = false }, w.Owner);
        Assert.Equal("SLA consumption reaches 100%", updated!.TriggerDescription);
        Assert.False(updated.IsActive);
    }

    [PostgresFact]
    public async Task DeletingALevel_MovesCasesWithTheRenumbering()
    {
        await using var w = await World.CreateAsync();
        var onThree = await w.AddCaseAsync(ageMinutes: 1);
        var onTwo = await w.AddCaseAsync(ageMinutes: 1);
        await using (var ctx = w.Ctx())
        {
            await ctx.Cases.Where(c => c.Id == onThree).ExecuteUpdateAsync(u => u.SetProperty(c => c.EscalationLevel, 3));
            await ctx.Cases.Where(c => c.Id == onTwo).ExecuteUpdateAsync(u => u.SetProperty(c => c.EscalationLevel, 2));
        }

        await using var run = w.Ctx();
        var svc = new SlaRoutingService(run, NullLogger<SlaRoutingService>.Instance, new EscalationService(run, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), new FakeOrgDirectory(run)));
        var level2 = await run.EscalationLevelConfigs.AsNoTracking().FirstAsync(l => l.LevelNumber == 2);
        Assert.True(await svc.DeleteEscalationLevelAsync(level2.Id, w.Owner));

        await using var check = w.Ctx();
        Assert.Equal(2, (await check.Cases.FindAsync(onThree))!.EscalationLevel);   // old level 3 is now level 2
        Assert.Equal(1, (await check.Cases.FindAsync(onTwo))!.EscalationLevel);     // its level no longer exists: back one
    }

    [PostgresFact]
    public async Task TheTimeZone_IsConfigurationAndValidated()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
        var business = new BusinessTimeService(ctx, cache);

        var cal = await business.GetCalendarAsync();
        Assert.Equal("UTC", cal.TimeZone.Id);

        Assert.Throws<InvalidOperationException>(() => BusinessTimeService.FindTimeZone("Mars/Olympus_Mons"));
        Assert.Throws<InvalidOperationException>(() => BusinessTimeService.FindTimeZone(""));
    }

    [PostgresFact]
    public async Task EverySeededCase_CarriesARealSlaSnapshot()
    {
        // Regression: sample cases once relied on model defaults and read as "no target" once those defaults were removed.
        await using var db = await TempDatabase.CreateAsync();
        await using var ctx = db.NewContext();
        await ctx.Database.MigrateAsync();
        DbSeeder.Run(ctx, SeedMode.Development, adoptedLegacyDatabase: false);

        var cases = await ctx.Cases.AsNoTracking().ToListAsync();
        Assert.NotEmpty(cases);
        Assert.All(cases, c =>
        {
            Assert.True(c.InternalResolutionTargetMinutes > 0, $"{c.CaseNumber} has no internal target");
            Assert.True(c.ExternalResolutionTargetMinutes >= c.InternalResolutionTargetMinutes, $"{c.CaseNumber} external < internal");
            Assert.True(c.FirstResponseTargetMinutes > 0, $"{c.CaseNumber} has no first-response target");
            Assert.NotNull(c.InternalResolutionDueAt);
        });
    }

    [PostgresFact]
    public async Task FinishedCasesWithoutAnOutcome_GetOneFromTheMonitor_AccordingToTheirOwnClock()
    {
        await using var w = await World.CreateAsync();
        var onTime = await w.AddCaseAsync(ageMinutes: 600, CaseStatus.Resolved);   // targets: 100 internal / 200 external business minutes
        var late = await w.AddCaseAsync(ageMinutes: 600, CaseStatus.Resolved);
        await using (var ctx = w.Ctx())
        {
            // Resolved after 150 minutes (within 200) / after 500 minutes (past 200); no outcome recorded yet (legacy rows).
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"Cases\" SET \"ResolvedAt\" = \"SlaStartTime\" + interval '150 minutes', \"SlaOutcome\" = NULL WHERE \"Id\" = {0}", onTime);
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"Cases\" SET \"ResolvedAt\" = \"SlaStartTime\" + interval '500 minutes', \"SlaOutcome\" = NULL WHERE \"Id\" = {0}", late);
        }

        await using (var ctx = w.Ctx()) await w.Monitor(ctx).RunCycleAsync();

        await using var check = w.Ctx();
        Assert.Equal("Met", (await check.Cases.FindAsync(onTime))!.SlaOutcome);
        Assert.Equal("Breached", (await check.Cases.FindAsync(late))!.SlaOutcome);

        // And the next cycle leaves them alone.
        await using (var ctx = w.Ctx()) await w.Monitor(ctx).RunCycleAsync();
        await using var again = w.Ctx();
        Assert.Equal("Breached", (await again.Cases.FindAsync(late))!.SlaOutcome);
    }
}
