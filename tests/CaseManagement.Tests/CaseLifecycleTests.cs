using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// The case lifecycle, escalation, collaboration, swarm and bulk rules, driven through the services the controllers call.
/// </summary>
/// <remarks>
/// Every rule here is enforced on the server whatever the screen offered: a previous holder posting a status change for a
/// case escalated away from them, an agent naming themselves as the escalation recipient, a "collaborator" from another
/// team, a swarm request to a regular agent. Each test is one of those requests and the answer it must get.
/// </remarks>
public class CaseLifecycleTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Team team;
    private readonly Team otherTeam;
    private readonly Department retail;
    private readonly User agent, holder, lead, otherLead, supervisor, outsider;

    public CaseLifecycleTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"lifecycle-{Guid.NewGuid()}")
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        agent = U("Agent Ann", "Agent");
        holder = U("Holder Hana", "Manager");
        lead = U("Lead Lee", "Team Lead");
        otherLead = U("Other Omar", "Team Lead");
        supervisor = U("Super Sam", "Supervisor");
        outsider = U("Outside Ola", "Agent");
        team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", OwnerId = lead.Id, IsActive = true };
        otherTeam = new Team { Id = Guid.NewGuid(), Name = "Loans", Code = "LNS", OwnerId = otherLead.Id, IsActive = true };
        retail = new Department { Id = Guid.NewGuid(), Name = "Retail", Code = "RTL", IsActive = true };
        db.AddRange(agent, holder, lead, otherLead, supervisor, outsider, team, otherTeam, retail);
        db.TeamMembers.AddRange(
            new TeamMember { TeamId = team.Id, UserId = agent.Id, MemberRole = "Agent" },
            new TeamMember { TeamId = team.Id, UserId = holder.Id, MemberRole = "Agent" },
            new TeamMember { TeamId = otherTeam.Id, UserId = outsider.Id, MemberRole = "Agent" });
        db.TeamDepartments.Add(new TeamDepartment { TeamId = otherTeam.Id, DepartmentId = retail.Id });
        db.ManualAssignmentSettings.Add(new ManualAssignmentSettings { EnforceCapacity = false, RequireAvailable = false });
        db.AssignmentConfigurations.Add(new AssignmentConfiguration { TeamId = null, MaxConcurrentCapacity = 10 });
        var reasons = new LookupType { Id = Guid.NewGuid(), Code = "ESCALATION_REASON", Name = "Escalation Reason" };
        var assignReasons = new LookupType { Id = Guid.NewGuid(), Code = "ASSIGNMENT_REASON", Name = "Assignment Reason" };
        db.LookupTypes.AddRange(reasons, assignReasons);
        db.LookupValues.AddRange(
            new LookupValue { Id = Guid.NewGuid(), LookupTypeId = reasons.Id, TypeCode = "ESCALATION_REASON", Value = "Complex", Label = "Complex", IsActive = true },
            new LookupValue { Id = Guid.NewGuid(), LookupTypeId = assignReasons.Id, TypeCode = "ASSIGNMENT_REASON", Value = "Workload", Label = "Workload", IsActive = true });
        db.EscalationLevelConfigs.AddRange(
            Level(1, "Team Lead", 50), Level(2, "Manager", 75), Level(3, "Supervisor", 90));
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static User U(string name, string role) =>
        new() { Id = Guid.NewGuid(), Name = name, Email = name.Replace(' ', '.') + "@x.test", Role = role, ExternalUserId = Guid.NewGuid().ToString() };

    private static EscalationLevelConfig Level(int n, string role, decimal pct) => new()
    {
        LevelNumber = n, Name = $"Level {n}", AssignmentType = "Role", TargetRole = role, TriggerType = "SlaPercentage", TriggerValue = pct,
        ActionDescription = $"Escalate to {role}", NotifyRoleHolders = true, NotificationPriority = "High", IsActive = true,
    };

    private Case AddCase(CaseStatus status, Guid ownerId, int level = 0, Guid? teamId = null)
    {
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60123456789" };
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..6], Title = "t", CustomerId = customer.Id,
            TeamId = teamId ?? team.Id, OwnerId = ownerId, Status = status, Severity = "High", EscalationLevel = level,
            SlaStartTime = DateTime.UtcNow.AddHours(-1), InternalResolutionTargetMinutes = 600, ExternalResolutionTargetMinutes = 1200,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.AddRange(customer, c);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return c;
    }

    private sealed class Caller(Guid id, params string[] permissions) : ICurrentUserAccessor
    {
        public Guid? LocalUserId => id;
        public HostPrincipal? Principal => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
        public bool Has(string permission) => Permissions.Contains("*") || Permissions.Contains(permission);
    }

    private CaseService Cases(Guid callerId, params string[] permissions) =>
        new(new CaseRepository(db), new Mock<INotificationService>().Object, new Mock<IConfigurableSettingsService>().Object, db,
            new Mock<IWebHostEnvironment>().Object,
            escalation: new EscalationService(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), new FakeOrgDirectory(db)),
            currentUser: new Caller(callerId, permissions));

    private CaseCollaborationService Collaboration()
    {
        var clock = new Mock<ISlaClockProvider>();
        clock.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SlaClock(BusinessCalendar.RoundTheClock, null));
        return new CaseCollaborationService(new CaseRepository(db), new Mock<INotificationService>().Object, new Mock<IMentionService>().Object,
            clock.Object, db, NullLogger<CaseCollaborationService>.Instance, new FakeOrgDirectory(db, supervisorRoles: new[] { "Supervisor" }));
    }

    private BulkCaseService Bulk(Guid callerId)
    {
        var caller = new Caller(callerId, Permissions.CasesSupervise, Permissions.CasesViewAll);
        return new BulkCaseService(db, Cases(callerId, Permissions.CasesSupervise, Permissions.CasesViewAll), new CaseAccessScope(db, caller),
            new AssignmentEligibilityService(db), new Mock<INotificationService>().Object, NullLogger<BulkCaseService>.Instance);
    }

    // ------------------------------------------------------------------------------------------------ status lifecycle

    [Fact]
    public async Task A_case_in_progress_cannot_be_moved_back_to_Open()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Cases(agent.Id).UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "Open", UserId = agent.Id }));

        Assert.Contains("cannot be moved to Open", ex.Message);
    }

    [Fact]
    public async Task Waiting_on_customer_pauses_the_SLA_and_going_back_to_work_resumes_it()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);
        var service = Cases(agent.Id);

        await service.UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "Waiting on Customer", UserId = agent.Id });
        Assert.NotNull((await db.Cases.AsNoTracking().FirstAsync(x => x.Id == c.Id)).SlaPausedAt);

        await service.UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "In Progress", UserId = agent.Id });
        var after = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == c.Id);

        Assert.Null(after.SlaPausedAt);
        Assert.Equal(CaseStatus.InProgress, after.Status);
    }

    [Fact]
    public async Task The_previous_holder_cannot_change_an_escalated_case_but_its_holder_and_a_supervisor_can()
    {
        var c = AddCase(CaseStatus.Escalated, holder.Id, level: 2);

        await Assert.ThrowsAsync<CaseLockedException>(() =>
            Cases(agent.Id).UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "In Progress", UserId = agent.Id }));
        await Assert.ThrowsAsync<CaseLockedException>(() =>
            Cases(agent.Id).ResolveCaseAsync(c.Id, new ResolveCaseDto { Disposition = "Done", UserId = agent.Id }));

        await Cases(holder.Id).UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "Waiting on Customer", UserId = holder.Id });
        await Cases(supervisor.Id, Permissions.CasesSupervise).UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "In Progress", UserId = supervisor.Id });

        Assert.Equal(CaseStatus.InProgress, (await db.Cases.AsNoTracking().FirstAsync(x => x.Id == c.Id)).Status);
    }

    [Fact]
    public async Task A_resolved_case_changes_only_through_Reopen_which_brings_the_same_case_back_in_progress()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);
        var service = Cases(agent.Id);
        await service.ResolveCaseAsync(c.Id, new ResolveCaseDto { Disposition = "Fixed", ResolutionNote = "ok", UserId = agent.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = "In Progress", UserId = agent.Id }));

        var result = await service.ReopenCaseAsync(c.Id, new ReopenCaseDto { Message = "Customer called back", UserId = agent.Id });
        var reopened = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == c.Id);

        Assert.Equal(c.Id, result.NewCaseId);
        Assert.Equal(CaseStatus.InProgress, reopened.Status);
        Assert.Null(reopened.ResolvedAt);
        Assert.Null(reopened.SlaOutcome);
        Assert.Equal(1, await db.Cases.CountAsync(x => x.CaseNumber.StartsWith(c.CaseNumber)));   // no R01 copy
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.ActionType == "CASE_REOPENED" && e.UserId == agent.Id));
    }

    [Fact]
    public async Task Resolving_an_Open_case_records_that_work_started_first()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        await Cases(agent.Id).ResolveCaseAsync(c.Id, new ResolveCaseDto { Disposition = "Fixed", UserId = agent.Id });

        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.Message.StartsWith("Status changed to In Progress")));
        Assert.Equal(CaseStatus.Resolved, (await db.Cases.AsNoTracking().FirstAsync(x => x.Id == c.Id)).Status);
    }

    // ------------------------------------------------------------------------------------------------ manual escalation

    [Fact]
    public async Task Escalation_offers_only_holders_of_the_levels_role_and_never_the_caller()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);

        var options = await Cases(lead.Id).GetEscalationOptionsAsync(c.Id, lead.Id);

        Assert.True(options.CanEscalate);
        Assert.Equal(new[] { 1, 2, 3 }, options.Levels.Select(l => l.LevelNumber));
        // The case team's only Team Lead is the caller, so Level 1 falls back to the role's other holders.
        Assert.Equal(new[] { (otherLead.Id, "Role") }, options.Levels[0].Recipients.Select(r => (r.UserId, r.Scope)));
        Assert.Equal(new[] { holder.Id }, options.Levels[1].Recipients.Select(r => r.UserId));    // Manager
        Assert.Equal(new[] { supervisor.Id }, options.Levels[2].Recipients.Select(r => r.UserId));
    }

    [Fact]
    public async Task Nobody_can_escalate_a_case_to_themselves_even_through_the_API()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Cases(lead.Id).EscalateCaseAsync(c.Id,
            new EscalateCaseDto { LevelNumber = 1, TargetUserId = lead.Id, Reason = "Complex" }, lead.Id));

        Assert.Equal("You cannot escalate a case to yourself.", ex.Message);
    }

    [Fact]
    public async Task A_recipient_outside_the_levels_role_is_refused()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => Cases(agent.Id).EscalateCaseAsync(c.Id,
            new EscalateCaseDto { LevelNumber = 2, TargetUserId = supervisor.Id, Reason = "Complex" }, agent.Id));
    }

    [Fact]
    public async Task An_Open_case_must_be_started_before_it_is_escalated()
    {
        var c = AddCase(CaseStatus.Open, agent.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Cases(agent.Id).EscalateCaseAsync(c.Id,
            new EscalateCaseDto { LevelNumber = 1, TargetUserId = lead.Id, Reason = "Complex" }, agent.Id));
    }

    [Fact]
    public async Task Escalating_hands_the_case_to_the_recipient_marks_it_Escalated_and_audits_it()
    {
        var c = AddCase(CaseStatus.WaitingOnCustomer, agent.Id);

        await Cases(agent.Id).EscalateCaseAsync(c.Id, new EscalateCaseDto { LevelNumber = 2, TargetUserId = holder.Id, Reason = "Complex" }, agent.Id);

        var after = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == c.Id);
        Assert.Equal(holder.Id, after.OwnerId);
        Assert.Equal(2, after.EscalationLevel);
        Assert.Equal(CaseStatus.Escalated, after.Status);
        Assert.Null(after.SlaPausedAt);
        var audit = await db.CaseEvents.SingleAsync(e => e.CaseId == c.Id && e.ActionType == "MANUAL_ESCALATION");
        Assert.Equal(agent.Id, audit.UserId);
        Assert.Contains("Reason: Complex", audit.Message);
    }

    // ------------------------------------------------------------------------------------------------ collaboration and swarm

    [Fact]
    public async Task Collaborators_come_from_the_chosen_team_only()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);
        var collaboration = Collaboration();

        var candidates = await collaboration.GetCollaboratorCandidatesAsync(c.Id, team.Id, agent.Id);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            collaboration.AddCoworkersAsync(c.Id, new AddCollaboratorsDto { TeamId = team.Id, CoworkerIds = { outsider.Id } }, agent.Id));
        await collaboration.AddCoworkersAsync(c.Id, new AddCollaboratorsDto { TeamId = team.Id, CoworkerIds = { holder.Id } }, agent.Id);

        Assert.DoesNotContain(candidates, x => x.UserId == outsider.Id);
        Assert.Contains(candidates, x => x.UserId == lead.Id && x.IsTeamLead);
        Assert.True(await db.CaseParticipants.AnyAsync(p => p.CaseId == c.Id && p.UserId == holder.Id && p.Role == ParticipantRole.CoWorker));
    }

    [Fact]
    public async Task Swarm_requests_go_to_team_leads_of_any_team_and_supervisors_never_to_agents()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);
        var collaboration = Collaboration();

        var candidates = await collaboration.GetSwarmCandidatesAsync(c.Id, agent.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => collaboration.RequestSwarmAsync(c.Id,
            new CreateSwarmRequestDto { RecipientUserId = outsider.Id, RecipientKind = "TeamLead", TeamId = otherTeam.Id }, agent.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => collaboration.RequestSwarmAsync(c.Id,
            new CreateSwarmRequestDto { RecipientUserId = holder.Id, RecipientKind = "Supervisor" }, agent.Id));
        var crossTeam = await collaboration.RequestSwarmAsync(c.Id,
            new CreateSwarmRequestDto { RecipientUserId = otherLead.Id, RecipientKind = "TeamLead", TeamId = otherTeam.Id, Message = "Need loans view" }, agent.Id);

        Assert.Equal(new[] { lead.Id, otherLead.Id }, candidates.TeamLeads.Select(l => l.UserId));   // the case's own team first
        Assert.Equal(new[] { supervisor.Id }, candidates.Supervisors.Select(s => s.UserId));
        Assert.Equal(SwarmRequestStatuses.Pending, crossTeam.Status);
        Assert.True(await db.CaseParticipants.AnyAsync(p => p.CaseId == c.Id && p.UserId == otherLead.Id && p.Role == ParticipantRole.Watcher));
    }

    [Fact]
    public async Task Only_the_recipient_answers_a_swarm_request_and_accepting_makes_them_a_collaborator()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);
        var collaboration = Collaboration();
        var request = await collaboration.RequestSwarmAsync(c.Id, new CreateSwarmRequestDto { RecipientUserId = supervisor.Id, RecipientKind = "Supervisor" }, agent.Id);

        await Assert.ThrowsAsync<CaseLockedException>(() =>
            collaboration.RespondToSwarmAsync(c.Id, request.Id, new RespondSwarmRequestDto { Accept = true }, agent.Id));
        var answered = await collaboration.RespondToSwarmAsync(c.Id, request.Id, new RespondSwarmRequestDto { Accept = true, Note = "On it" }, supervisor.Id);

        Assert.Equal(SwarmRequestStatuses.Accepted, answered.Status);
        Assert.NotNull(answered.RespondedAt);
        Assert.True(await db.CaseParticipants.AnyAsync(p => p.CaseId == c.Id && p.UserId == supervisor.Id && p.Role == ParticipantRole.CoWorker));
    }

    // ------------------------------------------------------------------------------------------------ bulk operations

    [Fact]
    public async Task Bulk_reassign_moves_eligible_cases_to_the_departments_team_and_reports_each_failure()
    {
        var open = AddCase(CaseStatus.InProgress, agent.Id);
        var resolved = AddCase(CaseStatus.Resolved, agent.Id);

        var result = await Bulk(supervisor.Id).ReassignAsync(new BulkReassignRequestDto
        {
            CaseIds = { open.Id, resolved.Id }, DepartmentId = retail.Id, AssigneeUserId = outsider.Id, Reason = "Workload",
        }, supervisor.Id);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Contains("cannot be reassigned", result.Items.Single(i => i.CaseId == resolved.Id).Error);
        var moved = await db.Cases.AsNoTracking().FirstAsync(x => x.Id == open.Id);
        Assert.Equal((outsider.Id, otherTeam.Id, (Guid?)retail.Id), (moved.OwnerId, moved.TeamId, moved.DepartmentId));
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == open.Id && e.ActionType == "BULK_REASSIGN"));
    }

    [Fact]
    public async Task Bulk_reassign_refuses_an_assignee_who_does_not_serve_the_department()
    {
        var c = AddCase(CaseStatus.InProgress, agent.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => Bulk(supervisor.Id).ReassignAsync(new BulkReassignRequestDto
        {
            CaseIds = { c.Id }, DepartmentId = retail.Id, AssigneeUserId = agent.Id, Reason = "Workload",
        }, supervisor.Id));
    }

    [Fact]
    public async Task Bulk_resolve_runs_the_single_case_resolution_for_every_case()
    {
        var first = AddCase(CaseStatus.InProgress, agent.Id);
        var second = AddCase(CaseStatus.Open, agent.Id);
        var already = AddCase(CaseStatus.Resolved, agent.Id);

        var result = await Bulk(supervisor.Id).ResolveAsync(new BulkResolveRequestDto
        {
            CaseIds = { first.Id, second.Id, already.Id }, Disposition = "Duplicate", ResolutionNote = "Cleanup",
        }, supervisor.Id);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal("Case is already resolved.", result.Items.Single(i => i.CaseId == already.Id).Error);
        Assert.Equal(2, await db.CaseEvents.CountAsync(e => e.ActionType == "BULK_RESOLVE"));
    }
}
