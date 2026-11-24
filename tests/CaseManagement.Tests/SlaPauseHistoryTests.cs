using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Statuses;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>The SLA across many pause and resume cycles: its history, the maximum pause a status allows, and the dashboard by status.</summary>
/// <remarks>
/// <para>The case keeps only running totals; every pause is also written as its own row whatever paused or resumed it, so the
/// audit trail and the calendar can show when and why — and the minutes in those rows always add up to the case's total.</para>
/// <para>A status's pause limit must act exactly once, counting working time from when the case entered that status, and must
/// never leave the clock stopped past its limit.</para>
/// </remarks>
public class SlaPauseHistoryTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly ConfigCache cache = new(new MemoryCache(new MemoryCacheOptions()));
    private readonly Team team;
    private readonly User agent;
    private readonly SlaClock clock = new(BusinessCalendar.RoundTheClock, null);

    public SlaPauseHistoryTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"pauses-{Guid.NewGuid()}")
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        agent = new User { Id = Guid.NewGuid(), Name = "Agent Ann", Email = "ann@x.test", Role = "Agent", ExternalUserId = "ann" };
        team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", IsActive = true };
        db.AddRange(agent, team);
        db.BusinessCalendarSettings.Add(new BusinessCalendarSetting { Id = Guid.NewGuid(), TimeZoneId = "UTC", CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private sealed class Caller(Guid id) : ICurrentUserAccessor
    {
        public Guid? LocalUserId => id;
        public HostPrincipal? Principal => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { "*" };
        public bool Has(string permission) => true;
    }

    private CaseStatusAdminService Admin() => new(db, cache, new Mock<INotificationService>().Object, new Caller(agent.Id));

    private CaseService Cases() =>
        new(new CaseRepository(db), new Mock<INotificationService>().Object, new Mock<IConfigurableSettingsService>().Object, db,
            new Mock<IWebHostEnvironment>().Object, configCache: cache, currentUser: new Caller(agent.Id), statusCatalog: new CaseStatusCatalog(db, cache));

    private Case AddCase(CaseStatus status)
    {
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..6], Title = "t", TeamId = team.Id, OwnerId = agent.Id,
            Status = status, Severity = "High", SlaStartTime = DateTime.UtcNow.AddHours(-1), InternalResolutionTargetMinutes = 600,
            ExternalResolutionTargetMinutes = 1200, CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.Cases.Add(c);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return c;
    }

    private Task Move(Case c, string status) => Cases().UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = status, UserId = agent.Id });

    private async Task<string> LimitedStatus(string onExpiry, string? target = null, int minutes = 60)
    {
        var created = await Admin().CreateAsync(new SaveCaseStatusDto
        {
            Label = "Pending documents", Category = "Waiting", SlaBehaviour = "Paused",
            MaxPauseMinutes = minutes, OnPauseExpiry = onExpiry, PauseExpiryStatusKey = target,
        });
        var moves = (await Admin().GetAsync(false)).Transitions;
        moves.Add(new CaseStatusTransitionDto { From = "InProgress", To = created.Key });
        moves.Add(new CaseStatusTransitionDto { From = created.Key, To = "InProgress" });
        await Admin().SaveTransitionsAsync(new SaveCaseStatusTransitionsDto { Transitions = moves });
        return created.Key;
    }

    /// <summary>Makes the case look as if it entered its status (and paused) <paramref name="minutesAgo"/> minutes ago.</summary>
    private async Task Age(Case c, int minutesAgo)
    {
        var tracked = await db.Cases.SingleAsync(x => x.Id == c.Id);
        var then = DateTime.UtcNow.AddMinutes(-minutesAgo);
        tracked.StatusEnteredAt = then;
        tracked.SlaPausedAt = then;
        foreach (var p in db.CaseSlaPauses.Where(p => p.CaseId == c.Id && p.EndedAt == null)) p.StartedAt = then;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class FixedClock(SlaClock clock) : ISlaClockProvider
    {
        public Task<SlaClock> GetAsync(CancellationToken ct = default) => Task.FromResult(clock);
    }

    private PauseLimitService Limits() => new(db, new Mock<INotificationService>().Object, NullLogger<PauseLimitService>.Instance);

    // ------------------------------------------------------------------------------------------------ pause history

    [Fact]
    public async Task Each_pause_is_recorded_and_closed_with_its_minutes_matching_the_case_total()
    {
        var c = AddCase(CaseStatus.InProgress);

        await Move(c, "WaitingOnCustomer");
        await Move(c, "InProgress");
        await Move(c, "WaitingOnCustomer");

        var pauses = await db.CaseSlaPauses.AsNoTracking().Where(p => p.CaseId == c.Id).OrderBy(p => p.StartedAt).ToListAsync();
        Assert.Equal(2, pauses.Count);
        Assert.NotNull(pauses[0].EndedAt);
        Assert.Equal("StatusChange", pauses[0].EndReason);
        Assert.Null(pauses[1].EndedAt);
        var total = (await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id)).SlaTotalPausedMinutes;
        Assert.Equal(total, pauses.Sum(p => p.PausedMinutes ?? 0));
    }

    [Fact]
    public async Task Entering_a_status_records_when()
    {
        var c = AddCase(CaseStatus.InProgress);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await Move(c, "WaitingOnCustomer");

        Assert.True((await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id)).StatusEnteredAt >= before);
    }

    // ------------------------------------------------------------------------------------------------ pause limit

    [Fact]
    public async Task Below_the_limit_nothing_happens()
    {
        var key = await LimitedStatus("ResumeSla", minutes: 120);
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);
        await Age(c, 30);

        Assert.Equal(0, await Limits().ApplyAsync(clock, DateTime.UtcNow));
        Assert.NotNull((await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id)).SlaPausedAt);
    }

    [Fact]
    public async Task Resume_at_the_limit_restarts_the_clock_and_keeps_the_status_once()
    {
        var key = await LimitedStatus("ResumeSla");
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);
        await Age(c, 90);

        Assert.Equal(1, await Limits().ApplyAsync(clock, DateTime.UtcNow));
        Assert.Equal(0, await Limits().ApplyAsync(clock, DateTime.UtcNow));   // once

        var after = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        Assert.Null(after.SlaPausedAt);
        Assert.Equal(key, after.StatusKey);
        Assert.True(after.SlaTotalPausedMinutes >= 89);
        var pause = await db.CaseSlaPauses.AsNoTracking().SingleAsync(p => p.CaseId == c.Id);
        Assert.Equal("PauseLimit", pause.EndReason);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.ActionType == "SLA_PAUSE_LIMIT" && e.Message.StartsWith("[Pause limit] SLA resumed")));
    }

    [Fact]
    public async Task Move_at_the_limit_puts_the_case_in_the_chosen_status()
    {
        var key = await LimitedStatus("MoveToStatus", "InProgress");
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);
        await Age(c, 90);

        await Limits().ApplyAsync(clock, DateTime.UtcNow);

        var after = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        Assert.Equal(CaseStatus.InProgress, after.Status);
        Assert.Null(after.StatusKey);
        Assert.Null(after.SlaPausedAt);
    }

    [Fact]
    public async Task Notify_at_the_limit_tells_the_holder_and_keeps_the_clock_paused()
    {
        var key = await LimitedStatus("NotifyOnly");
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);
        await Age(c, 90);
        var notifications = new Mock<INotificationService>();

        await new PauseLimitService(db, notifications.Object, NullLogger<PauseLimitService>.Instance).ApplyAsync(clock, DateTime.UtcNow);

        Assert.NotNull((await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id)).SlaPausedAt);
        notifications.Verify(n => n.CreateNotificationAsync(agent.Id, It.IsAny<string>(), "SLA pause limit reached", It.IsAny<string>(),
            c.Id, c.CaseNumber, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Once);
    }

    // ------------------------------------------------------------------------------------------------ dashboard

    [Fact]
    public async Task The_dashboard_counts_each_status_including_custom_ones()
    {
        var key = await LimitedStatus("ResumeSla");
        AddCase(CaseStatus.InProgress);
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);
        var dashboard = new DashboardService(db, new FixedClock(clock));

        var summary = await dashboard.GetSummaryAsync(new DashboardQuery(null, null, null, null, null, null, null, null, null));

        Assert.Contains(summary.CasesByStatus, s => s.Key == "InProgress" && s.Count == 1);
        Assert.Contains(summary.CasesByStatus, s => s.Key == key && s.Label == "Pending documents" && s.Count == 1);
        Assert.Equal(1, summary.WaitingOnCustomerCases);   // the tiles count by category
    }
}
