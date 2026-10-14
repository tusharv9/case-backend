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

public class EscalationTriggerTests
{
    [Theory]
    [InlineData("SlaPercentage", 70, true)]
    [InlineData("SlaPercentage", null, false)]      // a threshold is required
    [InlineData("SlaPercentage", 0, false)]
    [InlineData("SlaPostBreachHours", 12, true)]
    [InlineData("SlaPostBreachHours", null, false)]
    [InlineData("SlaBreached", null, true)]
    [InlineData("FirstResponseBreached", null, true)]
    [InlineData("ManualOnly", null, true)]
    [InlineData("WhenTheMoonIsFull", null, false)]  // only triggers the engine can execute
    public void Validate_AcceptsOnlyExecutableTriggers(string type, int? value, bool valid)
    {
        Assert.Equal(valid, EscalationTriggers.Validate(type, value) == null);
    }

    [Fact]
    public void Describe_IsDerivedFromTheStructure()
    {
        Assert.Equal("SLA consumption reaches 90%", EscalationTriggers.Describe("SlaPercentage", 90m));
        Assert.Equal("SLA has been breached for 12 hours", EscalationTriggers.Describe("SlaPostBreachHours", 12m));
        Assert.Equal("SLA is breached", EscalationTriggers.Describe("SlaBreached", null));
    }
}

public class EscalationDecisionTests
{
    private static EscalationLevelConfig Level(int n, string type, decimal? value = null, bool active = true) => new()
    {
        LevelNumber = n, Name = $"Level {n}", TriggerType = type, TriggerValue = value, IsActive = active, TargetRole = "Role" + n,
    };

    private static EscalationDecision? Decide(Case c, SlaSnapshot s, params EscalationLevelConfig[] levels)
    {
        var svc = new EscalationService(null!, null!);
        var policy = new EscalationPolicy(levels.Where(l => l.IsActive).ToList(), null);
        return svc.FindDueLevel(c, s, policy, DateTime.UtcNow);
    }

    private static SlaSnapshot Snapshot(double internalPct = 0, bool internalBreached = false, bool paused = false, bool frBreached = false, bool stopped = false)
    {
        SlaTargetState T(double pct, bool b) => new(100, pct, 100 - pct, pct, null, b);
        return new SlaSnapshot(SlaHealth.Healthy, paused, stopped, !paused, T(internalPct, internalBreached), T(internalPct / 2, false),
            T(0, frBreached), "Pending", DateTime.UtcNow);
    }

    [Fact]
    public void PercentageTrigger_FiresAtItsThreshold()
    {
        var c = new Case { EscalationLevel = 1 };
        var levels = new[] { Level(1, "SlaPercentage", 70), Level(2, "SlaPercentage", 90) };

        Assert.Null(Decide(c, Snapshot(89), levels));
        Assert.Equal(2, Decide(c, Snapshot(90), levels)!.Level.LevelNumber);
    }

    [Fact]
    public void ANewLevel_UsesItsOwnThreshold_NotADefault70()
    {
        // Regression: levels added without a value used to fire at 70%.
        var c = new Case { EscalationLevel = 2 };
        var levels = new[] { Level(1, "SlaPercentage", 70), Level(2, "SlaPercentage", 90), Level(5, "SlaPercentage", 150) };

        Assert.Null(Decide(c, Snapshot(100), levels));
        Assert.Equal(5, Decide(c, Snapshot(150), levels)!.Level.LevelNumber);
    }

    [Fact]
    public void PausedAndResolvedCases_NeverEscalate()
    {
        var c = new Case { EscalationLevel = 1 };
        var levels = new[] { Level(1, "SlaPercentage", 70), Level(2, "SlaBreached") };

        Assert.Null(Decide(c, Snapshot(200, internalBreached: true, paused: true), levels));
        Assert.Null(Decide(c, Snapshot(200, internalBreached: true, stopped: true), levels));
        Assert.NotNull(Decide(c, Snapshot(200, internalBreached: true), levels));
    }

    [Fact]
    public void InactiveLevels_AreSkipped_AndManualOnlyLevelsDoNotBlock()
    {
        var c = new Case { EscalationLevel = 1 };
        var levels = new[]
        {
            Level(1, "SlaPercentage", 70),
            Level(2, "SlaBreached", active: false),
            Level(3, "ManualOnly"),
            Level(4, "SlaBreached"),
        };
        Assert.Equal(4, Decide(c, Snapshot(120, internalBreached: true), levels)!.Level.LevelNumber);
    }

    [Fact]
    public void Levels_AreSequential()
    {
        // Level 3's trigger is met but level 2's is not: 2 must come first.
        var c = new Case { EscalationLevel = 1 };
        var levels = new[] { Level(1, "SlaPercentage", 70), Level(2, "SlaPercentage", 200), Level(3, "SlaBreached") };
        Assert.Null(Decide(c, Snapshot(120, internalBreached: true), levels));
    }

    [Fact]
    public void PostBreachHours_CountsFromTheRecordedBreach()
    {
        var svc = new EscalationService(null!, null!);
        var policy = new EscalationPolicy(new[] { Level(1, "SlaPercentage", 70), Level(2, "SlaPostBreachHours", 12) }, null);
        var now = DateTime.UtcNow;
        var s = Snapshot(150, internalBreached: true);

        Assert.Null(svc.FindDueLevel(new Case { EscalationLevel = 1, SlaBreachedAt = now.AddHours(-11) }, s, policy, now));
        Assert.NotNull(svc.FindDueLevel(new Case { EscalationLevel = 1, SlaBreachedAt = now.AddHours(-13) }, s, policy, now));
    }

    [Fact]
    public void FirstResponseTrigger_FiresOnAMissedFirstResponse()
    {
        var c = new Case { EscalationLevel = 1 };
        var levels = new[] { Level(1, "SlaPercentage", 70), Level(2, "FirstResponseBreached") };
        Assert.Null(Decide(c, Snapshot(10), levels));
        Assert.NotNull(Decide(c, Snapshot(10, frBreached: true), levels));
    }
}

public class EscalationTargetTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("esc_" + Guid.NewGuid()).Options);

    private static EscalationService Service(AppDbContext db) =>
        new(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())));

    private static User U(string name, string role, Guid? dept = null, bool active = true) =>
        new() { Id = Guid.NewGuid(), Name = name, Email = name + "@x.test", Role = role, DepartmentId = dept, IsActive = active };

    [Fact]
    public async Task RoleMatch_IsExact_NotAContainsGuess()
    {
        using var db = NewDb();
        var owner = U("Owner", "Agent");
        var headTeller = U("Head Teller", "Head Teller");       // contains "Head" — must NOT become the Head of CX
        var head = U("Real Head", "Head of Customer Experience");
        db.Users.AddRange(owner, headTeller, head);
        await db.SaveChangesAsync();

        var c = new Case { OwnerId = owner.Id, DepartmentId = Guid.NewGuid() };
        var level = new EscalationLevelConfig { AssignmentType = "Role", TargetRole = "Head of Customer Experience" };

        Assert.Equal(head.Id, (await Service(db).ResolveTargetAsync(c, level))!.Id);
    }

    [Fact]
    public async Task PrefersTheCasesOwnTeam_AndNeverTheCurrentOwner()
    {
        using var db = NewDb();
        var deptA = Guid.NewGuid(); var deptB = Guid.NewGuid();
        var owner = U("Owner", "Team Lead", deptA);
        var otherTeamLead = U("Alpha", "Team Lead", deptB);
        var sameTeamLead = U("Zed", "Team Lead", deptA);
        db.Users.AddRange(owner, otherTeamLead, sameTeamLead);
        await db.SaveChangesAsync();

        var c = new Case { OwnerId = owner.Id, DepartmentId = deptA };
        var level = new EscalationLevelConfig { AssignmentType = "Role", TargetRole = "team lead" };   // case-insensitive

        Assert.Equal(sameTeamLead.Id, (await Service(db).ResolveTargetAsync(c, level))!.Id);
    }

    [Fact]
    public async Task InactiveUsers_AreNeverTargets()
    {
        using var db = NewDb();
        var owner = U("Owner", "Agent");
        var gone = U("Gone", "Team Lead", active: false);
        db.Users.AddRange(owner, gone);
        await db.SaveChangesAsync();

        var c = new Case { OwnerId = owner.Id, DepartmentId = Guid.NewGuid() };
        Assert.Null(await Service(db).ResolveTargetAsync(c, new EscalationLevelConfig { AssignmentType = "Role", TargetRole = "Team Lead" }));
    }

    [Fact]
    public async Task WhenNobodyHoldsTheRole_ItFallsBackToTheDepartmentOwner_NotToAnyone()
    {
        using var db = NewDb();
        var owner = U("Owner", "Agent");
        var deptOwner = U("DeptOwner", "Manager");
        var stranger = U("Stranger", "Cashier");
        var dept = new Department { Id = Guid.NewGuid(), Name = "D", Code = "D", OwnerId = deptOwner.Id };
        db.AddRange(owner, deptOwner, stranger, dept);
        await db.SaveChangesAsync();

        var c = new Case { OwnerId = owner.Id, DepartmentId = dept.Id };
        var level = new EscalationLevelConfig { AssignmentType = "Role", TargetRole = "CX Supervisor" };
        Assert.Equal(deptOwner.Id, (await Service(db).ResolveTargetAsync(c, level))!.Id);

        dept.OwnerId = null;
        await db.SaveChangesAsync();
        Assert.Null(await Service(db).ResolveTargetAsync(c, level));
    }

    [Fact]
    public async Task AssignmentTypes_UserAndOwner()
    {
        using var db = NewDb();
        var owner = U("Owner", "Agent"); var named = U("Named", "Whatever");
        db.Users.AddRange(owner, named);
        await db.SaveChangesAsync();
        var c = new Case { OwnerId = owner.Id, DepartmentId = Guid.NewGuid() };

        Assert.Equal(named.Id, (await Service(db).ResolveTargetAsync(c, new EscalationLevelConfig { AssignmentType = "User", TargetUserId = named.Id }))!.Id);
        Assert.Equal(owner.Id, (await Service(db).ResolveTargetAsync(c, new EscalationLevelConfig { AssignmentType = "Owner" }))!.Id);
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
                setup.Departments.Add(new Department { Id = w.Dept, Name = "Support", Code = "SUP", CreatedAt = DateTime.UtcNow });
                await setup.SaveChangesAsync();
                setup.Users.AddRange(
                    new User { Id = w.Owner, Name = "Owner", Email = "o@x.test", Role = "Agent", DepartmentId = w.Dept, CreatedAt = DateTime.UtcNow },
                    new User { Id = w.Lead, Name = "Lead", Email = "l@x.test", Role = "Team Lead", DepartmentId = w.Dept, CreatedAt = DateTime.UtcNow },
                    new User { Id = w.Supervisor, Name = "Sup", Email = "s@x.test", Role = "CX Supervisor", DepartmentId = w.Dept, CreatedAt = DateTime.UtcNow });
                await setup.SaveChangesAsync();
            }
            return w;
        }

        public SlaMonitorService Monitor(AppDbContext ctx, INotificationService? notifications = null)
        {
            var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
            var escalation = new EscalationService(ctx, cache);
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
                Id = Guid.NewGuid(), CaseNumber = "T-" + Guid.NewGuid().ToString("N")[..8], Title = "t", CustomerId = customer.Id, DepartmentId = Dept, OwnerId = Owner,
                Status = status, Severity = "High", SlaStartTime = start, SlaPausedAt = pausedAt,
                FirstResponseTargetMinutes = 30, InternalResolutionTargetMinutes = 100, ExternalResolutionTargetMinutes = 200,
                FirstResponseActualAt = start.AddMinutes(1), FirstResponseStatus = "Met", EscalationLevel = 1, CreatedAt = start,
            };
            ctx.Cases.Add(c);
            await ctx.SaveChangesAsync();
            return c.Id;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    [PostgresFact]
    public async Task Reminder_UsesTheFirstLevelsThreshold_AndIsSentOnce()
    {
        await using var w = await World.CreateAsync();
        var young = await w.AddCaseAsync(ageMinutes: 50);     // 50% of 100
        var old = await w.AddCaseAsync(ageMinutes: 75);       // 75% >= 70

        await using (var ctx = w.Ctx()) { var r1 = await w.Monitor(ctx).RunCycleAsync(); Assert.True(r1.Ran); Assert.Equal(1, r1.Reminders); }
        await using (var ctx = w.Ctx()) { var r2 = await w.Monitor(ctx).RunCycleAsync(); Assert.Equal(0, r2.Reminders); }

        await using var check = w.Ctx();
        Assert.True((await check.Cases.FindAsync(old))!.SlaReminderSent);
        Assert.False((await check.Cases.FindAsync(young))!.SlaReminderSent);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.CaseId == old && n.Type == "SLA_REMINDER"));
    }

    [PostgresFact]
    public async Task ChangingLevelOnesThreshold_ChangesWhenTheReminderFires()
    {
        await using var w = await World.CreateAsync();
        await using (var ctx = w.Ctx())
            await ctx.EscalationLevelConfigs.Where(l => l.LevelNumber == 1).ExecuteUpdateAsync(u => u.SetProperty(l => l.TriggerValue, 40m));
        await w.AddCaseAsync(ageMinutes: 50);

        await using var run = w.Ctx();
        Assert.Equal(1, (await w.Monitor(run).RunCycleAsync()).Reminders);
    }

    [PostgresFact]
    public async Task EscalatesOneLevelPerCycle_ReassignsToTheRoleHolder_AndNotifiesOnce()
    {
        await using var w = await World.CreateAsync();
        var id = await w.AddCaseAsync(ageMinutes: 95);        // 95% of internal: level 2 (90%)

        await using (var ctx = w.Ctx()) Assert.Equal(1, (await w.Monitor(ctx).RunCycleAsync()).Escalations);
        await using (var ctx = w.Ctx()) Assert.Equal(0, (await w.Monitor(ctx).RunCycleAsync()).Escalations);   // level 3 needs a breach

        await using var check = w.Ctx();
        var c = (await check.Cases.FindAsync(id))!;
        Assert.Equal(2, c.EscalationLevel);
        Assert.Equal(CaseStatus.Escalated, c.Status);
        Assert.Equal(w.Lead, c.OwnerId);
        Assert.Equal(1, await check.Notifications.CountAsync(n => n.CaseId == id && n.Type == "CASE_AUTOMATICALLY_ESCALATED" && n.RecipientUserId == w.Lead));
    }

    [PostgresFact]
    public async Task ABreachedCase_ReachesTheNextLevel_AndRecordsWhenItBreached()
    {
        await using var w = await World.CreateAsync();
        var id = await w.AddCaseAsync(ageMinutes: 130);       // internal breached, external (200) not yet

        for (var i = 0; i < 3; i++) { await using var ctx = w.Ctx(); await w.Monitor(ctx).RunCycleAsync(); }

        await using var check = w.Ctx();
        var c = (await check.Cases.FindAsync(id))!;
        Assert.NotNull(c.SlaBreachedAt);
        Assert.Equal(3, c.EscalationLevel);                   // SlaBreached level, then waits for 12h post-breach
        Assert.Equal(w.Supervisor, c.OwnerId);
    }

    [PostgresFact]
    public async Task APausedCase_IsNeverEscalatedOrBreached()
    {
        await using var w = await World.CreateAsync();
        // Waiting on the customer since 10 minutes after it started — only 10% consumed, however long it waits.
        var id = await w.AddCaseAsync(ageMinutes: 600, CaseStatus.WaitingOnCustomer, pausedAt: DateTime.UtcNow.AddMinutes(-590));

        await using (var ctx = w.Ctx()) { var r = await w.Monitor(ctx).RunCycleAsync(); Assert.Equal(0, r.Escalations + r.Reminders + r.Breaches); }

        await using var check = w.Ctx();
        var c = (await check.Cases.FindAsync(id))!;
        Assert.Equal(1, c.EscalationLevel);
        Assert.Equal(CaseStatus.WaitingOnCustomer, c.Status);
        Assert.Null(c.SlaBreachedAt);
    }

    [PostgresFact]
    public async Task InactiveLevels_AreNotExecuted()
    {
        await using var w = await World.CreateAsync();
        await using (var ctx = w.Ctx())
            await ctx.EscalationLevelConfigs.Where(l => l.LevelNumber == 2).ExecuteUpdateAsync(u => u.SetProperty(l => l.IsActive, false));
        var id = await w.AddCaseAsync(ageMinutes: 95);        // would have hit level 2 at 90%

        await using (var ctx = w.Ctx()) Assert.Equal(0, (await w.Monitor(ctx).RunCycleAsync()).Escalations);
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
        Assert.Equal(1, report.Reminders);
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
        var svc = new SlaRoutingService(ctx, NullLogger<SlaRoutingService>.Instance, new EscalationService(ctx, cache));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = null, TargetRole = "Team Lead" }, w.Owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "Bogus", TargetRole = "Team Lead" }, w.Owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaBreached", AssignmentType = "Role", TargetRole = "" }, w.Owner));

        var created = await svc.AddEscalationLevelAsync(
            new Api.DTOs.CreateEscalationLevelDto { TriggerType = "SlaPercentage", TriggerValue = 150, TargetRole = "Director" }, w.Owner);
        Assert.Equal("SLA consumption reaches 150%", created.TriggerDescription);
        Assert.Equal(150m, created.TriggerValue);

        var updated = await svc.UpdateEscalationLevelAsync(created.Id!.Value, new Api.DTOs.UpdateEscalationLevelDto { TriggerType = "SlaBreached", IsActive = false }, w.Owner);
        Assert.Equal("SLA is breached", updated!.TriggerDescription);
        Assert.Null(updated.TriggerValue);
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
        var svc = new SlaRoutingService(run, NullLogger<SlaRoutingService>.Instance, new EscalationService(run, new ConfigCache(new MemoryCache(new MemoryCacheOptions()))));
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
