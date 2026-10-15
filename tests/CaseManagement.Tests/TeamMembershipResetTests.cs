using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Teams are being re-staffed from the Host directory. The reset that clears the old sample people must remove only what
/// belongs to people who are not Host users, keep every team and all history, and never touch a Host user's membership.
/// </summary>
public class TeamMembershipResetTests
{
    private const string HostUserId = "11111111-1111-4111-8111-111111111111";

    private static async Task<(TempDatabase Db, Guid Team, Guid HostUser, Guid Sample1, Guid Sample2)> SeedAsync()
    {
        var db = await TempDatabase.CreateAsync();
        using (var warm = new AppUnderTest(db.ConnectionString)) await warm.ReadyClientAsync();
        await using var ctx = db.NewContext();
        var team = new Department { Id = Guid.NewGuid(), Name = "Loans", Code = "LN", CreatedAt = DateTime.UtcNow };
        var host = new User { Id = Guid.NewGuid(), Name = "Host Person", Email = "host@bank.test", ExternalUserId = HostUserId, CreatedAt = DateTime.UtcNow };
        var s1 = new User { Id = Guid.NewGuid(), Name = "Sample Lead", Email = "lead@sample.test", ExternalUserId = Guid.NewGuid().ToString(), CreatedAt = DateTime.UtcNow };
        var s2 = new User { Id = Guid.NewGuid(), Name = "Sample Agent", Email = "agent@sample.test", ExternalUserId = Guid.NewGuid().ToString(), CreatedAt = DateTime.UtcNow };
        ctx.Users.AddRange(host, s1, s2);
        ctx.Departments.Add(team);
        await ctx.SaveChangesAsync();
        team.OwnerId = s1.Id;
        ctx.TeamMembers.AddRange(
            new TeamMember { Id = Guid.NewGuid(), DepartmentId = team.Id, UserId = s1.Id, IsActive = true, CreatedAt = DateTime.UtcNow },
            new TeamMember { Id = Guid.NewGuid(), DepartmentId = team.Id, UserId = s2.Id, IsActive = true, CreatedAt = DateTime.UtcNow },
            new TeamMember { Id = Guid.NewGuid(), DepartmentId = team.Id, UserId = host.Id, IsActive = true, CreatedAt = DateTime.UtcNow });
        ctx.TeamAssignmentPointers.Add(new TeamAssignmentPointer { DepartmentId = team.Id, LastAssignedUserId = s2.Id });
        await ctx.SaveChangesAsync();
        return (db, team.Id, host.Id, s1.Id, s2.Id);
    }

    [PostgresFact]
    public async Task A_dry_run_reports_and_changes_nothing()
    {
        var (db, team, host, _, _) = await SeedAsync();
        await using var _db = db;
        await using var ctx = db.NewContext();

        var report = await new TeamMembershipResetService(ctx).RunAsync(new HashSet<string> { HostUserId }, apply: false, actorId: null);

        Assert.Equal(2, report.MembershipsRemoved);
        Assert.Equal(1, report.LeadsCleared);
        Assert.Equal(2, report.UsersDeactivated.Count);
        await using var check = db.NewContext();
        Assert.Equal(3, await check.TeamMembers.CountAsync());
        Assert.NotNull((await check.Departments.SingleAsync(d => d.Id == team)).OwnerId);
        Assert.True(await check.Users.AllAsync(u => u.IsActive));
    }

    [PostgresFact]
    public async Task Applying_clears_only_people_who_are_not_Host_users_and_keeps_teams_users_and_history()
    {
        var (db, team, host, sample1, sample2) = await SeedAsync();
        await using var _db = db;
        await using var ctx = db.NewContext();

        await new TeamMembershipResetService(ctx).RunAsync(new HashSet<string> { HostUserId }, apply: true, actorId: host);

        await using var check = db.NewContext();
        Assert.Equal(new[] { host }, await check.TeamMembers.Select(m => m.UserId).ToListAsync());   // the Host user's membership kept
        Assert.Null((await check.Departments.SingleAsync(d => d.Id == team)).OwnerId);
        Assert.Equal(0, await check.TeamAssignmentPointers.CountAsync());
        Assert.Equal(3, await check.Users.CountAsync());                                              // nobody deleted
        Assert.False((await check.Users.SingleAsync(u => u.Id == sample1)).IsActive);
        Assert.False((await check.Users.SingleAsync(u => u.Id == sample2)).IsActive);
        Assert.True((await check.Users.SingleAsync(u => u.Id == host)).IsActive);
        Assert.Equal(1, await check.Departments.CountAsync(d => d.Id == team));
        Assert.Equal(2, await check.CaseEvents.CountAsync(e => e.Module == "Teams" && (e.ActionType == "MEMBERSHIP_RESET" || e.ActionType == "DEACTIVATE_NON_HOST_USERS")));

        // Running it again changes nothing more.
        await using var again = db.NewContext();
        var second = await new TeamMembershipResetService(again).RunAsync(new HashSet<string> { HostUserId }, apply: true, actorId: host);
        Assert.Equal(0, second.MembershipsRemoved);
        Assert.Empty(second.UsersDeactivated);
    }

    [PostgresFact]
    public async Task Without_a_list_of_Host_users_nothing_is_done()
    {
        var (db, _, _, _, _) = await SeedAsync();
        await using var _db = db;
        await using var ctx = db.NewContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TeamMembershipResetService(ctx).RunAsync(new HashSet<string>(), apply: true, actorId: Guid.NewGuid()));
    }
}
