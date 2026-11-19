using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Teams;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>The team lead is chosen from the team's members (Team Management → New team / Edit team, Step 4).</summary>
/// <remarks>
/// The drawer offers only the people ticked in Members, but a request that skips the drawer must be held to the same rule, or a
/// lead nobody chose as a member would be added to the team behind the person's back. A save that sends no member list (only the
/// lead changed, or the form hides Members) keeps working as before, so other callers are not broken; and unticking the current
/// lead must not quietly put them back, since removing a lead is its own step with a reason.
/// </remarks>
public class TeamLeadMembershipTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Guid lead = Guid.NewGuid(), ann = Guid.NewGuid(), bob = Guid.NewGuid();
    private readonly Guid cards = Guid.NewGuid();

    public TeamLeadMembershipTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"team-lead-{Guid.NewGuid()}").Options);
        User U(Guid id, string name) => new() { Id = id, Name = name, Email = name.ToLower().Replace(' ', '.') + "@x.test", Role = "Agent", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(U(lead, "Lena Lead"), U(ann, "Ann Agent"), U(bob, "Bob Agent"));
        db.Teams.Add(new Team { Id = cards, Name = "Cards", Code = "CD", OwnerId = lead, IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.AddRange(Member(cards, lead), Member(cards, ann));
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static TeamMember Member(Guid team, Guid user) => new()
    {
        Id = Guid.NewGuid(), TeamId = team, UserId = user, MemberRole = "Agent", IsActive = true, IsAssignable = true, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
    };

    private TeamService Teams()
    {
        var routing = new Mock<IRoutingEngineService>();
        routing.Setup(r => r.GetAssignmentConfigAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssignmentConfigDto { Algorithm = "RoundRobin", MaxConcurrentCapacity = 5 });
        return new TeamService(db, routing.Object, new TeamSettingsService(db));
    }

    private static List<TeamMemberInputDto> Members(params Guid[] ids) => ids.Select(i => new TeamMemberInputDto { UserId = i }).ToList();

    [Fact]
    public async Task A_new_team_whose_lead_is_not_one_of_its_members_is_refused_naming_the_lead_field()
    {
        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "Loans", TeamLeadId = bob, Members = Members(ann) }, lead));

        Assert.Equal(TeamFormFields.TeamLead, Assert.Single(error.Errors).Field);
        Assert.False(await db.Teams.AnyAsync(t => t.Name == "Loans"));
    }

    [Fact]
    public async Task A_new_team_led_by_one_of_its_members_is_saved_with_that_lead()
    {
        var team = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "Loans", TeamLeadId = bob, Members = Members(ann, bob) }, lead);

        Assert.Equal(bob, (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id)).OwnerId);
    }

    [Fact]
    public async Task Unticking_the_current_lead_without_choosing_another_is_refused_rather_than_putting_them_back()
    {
        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().UpdateTeamAsync(cards, new UpdateTeamDto { Members = Members(ann) }, lead));

        Assert.Contains("no longer ticked", Assert.Single(error.Errors).Message);
        Assert.Equal(lead, (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == cards)).OwnerId);
    }

    [Fact]
    public async Task Choosing_another_member_as_lead_while_unticking_the_old_one_is_saved()
    {
        await Teams().UpdateTeamAsync(cards, new UpdateTeamDto { TeamLeadId = ann, Members = Members(ann, bob) }, lead);

        Assert.Equal(ann, (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == cards)).OwnerId);
    }

    [Fact]
    public async Task A_save_that_sends_no_member_list_keeps_working_as_before()
    {
        await Teams().UpdateTeamAsync(cards, new UpdateTeamDto { TeamLeadId = ann }, lead);

        Assert.Equal(ann, (await db.Teams.AsNoTracking().SingleAsync(t => t.Id == cards)).OwnerId);
    }
}
