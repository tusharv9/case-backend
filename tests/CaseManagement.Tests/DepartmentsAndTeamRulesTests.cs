using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// Departments and teams as independent records joined by a mapping (one team → many departments, one department → one
/// team), the team rules added with Team Configuration, case-view recording, and the Multi-select field value.
/// </summary>
/// <remarks>
/// The mapping decides which team every case of a department goes to, and there is no default or fallback team behind it, so
/// a department must never end up on two teams, a conflicting save must name the team that already holds the department
/// instead of quietly moving it, and a team's own departments must never count as a conflict when it is edited. A team that
/// is deactivated releases its departments rather than handing them to anyone. The other team rules are refusals that protect existing
/// teams, so tightening one must name what breaks it instead of changing anything, and the team on/off switch must obey them
/// like every other save. A case view is an audit record that a busy agent creates constantly, so it must be written once per
/// window, not once per click. A Multi-select value travels as JSON text between browser, validator and storage, so a malformed
/// one must be told apart from an empty one.
/// </remarks>
public class DepartmentsAndTeamRulesTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Guid lead = Guid.NewGuid(), ann = Guid.NewGuid(), cy = Guid.NewGuid();
    private readonly Guid cards = Guid.NewGuid(), loans = Guid.NewGuid(), wealth = Guid.NewGuid();
    private readonly Guid retail = Guid.NewGuid(), corporate = Guid.NewGuid();

    public DepartmentsAndTeamRulesTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"departments-{Guid.NewGuid()}").Options;
        db = new AppDbContext(options);

        User U(Guid id, string name) => new() { Id = id, Name = name, Email = $"{id:N}@x.test", Role = "Agent", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(U(lead, "Lena Lead"), U(ann, "Ann Agent"), U(cy, "Cy Agent"));
        db.Teams.AddRange(
            new Team { Id = cards, Name = "Cards", Code = "CD", OwnerId = lead, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Team { Id = loans, Name = "Loans", Code = "LN", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Team { Id = wealth, Name = "Wealth", Code = "WL", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.AddRange(Member(cards, lead, false), Member(cards, ann), Member(loans, cy));
        db.Departments.AddRange(
            new Department { Id = retail, Name = "Retail Banking", Code = "RB", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Department { Id = corporate, Name = "Corporate Banking", Code = "CB", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static TeamMember Member(Guid team, Guid user, bool assignable = true) => new()
    {
        Id = Guid.NewGuid(), TeamId = team, UserId = user, MemberRole = "Agent", IsActive = true, IsAssignable = assignable, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
    };

    private void Link(Guid team, Guid department)
    {
        db.TeamDepartments.Add(new TeamDepartment { TeamId = team, DepartmentId = department });
        db.SaveChanges();
    }

    private TeamService Teams()
    {
        var routing = new Mock<IRoutingEngineService>();
        routing.Setup(r => r.GetAssignmentConfigAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssignmentConfigDto { Algorithm = "RoundRobin", MaxConcurrentCapacity = 5 });
        return new TeamService(db, routing.Object, new TeamSettingsService(db));
    }

    private static UpdateTeamSettingsDto Rules(bool empty = true, bool leadElsewhere = true, bool requireLead = false, bool deactivateWithOpen = true) => new()
    {
        AllowMultipleTeamMembership = true, AllowMultipleTeamLeads = true, AllowMultipleDepartments = true,
        AllowTeamsWithoutMembers = empty, AllowLeadInOtherTeams = leadElsewhere, RequireTeamLead = requireLead, AllowDeactivateWithOpenCases = deactivateWithOpen,
    };

    private void OpenCase(Guid team)
    {
        db.Cases.Add(new Case { Id = Guid.NewGuid(), CaseNumber = $"C-{Guid.NewGuid():N}"[..10], Title = "t", TeamId = team, OwnerId = ann, Status = CaseStatus.Open, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    // ------------------------------------------------------------------------------------------------ team–department mapping

    [Fact]
    public async Task One_team_can_be_created_with_several_departments()
    {
        var created = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "Banking Desk", DepartmentIds = new() { retail, corporate } }, lead);

        Assert.Equal(new[] { "Corporate Banking", "Retail Banking" }, created.Departments.Select(d => d.Name).OrderBy(n => n));
        Assert.Equal(2, await db.TeamDepartments.CountAsync(l => l.TeamId == created.Id));
    }

    [Fact]
    public async Task A_department_already_assigned_to_a_team_cannot_be_given_to_another_and_the_refusal_names_that_team()
    {
        Link(cards, retail);
        db.ChangeTracker.Clear();

        var onCreate = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "Second", DepartmentIds = new() { retail } }, lead));
        var onEdit = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().UpdateTeamAsync(loans, new UpdateTeamDto { DepartmentIds = new() { retail, corporate } }, lead));

        Assert.Contains(onCreate.Errors, e => e.Field == TeamFormFields.Departments && e.Message.Contains("Retail Banking") && e.Message.Contains("'Cards'"));
        Assert.Contains(onEdit.Errors, e => e.Field == TeamFormFields.Departments && e.Message.Contains("Retail Banking") && e.Message.Contains("'Cards'"));
        Assert.Equal(cards, (await db.TeamDepartments.SingleAsync(l => l.DepartmentId == retail)).TeamId);
        Assert.False(await db.TeamDepartments.AnyAsync(l => l.DepartmentId == corporate));
    }

    [Fact]
    public async Task Editing_a_team_keeps_its_own_departments_without_a_conflict()
    {
        Link(cards, retail);
        db.ChangeTracker.Clear();

        var updated = await Teams().UpdateTeamAsync(cards, new UpdateTeamDto { DepartmentIds = new() { retail, corporate }, Function = "Retail and corporate" }, lead);

        Assert.Equal(new[] { "Corporate Banking", "Retail Banking" }, updated.Departments.Select(d => d.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task A_department_is_assigned_to_a_team_explicitly_and_is_never_moved_from_the_team_that_holds_it()
    {
        var assigned = await new DepartmentService(db).AssignTeamAsync(corporate, loans, lead);
        Link(cards, retail);

        var moved = await Assert.ThrowsAsync<FieldValidationException>(() => new DepartmentService(db).AssignTeamAsync(retail, loans, lead));
        var cleared = await new DepartmentService(db).AssignTeamAsync(corporate, null, lead);

        Assert.Equal("Loans", assigned.Team?.Name);
        Assert.Contains(moved.Errors, e => e.Field == "teamId" && e.Message.Contains("'Cards'"));
        Assert.Equal(cards, (await db.TeamDepartments.SingleAsync(l => l.DepartmentId == retail)).TeamId);
        Assert.Null(cleared.Team);
        Assert.Equal(2, await db.CaseEvents.CountAsync(e => e.Module == "Departments" && e.ActionType == "UPDATE"));
    }

    [Fact]
    public async Task An_inactive_team_cannot_be_assigned_a_department()
    {
        (await db.Teams.SingleAsync(t => t.Id == wealth)).IsActive = false;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<FieldValidationException>(() => new DepartmentService(db).AssignTeamAsync(retail, wealth, lead));

        Assert.Contains(error.Errors, e => e.Field == "teamId" && e.Message.Contains("inactive"));
        Assert.False(await db.TeamDepartments.AnyAsync());
    }

    [Fact]
    public async Task Deactivating_a_team_releases_its_departments_unassigned_and_keeps_its_cases()
    {
        Link(cards, retail);
        Link(cards, corporate);
        db.Cases.Add(new Case { Id = Guid.NewGuid(), CaseNumber = "C-9", Title = "t", TeamId = cards, DepartmentId = retail, OwnerId = ann, Status = CaseStatus.Resolved, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Teams().ToggleTeamStatusAsync(cards, lead);

        Assert.False(await db.TeamDepartments.AnyAsync());
        Assert.True(await db.Cases.AnyAsync(c => c.TeamId == cards && c.DepartmentId == retail));
        Assert.True(await db.CaseEvents.AnyAsync(e => e.Message.Contains("released Corporate Banking, Retail Banking")));
    }

    [Fact]
    public async Task With_one_department_per_team_a_second_department_is_refused()
    {
        var rules = Rules(); rules.AllowMultipleDepartments = false;
        await new TeamSettingsService(db).SaveAsync(rules, lead);
        db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "Two Desks", DepartmentIds = new() { retail, corporate } }, lead));

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.Departments);
    }

    [Fact]
    public async Task A_department_that_cases_belong_to_cannot_be_deleted()
    {
        db.Cases.Add(new Case { Id = Guid.NewGuid(), CaseNumber = "C-1", Title = "t", TeamId = cards, DepartmentId = corporate, OwnerId = ann, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DepartmentService(db).DeleteAsync(corporate, lead));

        Assert.Contains("Deactivate it instead", error.Message);
        Assert.True(await db.Departments.AnyAsync(d => d.Id == corporate));
    }

    [Fact]
    public async Task Departments_are_reordered_only_from_the_complete_current_list()
    {
        var stale = await Assert.ThrowsAsync<FieldValidationException>(() => new DepartmentService(db).ReorderAsync(new[] { corporate }, lead));
        var ordered = await new DepartmentService(db).ReorderAsync(new[] { corporate, retail }, lead);

        Assert.Contains(stale.Errors, e => e.Field == "ids");
        Assert.Equal(new[] { "Corporate Banking", "Retail Banking" }, ordered.Select(d => d.Name));
    }

    // ------------------------------------------------------------------------------------------------ team rules

    [Fact]
    public async Task Requiring_a_lead_is_refused_while_an_active_team_has_none_and_names_it()
    {
        var error = await Assert.ThrowsAsync<FieldValidationException>(() => new TeamSettingsService(db).SaveAsync(Rules(requireLead: true), lead));

        Assert.Contains(error.Errors, e => e.Field == "requireTeamLead" && e.Message.Contains("Loans") && e.Message.Contains("Wealth"));
        Assert.False((await new TeamSettingsService(db).GetAsync()).RequireTeamLead);
    }

    [Fact]
    public async Task With_teams_needing_members_a_team_saved_with_only_its_lead_is_refused()
    {
        db.Teams.Remove(await db.Teams.SingleAsync(d => d.Id == wealth));   // a team with no members at all
        await db.SaveChangesAsync();
        await new TeamSettingsService(db).SaveAsync(Rules(empty: false), lead);
        db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "Solo", TeamLeadId = cy }, lead));

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.Members);
    }

    [Fact]
    public async Task With_leads_kept_to_their_own_team_a_lead_cannot_be_added_to_another_team()
    {
        await new TeamSettingsService(db).SaveAsync(Rules(leadElsewhere: false), lead);
        db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().UpdateTeamAsync(loans, new UpdateTeamDto { Members = new() { new TeamMemberInputDto { UserId = cy }, new TeamMemberInputDto { UserId = lead } } }, lead));

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.Members && e.Message.Contains("Lena Lead (leads Cards)"));
    }

    [Fact]
    public async Task With_open_cases_blocking_deactivation_neither_the_switch_nor_an_edit_can_deactivate_the_team()
    {
        OpenCase(loans);
        await new TeamSettingsService(db).SaveAsync(Rules(deactivateWithOpen: false), lead);
        db.ChangeTracker.Clear();

        var viaSwitch = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().ToggleTeamStatusAsync(loans, lead));
        db.ChangeTracker.Clear();
        var viaEdit = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().UpdateTeamAsync(loans, new UpdateTeamDto { IsActive = false }, lead));

        Assert.Contains("1 open case", viaSwitch.Errors.Single().Message);
        Assert.Equal("isActive", viaEdit.Errors.Single().Field);
        Assert.True(await db.Teams.Where(d => d.Id == loans).Select(d => d.IsActive).SingleAsync());
    }

    // ------------------------------------------------------------------------------------------------ case views

    [Fact]
    public async Task Opening_a_case_again_within_the_window_records_one_view_and_another_person_records_their_own()
    {
        var caseId = Guid.NewGuid();
        db.Cases.Add(new Case { Id = caseId, CaseNumber = "C-9", Title = "t", TeamId = cards, OwnerId = ann, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await CaseViewLog.RecordAsync(db, caseId, ann, NullLogger.Instance);
        await CaseViewLog.RecordAsync(db, caseId, ann, NullLogger.Instance);
        await CaseViewLog.RecordAsync(db, caseId, cy, NullLogger.Instance);

        var views = await db.CaseEvents.Where(e => e.EventType == EventType.View).ToListAsync();
        Assert.Equal(2, views.Count);
        Assert.Equal(new[] { ann, cy }.OrderBy(x => x), views.Select(v => v.UserId!.Value).OrderBy(x => x));
    }

    // ------------------------------------------------------------------------------------------------ multi-select

    [Theory]
    [InlineData("", 0)]
    [InlineData("[]", 0)]
    [InlineData("[\"Email\",\"email\",\" \",\"Chat\"]", 2)]
    public void A_multi_select_value_drops_blanks_and_duplicates(string value, int expected)
    {
        Assert.Equal(expected, MultiSelectValues.Parse(value)!.Count);
    }

    [Fact]
    public void A_multi_select_value_that_is_not_a_list_is_not_a_value()
    {
        Assert.Null(MultiSelectValues.Parse("Email, Chat"));
    }
}
