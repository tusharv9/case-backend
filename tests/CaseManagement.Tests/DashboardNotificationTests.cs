using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseManagement.Tests;

public class DashboardDateRangeTests
{
    private static readonly TimeZoneInfo Kl = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kuala_Lumpur");   // UTC+8, no DST

    // Wednesday 2026-10-07 02:00 UTC = 10:00 in Kuala Lumpur.
    private static readonly DateTime Now = new(2026, 10, 7, 2, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Today_IsTheBusinessDay_NotTheUtcDay()
    {
        var (start, end) = DashboardService.DateRange("today", null, null, Now, Kl);
        Assert.Equal(Utc(2026, 10, 6, 16), start);     // 00:00 Oct 7 in KL
        Assert.Equal(Utc(2026, 10, 7, 16), end);

        // Just after midnight in KL it is still "yesterday" in UTC — the range must already be the new day.
        var (s2, _) = DashboardService.DateRange("today", null, null, Utc(2026, 10, 6, 16).AddMinutes(5), Kl);
        Assert.Equal(Utc(2026, 10, 6, 16), s2);
    }

    [Fact]
    public void Weeks_StartOnMonday()
    {
        Assert.Equal((Utc(2026, 10, 4, 16), Utc(2026, 10, 11, 16)), Pair(DashboardService.DateRange("this_week", null, null, Now, Kl)));   // Mon Oct 5 → Mon Oct 12
        Assert.Equal((Utc(2026, 9, 27, 16), Utc(2026, 10, 4, 16)), Pair(DashboardService.DateRange("last_week", null, null, Now, Kl)));
    }

    [Fact]
    public void Months_QuartersAndYears()
    {
        Assert.Equal((Utc(2026, 9, 30, 16), Utc(2026, 10, 31, 16)), Pair(DashboardService.DateRange("this_month", null, null, Now, Kl)));
        Assert.Equal((Utc(2026, 8, 31, 16), Utc(2026, 9, 30, 16)), Pair(DashboardService.DateRange("last_month", null, null, Now, Kl)));
        Assert.Equal((Utc(2026, 9, 30, 16), Utc(2026, 12, 31, 16)), Pair(DashboardService.DateRange("this_quarter", null, null, Now, Kl)));
        Assert.Equal((Utc(2025, 12, 31, 16), Utc(2026, 12, 31, 16)), Pair(DashboardService.DateRange("this_year", null, null, Now, Kl)));
    }

    [Fact]
    public void Custom_IsInclusiveOfTheEndDate_AndAllMeansNoLimit()
    {
        var (s, e) = DashboardService.DateRange("custom", "2026-10-01", "2026-10-03", Now, Kl);
        Assert.Equal(Utc(2026, 9, 30, 16), s);
        Assert.Equal(Utc(2026, 10, 3, 16), e);                       // through the end of Oct 3
        Assert.Equal((null, null), Pair2(DashboardService.DateRange("all", null, null, Now, Kl)));
        Assert.Equal((null, null), Pair2(DashboardService.DateRange(null, null, null, Now, Kl)));
    }

    [Fact]
    public void AnUnknownRange_IsAnError_NotSilentlyIgnored()
    {
        Assert.Throws<ArgumentException>(() => DashboardService.DateRange("yesterday-ish", null, null, Now, Kl));
    }

    private static (DateTime?, DateTime?) Pair2((DateTime? S, DateTime? E) r) => (r.S, r.E);
    private static (DateTime, DateTime) Pair((DateTime? S, DateTime? E) r) => (r.S!.Value, r.E!.Value);
}

public class NotificationPolicyTests
{
    private static NotificationService Service(AppDbContext ctx, NotificationOptions? options = null, IPermissionProvider? permissions = null) =>
        new(ctx, permissions ?? new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions())), Options.Create(options ?? new NotificationOptions()));

    private static async Task<(TempDatabase Db, Guid Admin, Guid Agent, Guid Supervisor)> WorldAsync()
    {
        var db = await TempDatabase.CreateAsync();
        await using var ctx = db.NewContext();
        await ctx.Database.MigrateAsync();
        Guid admin = Guid.NewGuid(), agent = Guid.NewGuid(), sup = Guid.NewGuid();
        ctx.Users.AddRange(
            new User { Id = admin, Name = "Admin", Email = "a@x.test", Role = "Admin", CreatedAt = DateTime.UtcNow },
            new User { Id = agent, Name = "Agent", Email = "g@x.test", Role = "Agent", CreatedAt = DateTime.UtcNow },
            new User { Id = sup, Name = "Sup", Email = "s@x.test", Role = "Supervisor", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        return (db, admin, agent, sup);
    }

    [PostgresFact]
    public async Task ConfigurationChanges_AreOnlyAnnouncedToPeopleWhoCanChangeConfiguration()
    {
        var (db, admin, agent, sup) = await WorldAsync();
        await using var _ = db;
        await using var ctx = db.NewContext();

        await Service(ctx).CreateConfigChangedNotificationAsync("Updated", "Case type X changed");

        var recipients = await ctx.Notifications.Where(n => n.Type == "CONFIG_CHANGED").Select(n => n.RecipientUserId).ToListAsync();
        Assert.Equal(new[] { admin }, recipients);          // Admin holds config.manage; Agent and Supervisor do not
    }

    [PostgresFact]
    public async Task ADeactivatedAdministrator_IsNotNotified()
    {
        var (db, admin, _, _) = await WorldAsync();
        await using var _db = db;
        await using var ctx = db.NewContext();
        await ctx.Users.Where(u => u.Id == admin).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));

        await Service(ctx).CreateConfigChangedNotificationAsync("Updated", "x");
        Assert.Empty(await ctx.Notifications.ToListAsync());
    }

    [PostgresFact]
    public async Task RetentionRemovesOldReadNotificationsSoonerThanUnreadOnes()
    {
        var (db, _, agent, _) = await WorldAsync();
        await using var _db = db;
        await using var ctx = db.NewContext();
        var now = DateTime.UtcNow;

        NotificationItem N(string type, bool read, int ageDays) => new() { RecipientUserId = agent, Type = type, Title = type, Message = "m", IsRead = read, CreatedAt = now.AddDays(-ageDays) };
        ctx.Notifications.AddRange(
            N("recent-read", true, 5), N("old-read", true, 45), N("old-unread", false, 45), N("ancient-unread", false, 400));
        await ctx.SaveChangesAsync();

        // (The audit base class stamps CreatedAt when saving, so set the ages explicitly.)
        foreach (var (type, age) in new[] { ("recent-read", 5), ("old-read", 45), ("old-unread", 45), ("ancient-unread", 400) })
            await ctx.Notifications.Where(n => n.Type == type).ExecuteUpdateAsync(u => u.SetProperty(n => n.CreatedAt, now.AddDays(-age)));

        var removed = await Service(ctx, new NotificationOptions { ReadRetentionDays = 30, MaxAgeDays = 180 }).PurgeExpiredAsync(now);

        Assert.Equal(2, removed);
        Assert.Equal(new[] { "old-unread", "recent-read" }, (await ctx.Notifications.Select(n => n.Type).ToListAsync()).OrderBy(t => t).ToArray());
    }

    [PostgresFact]
    public async Task BreachReminderPolicy_ComesFromConfiguration()
    {
        var (db, _, agent, _) = await WorldAsync();
        await using var _db = db;
        await using var ctx = db.NewContext();
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = "N", PhoneNumber = "+60111111111", CreatedAt = DateTime.UtcNow };
        var dept = new Department { Id = Guid.NewGuid(), Name = "D", Code = "D", CreatedAt = DateTime.UtcNow };
        ctx.AddRange(customer, dept);
        await ctx.SaveChangesAsync();
        var c = new Case { Id = Guid.NewGuid(), CaseNumber = "T-1", Title = "t", CustomerId = customer.Id, DepartmentId = dept.Id, OwnerId = agent, Status = CaseStatus.Open, Severity = "High", SlaStartTime = DateTime.UtcNow.AddHours(-5), CreatedAt = DateTime.UtcNow };
        ctx.Cases.Add(c);
        await ctx.SaveChangesAsync();

        var candidate = new[] { new SlaBreachCandidate(c.Id, c.CaseNumber, c.Title, agent, c.SlaStartTime) };
        var svc = Service(ctx, new NotificationOptions { BreachReminderCooldownMinutes = 10, BreachMaxReminders = 1, BreachGroupingThreshold = 0 });

        await svc.PublishSlaBreachNotificationsAsync(candidate, DateTime.UtcNow);                        // first: SLA_BREACHED
        await svc.PublishSlaBreachNotificationsAsync(candidate, DateTime.UtcNow.AddMinutes(5));          // inside the cooldown: nothing
        await svc.PublishSlaBreachNotificationsAsync(candidate, DateTime.UtcNow.AddMinutes(15));         // one reminder
        await svc.PublishSlaBreachNotificationsAsync(candidate, DateTime.UtcNow.AddMinutes(60));         // maximum reached: nothing more

        Assert.Equal(new[] { "SLA_BREACHED", "SLA_BREACH_REMINDER" }, (await ctx.Notifications.OrderBy(n => n.CreatedAt).Select(n => n.Type).ToListAsync()).ToArray());
    }
}
