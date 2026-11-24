using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>Handle: an Open case is started only by the person holding it, and only Handle moves it to In Progress.</summary>
/// <remarks>
/// Every new case starts Open. Before, the first note, reply, attachment or resolution moved it to In Progress on its own, so a
/// glance at a case could start its clock's first response for the wrong person. Now the holder's Handle is the one way out of
/// Open, the server refuses anyone else's Handle and any work on a case nobody has started (supervisors excepted, so Bulk
/// Resolve keeps working), and who may see a case is the holder, its collaborators, its team lead and those who see every case
/// — not every member of the team. These are server rules: a screen that skipped the buttons must get the same answers.
/// </remarks>
public class HandleCaseTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Team team;
    private readonly User agent, teammate, lead, supervisor;

    public HandleCaseTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"handle-{Guid.NewGuid()}")
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        User U(string name) => new() { Id = Guid.NewGuid(), Name = name, Email = name.Replace(' ', '.') + "@x.test", Role = "Agent", ExternalUserId = Guid.NewGuid().ToString() };
        agent = U("Agent Ann"); teammate = U("Team Tom"); lead = U("Lead Lee"); supervisor = U("Super Sam");
        team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", OwnerId = lead.Id, IsActive = true };
        db.AddRange(agent, teammate, lead, supervisor, team);
        db.TeamMembers.AddRange(
            new TeamMember { TeamId = team.Id, UserId = agent.Id, MemberRole = "Agent", IsActive = true },
            new TeamMember { TeamId = team.Id, UserId = teammate.Id, MemberRole = "Agent", IsActive = true });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private sealed class Caller(Guid id, params string[] permissions) : ICurrentUserAccessor
    {
        public Guid? LocalUserId => id;
        public HostPrincipal? Principal => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
        public bool Has(string permission) => Permissions.Contains("*") || Permissions.Contains(permission);
    }

    private CaseService Cases(Guid callerId, params string[] permissions) =>
        new(new CaseRepository(db), new Mock<INotificationService>().Object, new Mock<IConfigurableSettingsService>().Object, db,
            new Mock<IWebHostEnvironment>().Object, currentUser: new Caller(callerId, permissions));

    private Case AddCase(CaseStatus status, Guid ownerId)
    {
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..6], Title = "t", TeamId = team.Id, OwnerId = ownerId,
            Status = status, Severity = "High", SlaStartTime = DateTime.UtcNow.AddHours(-1), InternalResolutionTargetMinutes = 600,
            ExternalResolutionTargetMinutes = 1200, CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.Cases.Add(c);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return c;
    }

    private Task<CaseStatus> StatusOf(Case c) => db.Cases.AsNoTracking().Where(x => x.Id == c.Id).Select(x => x.Status).SingleAsync();

    // ------------------------------------------------------------------------------------------------ Handle

    [Fact]
    public async Task Handle_moves_the_holders_Open_case_to_In_Progress_records_it_and_counts_as_the_first_response()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        await Cases(agent.Id, Permissions.CasesWrite).HandleCaseAsync(c.Id, agent.Id);

        var after = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);
        Assert.Equal(CaseStatus.InProgress, after.Status);
        Assert.NotNull(after.FirstResponseActualAt);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.ActionType == "STATUS_CHANGED" && e.UserId == agent.Id && e.NewValue == "In Progress"));
    }

    [Fact]
    public async Task Only_the_person_holding_the_case_can_handle_it_even_its_team_lead_is_refused()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        var error = await Assert.ThrowsAsync<CaseLockedException>(() => Cases(lead.Id, Permissions.CasesWrite).HandleCaseAsync(c.Id, lead.Id));

        Assert.Contains("Only Agent Ann can handle", error.Message);
        Assert.Equal(CaseStatus.Open, await StatusOf(c));
    }

    [Fact]
    public async Task Handling_a_case_that_was_already_started_is_refused_as_a_conflict()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);
        await Cases(agent.Id, Permissions.CasesWrite).HandleCaseAsync(c.Id, agent.Id);

        var error = await Assert.ThrowsAsync<CaseHandleException>(() => Cases(agent.Id, Permissions.CasesWrite).HandleCaseAsync(c.Id, agent.Id));

        Assert.Contains("already been started", error.Message);
        Assert.Equal(1, await db.CaseEvents.CountAsync(e => e.CaseId == c.Id && e.ActionType == "STATUS_CHANGED"));
    }

    [Fact]
    public async Task The_status_menu_cannot_move_an_Open_case_only_Handle_can()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        await Assert.ThrowsAsync<CaseHandleException>(() =>
            Cases(agent.Id, Permissions.CasesWrite).UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "InProgress", UserId = agent.Id }));

        Assert.Equal(CaseStatus.Open, await StatusOf(c));
    }

    // ------------------------------------------------------------------------------------------------ no work before Handle

    [Fact]
    public async Task A_note_on_an_Open_case_its_holder_has_not_handled_is_refused_and_moves_nothing()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        var error = await Assert.ThrowsAsync<CaseHandleException>(() =>
            Cases(agent.Id, Permissions.CasesWrite).AddNoteAsync(c.Id, new AddNoteDto { Message = "Called the customer", UserId = agent.Id }));

        Assert.Contains("Handle", error.Message);
        Assert.Equal(CaseStatus.Open, await StatusOf(c));
        Assert.False(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id));
    }

    [Fact]
    public async Task Someone_else_working_an_Open_case_is_told_whose_turn_it_is()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        var error = await Assert.ThrowsAsync<CaseHandleException>(() => Cases(lead.Id, Permissions.CasesWrite).EnsureWorkMayStartAsync(c.Id, lead.Id));

        Assert.Contains("Agent Ann has not started", error.Message);
    }

    [Fact]
    public async Task An_agent_resolving_an_Open_case_before_handling_it_is_refused()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        await Assert.ThrowsAsync<CaseHandleException>(() =>
            Cases(agent.Id, Permissions.CasesResolve).ResolveCaseAsync(c.Id, new ResolveCaseDto { Disposition = "Fixed", UserId = agent.Id }));

        Assert.Equal(CaseStatus.Open, await StatusOf(c));
    }

    [Fact]
    public async Task A_supervisor_resolving_an_Open_case_is_not_made_to_handle_it_and_it_passes_through_In_Progress()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        await Cases(supervisor.Id, Permissions.CasesSupervise, Permissions.CasesResolve)
            .ResolveCaseAsync(c.Id, new ResolveCaseDto { Disposition = "Fixed", UserId = supervisor.Id }, bulk: true);

        Assert.Equal(CaseStatus.Resolved, await StatusOf(c));
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.Message.StartsWith("Status changed to In Progress (first action: bulk resolution)")));
    }

    [Fact]
    public async Task The_workflow_offers_Handle_to_the_holder_only_and_no_status_change_on_an_Open_case()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);
        var loaded = await db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);

        var mine = Cases(agent.Id, Permissions.CasesWrite, Permissions.CasesResolve).WorkflowFor(loaded, agent.Id);
        var leads = Cases(lead.Id, Permissions.CasesWrite).WorkflowFor(loaded, lead.Id);

        Assert.Equal((true, true, false), (mine.AwaitingHandle, mine.CanHandle, mine.CanResolve));
        Assert.Empty(mine.AllowedStatuses);
        Assert.Equal((true, false), (leads.AwaitingHandle, leads.CanHandle));
    }

    // ------------------------------------------------------------------------------------------------ who sees a case

    private Task<bool> Sees(Guid who, Case c, params string[] permissions) =>
        new CaseAccessScope(db, new Caller(who, permissions)).CanSeeAsync(c.Id);

    [Fact]
    public async Task A_teammate_does_not_see_another_agents_case_but_the_holder_and_the_team_lead_with_ViewTeam_do()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        Assert.True(await Sees(agent.Id, c));
        Assert.True(await Sees(lead.Id, c, Permissions.CasesViewTeam));
        Assert.False(await Sees(teammate.Id, c));
        Assert.True(await Sees(supervisor.Id, c, Permissions.CasesViewAll));
    }

    [Fact]
    public async Task A_collaborator_sees_that_one_case_only_and_loses_it_when_removed()
    {
        var shared = AddCase(CaseStatus.InProgress, agent.Id);
        var other = AddCase(CaseStatus.InProgress, agent.Id);
        db.CaseParticipants.Add(new CaseParticipant { CaseId = shared.Id, UserId = teammate.Id, Role = ParticipantRole.CoWorker });
        db.SaveChanges();

        Assert.True(await Sees(teammate.Id, shared));
        Assert.False(await Sees(teammate.Id, other));

        db.CaseParticipants.RemoveRange(db.CaseParticipants.Where(p => p.UserId == teammate.Id));
        db.SaveChanges();

        Assert.False(await Sees(teammate.Id, shared));
    }
}
