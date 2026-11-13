using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// Taking people off a team (a member, or the lead) with a reason, team names that must be unique, a deactivated team
/// that comes back handling the department chosen for it, and what happens when a person is deleted in the Host.
/// </summary>
/// <remarks>
/// A person deleted in the Host can no longer work the cases they own, and nothing else would notice: those cases would sit
/// with someone who can never open them. They must move to the team lead and the lead and Supervisors must be told — and a
/// person the Host merely does not know (a row from another Host database) must never be treated as deleted, or one
/// misconfigured environment would strip every team of its agents.
/// </remarks>
public class TeamLeadRemovalAndDepartedUserTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Guid lead = Guid.NewGuid(), ann = Guid.NewGuid(), boss = Guid.NewGuid();
    private readonly Guid service = Guid.NewGuid(), fraud = Guid.NewGuid();
    private readonly Guid serviceDept = Guid.NewGuid();

    private sealed class RecordingNotifications : INotificationService
    {
        public List<(Guid Recipient, Guid? CaseId)> Sent { get; } = new();
        public Task CreateNotificationAsync(Guid recipientUserId, string type, string title, string message, Guid? caseId = null, string? caseNumber = null, string priority = "High", int reminderCount = 0, CancellationToken ct = default, string? eventKey = null)
        { Sent.Add((recipientUserId, caseId)); return Task.CompletedTask; }
        public Task<PagedResponseDto<NotificationDto>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 15, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkAsReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkAllAsReadAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteNotificationAsync(Guid notificationId, Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PublishSlaBreachNotificationsAsync(IReadOnlyList<SlaBreachCandidate> breached, DateTime now, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default) => Task.FromResult(0);
        public Task CreateConfigChangedNotificationAsync(string title, string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateBulkReassignedNotificationAsync(Guid recipientUserId, IReadOnlyList<(Guid CaseId, string CaseNumber)> cases, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedStatuses(Dictionary<string, string> answer) : IHostUserStatusClient
    {
        public Task<IReadOnlyDictionary<string, string>?> StatusesAsync(IReadOnlyCollection<string> hostUserIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(answer);
    }

    public TeamLeadRemovalAndDepartedUserTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"lead-removal-{Guid.NewGuid()}").Options;
        db = new AppDbContext(options);

        User U(Guid id, string name, string role) => new() { Id = id, Name = name, Email = $"{id:N}@x.test", Role = role, ExternalUserId = $"host-{name}", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(U(lead, "Lena", "Agent"), U(ann, "Ann", "Agent"), U(boss, "Sam", "Supervisor"));
        db.Teams.AddRange(
            new Team { Id = service, Name = "Service Team", Code = "ST", OwnerId = lead, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Team { Id = fraud, Name = "Fraud", Code = "FD", IsActive = false, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.AddRange(Member(service, lead, false), Member(service, ann));
        db.Departments.Add(new Department { Id = serviceDept, Name = "Fraud & Disputes", Code = "FDD", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static TeamMember Member(Guid team, Guid user, bool assignable = true) => new()
    {
        Id = Guid.NewGuid(), TeamId = team, UserId = user, MemberRole = "Agent", IsActive = true, IsAssignable = assignable, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
    };

    private TeamService Teams()
    {
        var routing = new Mock<IRoutingEngineService>();
        routing.Setup(r => r.GetAssignmentConfigAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssignmentConfigDto { Algorithm = "RoundRobin", MaxConcurrentCapacity = 5 });
        return new TeamService(db, routing.Object, new TeamSettingsService(db));
    }

    private Guid OpenCase(Guid owner)
    {
        var id = Guid.NewGuid();
        db.Cases.Add(new Case { Id = id, CaseNumber = $"C-{Guid.NewGuid():N}"[..10], Title = "t", TeamId = service, OwnerId = owner, Status = CaseStatus.Open, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return id;
    }

    [Fact]
    public async Task Removing_the_lead_leaves_the_team_without_one_and_records_the_reason()
    {
        var team = await Teams().RemoveLeadAsync(service, "Moved to another branch", ann);

        Assert.Null(team.TeamLeadId);
        Assert.DoesNotContain(team.Members, m => m.UserId == lead);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.ActionType == "REMOVE_LEAD" && e.NewValue == "Moved to another branch"));
    }

    [Fact]
    public async Task Removing_a_member_records_the_reason_and_the_lead_cannot_be_removed_as_a_plain_member()
    {
        await Teams().RemoveMemberAsync(service, ann, lead, default, "Left the bank");

        Assert.False(await db.TeamMembers.AnyAsync(m => m.TeamId == service && m.UserId == ann));
        Assert.True(await db.CaseEvents.AnyAsync(e => e.ActionType == "REMOVE_MEMBER" && e.NewValue == "Left the bank"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Teams().RemoveMemberAsync(service, lead, ann, default, "x"));
    }

    [Fact]
    public async Task A_team_name_already_taken_is_refused_whatever_its_case_or_spacing()
    {
        var error = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().CreateTeamAsync(new CreateTeamDto { Name = "  service   TEAM " }, lead));

        Assert.Contains(error.Errors, e => e.Field == "name");
    }

    [Fact]
    public async Task A_code_made_from_the_name_never_clashes_with_another_team()
    {
        var created = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "Super Team" }, lead);

        Assert.Equal("ST2", created.Code);
    }

    [Fact]
    public async Task An_inactive_team_activated_again_takes_on_the_department_chosen_for_it()
    {
        var team = await Teams().ToggleTeamStatusAsync(fraud, lead, default, new[] { serviceDept });

        Assert.True(team.IsActive);
        Assert.Equal(new[] { "Fraud & Disputes" }, team.Departments.Select(d => d.Name));
    }

    [Fact]
    public async Task A_person_deleted_in_the_host_hands_their_open_cases_to_the_team_lead_and_the_lead_and_supervisors_are_told()
    {
        var caseId = OpenCase(ann);
        var sent = new RecordingNotifications();
        var release = new DepartedUserService(db, new FakeOrgDirectory(db, supervisorRoles: new[] { "Supervisor" }), sent, NullLogger<DepartedUserService>.Instance);
        var monitor = new DepartedUserMonitor(db, new FixedStatuses(new() { ["host-Ann"] = "Deleted", ["host-Lena"] = "Active" }), release);

        var moved = await monitor.RunOnceAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal(1, moved);
        Assert.Equal(lead, (await db.Cases.SingleAsync(c => c.Id == caseId)).OwnerId);
        Assert.False((await db.Users.SingleAsync(u => u.Id == ann)).IsActive);
        Assert.False(await db.TeamMembers.AnyAsync(m => m.UserId == ann && m.IsActive));
        Assert.Equal(new[] { lead, boss }.OrderBy(x => x), sent.Sent.Where(s => s.CaseId == caseId).Select(s => s.Recipient).OrderBy(x => x));
    }

    [Fact]
    public async Task A_person_the_host_does_not_know_is_never_treated_as_deleted()
    {
        var caseId = OpenCase(ann);
        var release = new DepartedUserService(db, new FakeOrgDirectory(db), new RecordingNotifications(), NullLogger<DepartedUserService>.Instance);
        var monitor = new DepartedUserMonitor(db, new FixedStatuses(new()), release);

        var moved = await monitor.RunOnceAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal(0, moved);
        Assert.Equal(ann, (await db.Cases.SingleAsync(c => c.Id == caseId)).OwnerId);
        Assert.True((await db.Users.SingleAsync(u => u.Id == ann)).IsActive);
    }

    [Fact]
    public async Task A_deleted_team_lead_leaves_their_team_and_their_cases_go_to_a_supervisor()
    {
        var caseId = OpenCase(lead);
        var release = new DepartedUserService(db, new FakeOrgDirectory(db, supervisorRoles: new[] { "Supervisor" }), new RecordingNotifications(), NullLogger<DepartedUserService>.Instance);

        await release.ReleaseAsync(new[] { lead }, default);

        db.ChangeTracker.Clear();
        Assert.Null((await db.Teams.SingleAsync(t => t.Id == service)).OwnerId);
        Assert.Equal(boss, (await db.Cases.SingleAsync(c => c.Id == caseId)).OwnerId);
    }

    [Fact]
    public async Task With_no_lead_and_no_supervisor_an_administrator_holds_the_cases_and_is_told()
    {
        var admin = Guid.NewGuid();
        db.Users.Add(new User { Id = admin, Name = "Ada Admin", Email = "ada@x.test", Role = "Administrator", ExternalUserId = "host-Ada", IsActive = true, CreatedAt = DateTime.UtcNow });
        (await db.Teams.SingleAsync(t => t.Id == service)).OwnerId = null;   // the team has no lead
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var caseId = OpenCase(ann);
        var sent = new RecordingNotifications();
        var release = new DepartedUserService(db, new FakeOrgDirectory(db, administratorRoles: new[] { "Administrator" }), sent, NullLogger<DepartedUserService>.Instance);

        var moved = await release.ReleaseAsync(new[] { ann }, default);

        db.ChangeTracker.Clear();
        Assert.Equal(1, moved);
        Assert.Equal(admin, (await db.Cases.SingleAsync(c => c.Id == caseId)).OwnerId);
        Assert.Contains(sent.Sent, s => s.Recipient == admin && s.CaseId == caseId);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == caseId && e.Message.Contains("an administrator")));
    }
}
