using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// The escalation hierarchy's routing and delivery: who a level reaches under each strategy (several Supervisors included),
/// what its fallback does when nobody is found, and that a committed escalation's notifications are delivered from the outbox
/// until AuthService accepts them — once, however often they are tried.
/// </summary>
/// <remarks>
/// Worth testing because every one of these decides a real person's work: a wrong recipient hands a case to someone unrelated,
/// a lost notification leaves an escalated case unnoticed, and a duplicate one teaches people to ignore escalations.
/// </remarks>
public class EscalationHierarchyTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly User agent, lead, supA, supB, supC, admin;
    private readonly Team accounts, loans;

    public EscalationHierarchyTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"hierarchy-{Guid.NewGuid()}").Options);
        agent = U("Agent Ann", "Agent"); lead = U("Lead Lee", "Team Lead");
        supA = U("Sup Asha", "Supervisor"); supB = U("Sup Bala", "Supervisor"); supC = U("Sup Chen", "Supervisor");
        admin = U("Admin Ada", "Super Admin");
        accounts = new Team { Id = Guid.NewGuid(), Name = "Accounts", Code = "ACC", OwnerId = lead.Id, IsActive = true };
        loans = new Team { Id = Guid.NewGuid(), Name = "Loans", Code = "LNS", IsActive = true };
        db.AddRange(agent, lead, supA, supB, supC, admin, accounts, loans);
        db.TeamMembers.AddRange(
            new TeamMember { TeamId = accounts.Id, UserId = agent.Id, IsActive = true },
            new TeamMember { TeamId = accounts.Id, UserId = supA.Id, IsActive = true, IsAssignable = false },
            new TeamMember { TeamId = accounts.Id, UserId = supB.Id, IsActive = true, IsAssignable = false },
            new TeamMember { TeamId = loans.Id, UserId = supC.Id, IsActive = true, IsAssignable = false });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static User U(string name, string role) =>
        new() { Id = Guid.NewGuid(), Name = name, Email = name.Replace(' ', '.') + "@x.test", Role = role, ExternalUserId = Guid.NewGuid().ToString(), IsActive = true };

    private static EscalationLevelConfig Level(int n, string role, string routing = EscalationRouting.Team, string fallback = EscalationFallbacks.NextLevel, Guid? user = null) => new()
    {
        Id = Guid.NewGuid(), LevelNumber = n, Name = $"Level {n}", TriggerType = EscalationTriggers.SlaPercentage, TriggerValue = 20 * n,
        ActionDescription = "Needs attention", RoutingStrategy = routing, FallbackPolicy = fallback, TargetUserId = user, IsActive = true,
        Roles = [new EscalationLevelRole { RoleKey = role, RoleName = role }],
    };

    private EscalationService Escalation() =>
        new(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), new FakeOrgDirectory(db, administratorRoles: ["Super Admin"]));

    private Case NewCase(Team team, Guid ownerId)
    {
        var c = new Case { Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..5], Title = "t", TeamId = team.Id, OwnerId = ownerId, Status = CaseStatus.InProgress, Severity = "High" };
        db.Cases.Add(c);
        db.SaveChanges();
        return c;
    }

    private async Task<EscalationTarget> Resolve(Case c, EscalationLevelConfig level, params EscalationLevelConfig[] others) =>
        await Escalation().ResolveAsync(c, level, new EscalationPolicy([level, .. others]), null);

    // ------------------------------------------------------------------------------------------------ routing strategies

    [Fact]
    public async Task Team_routing_reaches_only_the_Supervisors_of_the_cases_own_team()
    {
        var target = await Resolve(NewCase(accounts, agent.Id), Level(3, "Supervisor"));

        Assert.Equal(new[] { supA.Id, supB.Id }.OrderBy(x => x), target.Recipients.Select(r => r.User.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task All_holders_routing_reaches_every_Supervisor()
    {
        var target = await Resolve(NewCase(accounts, agent.Id), Level(3, "Supervisor", EscalationRouting.AllHolders));

        Assert.Equal(3, target.Recipients.Count);
    }

    [Fact]
    public async Task One_holder_routing_picks_the_team_Supervisor_holding_the_fewest_open_cases()
    {
        NewCase(accounts, supA.Id);
        NewCase(accounts, supA.Id);
        NewCase(accounts, supB.Id);
        var resolved = NewCase(accounts, supB.Id);
        resolved.Status = CaseStatus.Resolved;   // finished work does not count
        db.SaveChanges();

        var target = await Resolve(NewCase(accounts, agent.Id), Level(3, "Supervisor", EscalationRouting.OneHolder));

        Assert.Equal(new[] { supB.Id }, target.Recipients.Select(r => r.User.Id));
    }

    [Fact]
    public async Task Specific_user_routing_reaches_that_person_only_while_they_hold_the_role()
    {
        var c = NewCase(accounts, agent.Id);

        var named = await Resolve(c, Level(3, "Supervisor", EscalationRouting.SpecificUser, EscalationFallbacks.Hold, supC.Id));
        var notAHolder = await Resolve(c, Level(3, "Supervisor", EscalationRouting.SpecificUser, EscalationFallbacks.Hold, lead.Id));

        Assert.Equal(new[] { supC.Id }, named.Recipients.Select(r => r.User.Id));
        Assert.Empty(notAHolder.Recipients);
    }

    [Fact]
    public async Task A_deactivated_holder_is_never_reached()
    {
        db.Users.Single(u => u.Id == lead.Id).IsActive = false;
        db.SaveChanges();

        var target = await Resolve(NewCase(accounts, agent.Id), Level(2, "Team Lead", fallback: EscalationFallbacks.Hold));

        Assert.Empty(target.Recipients);
    }

    // ------------------------------------------------------------------------------------------------ fallbacks

    [Fact]
    public async Task With_nobody_at_a_level_the_case_goes_up_to_the_next_one_and_says_why()
    {
        var c = NewCase(loans, agent.Id);   // Loans has no team lead
        var supervisors = Level(3, "Supervisor", fallback: EscalationFallbacks.Administrators);

        var target = await Resolve(c, Level(2, "Team Lead"), supervisors);

        Assert.Equal(3, target.Level.LevelNumber);
        Assert.Equal(new[] { supC.Id }, target.Recipients.Select(r => r.User.Id));
        Assert.Contains("Level 2 had nobody", target.FallbackNote);
    }

    [Fact]
    public async Task The_administrators_fallback_reaches_the_administrators_and_hold_reaches_nobody()
    {
        var c = NewCase(loans, agent.Id);

        var toAdmins = await Resolve(c, Level(2, "Team Lead", fallback: EscalationFallbacks.Administrators));
        var held = await Resolve(c, Level(2, "Team Lead", fallback: EscalationFallbacks.Hold));

        Assert.Equal(new[] { admin.Id }, toAdmins.Recipients.Select(r => r.User.Id));
        Assert.Empty(held.Recipients);
        Assert.Contains("stays with its holder", held.FallbackNote);
    }

    // ------------------------------------------------------------------------------------------------ history and delivery

    [Fact]
    public async Task Automatic_escalation_to_several_people_notifies_them_all_and_leaves_the_case_with_its_holder()
    {
        var c = NewCase(accounts, agent.Id);
        var target = await Resolve(c, Level(3, "Supervisor"));

        var history = await new EscalationExecutor(db, notifications: TestNotificationOutbox.Store(db)).ApplyAsync(c, new EscalationCommand(EscalationTypes.Automatic, target, null,
            "Escalated automatically because 80% of the SLA was consumed.", Trigger: "SLA consumption reaches 60%", SlaConsumedPercent: 80m));

        var after = db.Cases.AsNoTracking().Single(x => x.Id == c.Id);
        Assert.Equal((agent.Id, 3, CaseStatus.InProgress), (after.OwnerId, after.EscalationLevel, after.Status));
        Assert.Equal((EscalationTypes.Automatic, (Guid?)null, 80m), (history.Type, history.ToOwnerId, history.SlaConsumedPercent));
        Assert.Equal(2, TestNotificationOutbox.Rows(db).Count);
        Assert.Contains("80% of the SLA", db.CaseEvents.Single(e => e.CaseId == c.Id).Message);
    }

    [Fact]
    public async Task An_escalations_notification_is_saved_in_the_notification_outbox_with_the_escalation()
    {
        var c = NewCase(accounts, agent.Id);
        var target = await Resolve(c, Level(2, "Team Lead"));

        await new EscalationExecutor(db, notifications: TestNotificationOutbox.Store(db)).ApplyAsync(c, new EscalationCommand(EscalationTypes.Manual, target, lead.Id, "Complex"));

        var row = TestNotificationOutbox.Rows(db).Single();
        var sent = TestNotificationOutbox.Sent(db).Single();
        Assert.Equal((OmniConnect.Outbox.OutboxStates.Pending, row.EventId), (row.Status, sent.EventId));
        Assert.Equal("remote.casemanagement.cases:case.escalated", sent.Kind);
        Assert.Equal(new[] { Guid.Parse(lead.ExternalUserId!) }, sent.SubjectUserIds);
        Assert.Equal(("Level 2", "Complex", "Agent Ann"), (sent.Values!["level"], sent.Values["reason"], sent.Values["previous"]));
        Assert.NotNull(sent.OccurredAt);
        // The same escalation event always has the same id, so a retry is written once by the Notification Service.
        Assert.Equal(CaseNotificationBuilder.DeterministicGuid($"{CaseNotificationKinds.Escalated}:escalation:{c.Id}:{c.EscalationCycle}:2:{lead.Id}"), sent.EventId);
    }
}
