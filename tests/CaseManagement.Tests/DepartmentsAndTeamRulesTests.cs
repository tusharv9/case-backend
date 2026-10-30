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
/// Departments as their own master data (teams serve them; cases belong to one), the team rules added with Team
/// Configuration, case-view recording, and the Multi-select field value.
/// </summary>
/// <remarks>
/// A department's default team is where a case lands when no routing rule decides, so the fallback must be exact: the default
/// first, the only team next, and never an arbitrary pick among several. The new rules are refusals that protect existing
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
        db.Departments.AddRange(
            new Department { Id = cards, Name = "Cards", Code = "CD", OwnerId = lead, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Department { Id = loans, Name = "Loans", Code = "LN", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Department { Id = wealth, Name = "Wealth", Code = "WL", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.AddRange(Member(cards, lead, false), Member(cards, ann), Member(loans, cy));
        db.OrgDepartments.AddRange(
            new OrgDepartment { Id = retail, Name = "Retail Banking", Code = "RB", IsActive = true, CreatedAt = DateTime.UtcNow },
            new OrgDepartment { Id = corporate, Name = "Corporate Banking", Code = "CB", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static TeamMember Member(Guid team, Guid user, bool assignable = true) => new()
    {
        Id = Guid.NewGuid(), DepartmentId = team, UserId = user, MemberRole = "Agent", IsActive = true, IsAssignable = assignable, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
    };

    private void Link(Guid team, Guid department, bool isDefault = false)
    {
        db.TeamDepartments.Add(new TeamDepartment { TeamId = team, OrgDepartmentId = department, IsDefault = isDefault });
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
        db.Cases.Add(new Case { Id = Guid.NewGuid(), CaseNumber = $"C-{Guid.NewGuid():N}"[..10], Title = "t", DepartmentId = team, OwnerId = ann, Status = CaseStatus.Open, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    // ------------------------------------------------------------------------------------------------ default team

    [Fact]
    public async Task A_department_falls_back_to_its_default_team_before_any_other_serving_team()
    {
        Link(cards, retail);
        Link(loans, retail, isDefault: true);

        var fallback = await DepartmentTeams.ResolveFallbackAsync(db, retail);

        Assert.Equal(loans, fallback.TeamId);
    }

    [Fact]
    public async Task A_department_served_by_one_team_falls_back_to_it_and_one_served_by_several_without_a_default_has_no_fallback()
    {
        Link(cards, retail);
        Link(cards, corporate);
        Link(loans, corporate);

        var single = await DepartmentTeams.ResolveFallbackAsync(db, retail);
        var several = await DepartmentTeams.ResolveFallbackAsync(db, corporate);

        Assert.Equal(cards, single.TeamId);
        Assert.Null(several.TeamId);
        Assert.Contains("several teams", several.Reason);
    }

    [Fact]
    public async Task An_inactive_team_is_never_the_fallback_even_when_it_is_the_default()
    {
        Link(cards, retail, isDefault: true);
        (await db.Departments.SingleAsync(d => d.Id == cards)).IsActive = false;
        await db.SaveChangesAsync();

        var fallback = await DepartmentTeams.ResolveFallbackAsync(db, retail);

        Assert.Null(fallback.TeamId);
    }

    [Fact]
    public async Task Only_a_team_that_serves_the_department_can_be_its_default()
    {
        Link(cards, retail);

        var refused = await Assert.ThrowsAsync<FieldValidationException>(() => new OrgDepartmentService(db).SetDefaultTeamAsync(retail, loans, lead));
        var set = await new OrgDepartmentService(db).SetDefaultTeamAsync(retail, cards, lead);

        Assert.Contains(refused.Errors, e => e.Field == "teamId");
        Assert.True(set.Teams.Single().IsDefault);
    }

    [Fact]
    public async Task A_department_that_cases_belong_to_cannot_be_deleted()
    {
        db.Cases.Add(new Case { Id = Guid.NewGuid(), CaseNumber = "C-1", Title = "t", DepartmentId = cards, OrgDepartmentId = corporate, OwnerId = ann, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new OrgDepartmentService(db).DeleteAsync(corporate, lead));

        Assert.Contains("Deactivate it instead", error.Message);
        Assert.True(await db.OrgDepartments.AnyAsync(d => d.Id == corporate));
    }

    [Fact]
    public async Task Departments_are_reordered_only_from_the_complete_current_list()
    {
        var stale = await Assert.ThrowsAsync<FieldValidationException>(() => new OrgDepartmentService(db).ReorderAsync(new[] { corporate }, lead));
        var ordered = await new OrgDepartmentService(db).ReorderAsync(new[] { corporate, retail }, lead);

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
        db.Departments.Remove(await db.Departments.SingleAsync(d => d.Id == wealth));   // a team with no members at all
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
        Assert.True(await db.Departments.Where(d => d.Id == loans).Select(d => d.IsActive).SingleAsync());
    }

    // ------------------------------------------------------------------------------------------------ case views

    [Fact]
    public async Task Opening_a_case_again_within_the_window_records_one_view_and_another_person_records_their_own()
    {
        var caseId = Guid.NewGuid();
        db.Cases.Add(new Case { Id = caseId, CaseNumber = "C-9", Title = "t", DepartmentId = cards, OwnerId = ann, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await CaseViewLog.RecordAsync(db, caseId, ann, NullLogger.Instance);
        await CaseViewLog.RecordAsync(db, caseId, ann, NullLogger.Instance);
        await CaseViewLog.RecordAsync(db, caseId, cy, NullLogger.Instance);

        var views = await db.CaseEvents.Where(e => e.EventType == EventType.View).ToListAsync();
        Assert.Equal(2, views.Count);
        Assert.Equal(new[] { ann, cy }.OrderBy(x => x), views.Select(v => v.UserId).OrderBy(x => x));
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
