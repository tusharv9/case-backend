using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>Who sees which case: decided by the case (holder, collaborator) and by the Host's grants, never by a role name or team membership.</summary>
/// <remarks>
/// An agent must never see another agent's case — not through being in the same team or department, and a team lead only through
/// the Host capability <c>cases:ViewTeam</c>. The same rule decides who is TOLD about a case (automatic escalation) and whose names
/// appear in the Audit Logs filters, so neither can leak a case the person cannot open. Every list, board, search and per-case
/// route reads <see cref="ICaseAccessScope"/>, so these rules hold for a request that skips the screen.
/// </remarks>
public class CaseVisibilityTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Team cards, loans;
    private readonly User ann, tom, lee, sam, ola;

    public CaseVisibilityTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"visibility-{Guid.NewGuid()}").Options);
        User U(string name, string role) => new() { Id = Guid.NewGuid(), Name = name, Email = name.Replace(' ', '.') + "@x.test", Role = role, ExternalUserId = Guid.NewGuid().ToString(), IsActive = true };
        ann = U("Agent Ann", "Agent"); tom = U("Team Tom", "Agent"); lee = U("Lead Lee", "Team Lead"); sam = U("Super Sam", "Manager"); ola = U("Other Ola", "Agent");
        cards = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", OwnerId = lee.Id, IsActive = true };
        loans = new Team { Id = Guid.NewGuid(), Name = "Loans", Code = "LNS", IsActive = true };
        db.AddRange(ann, tom, lee, sam, ola, cards, loans);
        db.TeamMembers.AddRange(
            new TeamMember { TeamId = cards.Id, UserId = ann.Id, MemberRole = "Agent", IsActive = true },
            new TeamMember { TeamId = cards.Id, UserId = tom.Id, MemberRole = "Agent", IsActive = true },
            new TeamMember { TeamId = loans.Id, UserId = ola.Id, MemberRole = "Agent", IsActive = true });
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

    private Case AddCase(Team team, Guid ownerId)
    {
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..6], Title = "t", TeamId = team.Id, OwnerId = ownerId,
            Status = CaseStatus.InProgress, Severity = "High", SlaStartTime = DateTime.UtcNow.AddHours(-1), InternalResolutionTargetMinutes = 600,
            ExternalResolutionTargetMinutes = 1200, CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.Cases.Add(c);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return c;
    }

    private CaseAccessScope Scope(Guid who, params string[] permissions) => new(db, new Caller(who, permissions));

    private async Task<List<Guid>> VisibleTo(Guid who, params string[] permissions) =>
        await Scope(who, permissions).Apply(db.Cases.AsNoTracking()).Select(c => c.Id).ToListAsync();

    /// <summary>What each Host role is granted, as the Host would answer (by role here, so the test can name them).</summary>
    private ConfiguredPermissionProvider Grants()
    {
        var options = new HostIntegrationOptions();
        options.RolePermissions["Agent"] = new[] { Permissions.CasesRead, Permissions.CasesWrite };
        options.RolePermissions["Team Lead"] = new[] { Permissions.CasesRead, Permissions.CasesViewTeam };
        options.RolePermissions["Manager"] = new[] { Permissions.CasesRead, Permissions.CasesViewAll };
        return new ConfiguredPermissionProvider(Options.Create(options));
    }

    private CaseAudience Audience(IPermissionProvider? provider = null) =>
        new(db, provider ?? Grants(), NullLogger<CaseAudience>.Instance);

    // ------------------------------------------------------------------------------------------------ the list, board and search

    [Fact]
    public async Task An_agent_lists_only_the_cases_assigned_to_them_not_their_teammates_or_another_teams()
    {
        var mine = AddCase(cards, ann.Id);
        AddCase(cards, tom.Id);
        AddCase(loans, ola.Id);

        var visible = await VisibleTo(ann.Id, Permissions.CasesRead);

        Assert.Equal(new[] { mine.Id }, visible);
    }

    [Fact]
    public async Task Leading_a_team_shows_its_cases_only_with_the_Host_capability_ViewTeam()
    {
        var teamCase = AddCase(cards, ann.Id);
        var otherTeam = AddCase(loans, ola.Id);

        Assert.Empty(await VisibleTo(lee.Id, Permissions.CasesRead));
        Assert.Equal(new[] { teamCase.Id }, await VisibleTo(lee.Id, Permissions.CasesRead, Permissions.CasesViewTeam));
        Assert.DoesNotContain(otherTeam.Id, await VisibleTo(lee.Id, Permissions.CasesRead, Permissions.CasesViewTeam));
    }

    [Fact]
    public async Task ViewTeam_without_leading_the_team_shows_no_one_elses_case()
    {
        AddCase(cards, ann.Id);

        Assert.Empty(await VisibleTo(tom.Id, Permissions.CasesRead, Permissions.CasesViewTeam));
    }

    [Fact]
    public async Task ViewAll_and_the_administrator_see_every_case()
    {
        AddCase(cards, ann.Id);
        AddCase(loans, ola.Id);

        Assert.Equal(2, (await VisibleTo(sam.Id, Permissions.CasesViewAll)).Count);
        Assert.Equal(2, (await VisibleTo(sam.Id, Permissions.Everything)).Count);
    }

    [Fact]
    public async Task An_agent_loses_a_case_the_moment_it_is_reassigned_away()
    {
        var c = AddCase(cards, ann.Id);
        Assert.True(await Scope(ann.Id, Permissions.CasesRead).CanSeeAsync(c.Id));

        var tracked = await db.Cases.SingleAsync(x => x.Id == c.Id);
        tracked.OwnerId = tom.Id;
        await db.SaveChangesAsync();

        Assert.False(await Scope(ann.Id, Permissions.CasesRead).CanSeeAsync(c.Id));
        Assert.True(await Scope(tom.Id, Permissions.CasesRead).CanSeeAsync(c.Id));
    }

    [Fact]
    public async Task A_collaborator_from_another_team_sees_that_case_and_nothing_else()
    {
        var shared = AddCase(cards, ann.Id);
        AddCase(cards, ann.Id);
        db.CaseParticipants.Add(new CaseParticipant { CaseId = shared.Id, UserId = ola.Id, Role = ParticipantRole.CoWorker });
        db.SaveChanges();

        Assert.Equal(new[] { shared.Id }, await VisibleTo(ola.Id, Permissions.CasesRead));
    }

    // ------------------------------------------------------------------------------------------------ who is told about a case

    [Fact]
    public async Task Only_people_who_may_open_the_case_are_in_its_audience()
    {
        var c = AddCase(cards, ann.Id);

        var audience = await Audience().WhoCanSeeAsync(c.Id, new[] { ann.Id, tom.Id, lee.Id, sam.Id, ola.Id });

        // Ann holds it, Lee leads its team with ViewTeam, Sam has ViewAll; Tom (teammate) and Ola (another team) do not.
        Assert.Equal(new[] { ann.Id, lee.Id, sam.Id }.OrderBy(x => x), audience.OrderBy(x => x));
    }

    [Fact]
    public async Task A_role_name_alone_puts_nobody_in_the_audience()
    {
        var c = AddCase(cards, ann.Id);
        var nothingGranted = new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions()));

        var audience = await Audience(nothingGranted).WhoCanSeeAsync(c.Id, new[] { lee.Id, sam.Id });

        Assert.Empty(audience);
    }

    /// <summary>The local people the outbox's notifications are addressed to (they are sent by Host id).</summary>
    private static List<Guid> Told(AppDbContext db)
    {
        var hostIds = TestNotificationOutbox.Sent(db).SelectMany(n => n.SubjectUserIds!).Select(g => g.ToString()).ToList();
        return hostIds.Select(h => db.Users.AsNoTracking().Single(u => u.ExternalUserId == h).Id).ToList();
    }

    [Fact]
    public async Task An_escalation_to_every_role_holder_tells_only_those_who_can_see_the_case()
    {
        var c = AddCase(cards, ann.Id);
        var tina = new User { Id = Guid.NewGuid(), Name = "Manager Tina", Email = "tina@x.test", Role = "Agent", ExternalUserId = "tina", IsActive = true };
        db.Users.Add(tina);   // holds "Agent" (the level's second role): may not see Ann's case
        db.SaveChanges();
        var escalation = new EscalationService(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), new FakeOrgDirectory(db));
        var level = new EscalationLevelConfig
        {
            LevelNumber = 2, Name = "Level 2", RoutingStrategy = EscalationRouting.AllHolders, FallbackPolicy = EscalationFallbacks.Hold,
            Roles = [new EscalationLevelRole { RoleKey = "Manager", RoleName = "Manager" }, new EscalationLevelRole { RoleKey = "Agent", RoleName = "Agent" }],
        };
        var target = await escalation.ResolveAsync(c, level, new EscalationPolicy([level]), null);

        await new EscalationExecutor(db, Audience(), TestNotificationOutbox.Store(db)).ApplyAsync(db.Cases.Single(x => x.Id == c.Id), new EscalationCommand(EscalationTypes.Automatic, target, null, "SLA 80%"));

        Assert.Contains(tina.Id, target.Recipients.Select(r => r.User.Id));   // escalated to every holder…
        Assert.Equal(new[] { sam.Id }, Told(db));                              // …told only where the case can be opened
    }

    [Fact]
    public async Task The_person_a_case_is_handed_to_is_told_even_without_any_view_grant()
    {
        var c = AddCase(cards, ann.Id);
        var level = new EscalationLevelConfig
        {
            LevelNumber = 2, Name = "Level 2", RoutingStrategy = EscalationRouting.Team,
            Roles = [new EscalationLevelRole { RoleKey = "Team Lead", RoleName = "Team Lead" }],
        };
        var escalation = new EscalationService(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), new FakeOrgDirectory(db));
        var target = await escalation.ResolveAsync(c, level, new EscalationPolicy([level]), null);
        var nothingGranted = new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions()));

        await new EscalationExecutor(db, Audience(nothingGranted), TestNotificationOutbox.Store(db)).ApplyAsync(db.Cases.Single(x => x.Id == c.Id), new EscalationCommand(EscalationTypes.Automatic, target, lee.Id, "SLA 80%"));

        Assert.Equal(new[] { lee.Id }, Told(db));
        Assert.Equal(lee.Id, db.Cases.AsNoTracking().Single(x => x.Id == c.Id).OwnerId);
    }

    // ------------------------------------------------------------------------------------------------ Audit Logs filter lists

    [Fact]
    public async Task The_audit_filter_lists_name_only_people_and_teams_of_the_cases_the_caller_can_see()
    {
        var mine = AddCase(cards, ann.Id);
        var teammates = AddCase(cards, tom.Id);
        var otherTeam = AddCase(loans, ola.Id);
        CaseEvent E(Case c, Guid user) => new() { Id = Guid.NewGuid(), CaseId = c.Id, UserId = user, EventType = EventType.Note, Message = "x", CreatedAt = DateTime.UtcNow };
        db.CaseEvents.AddRange(E(mine, ann.Id), E(mine, sam.Id), E(teammates, tom.Id), E(otherTeam, ola.Id));
        db.SaveChanges();
        var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
        var clock = new SlaClockProvider(new BusinessTimeService(db, cache), new EscalationService(db, cache));
        var caller = new Caller(ann.Id, Permissions.CasesRead, Permissions.AuditView);
        var access = new CaseAccessScope(db, caller);

        var filters = await new AuditLogService(db, new CaseRepository(db, access: access), clock, access: access, currentUser: caller).GetFiltersAsync();

        Assert.Equal(new[] { "Agent Ann", "Super Sam" }, filters.Actors.Select(a => a.Name));
        Assert.Equal(new[] { "Cards" }, filters.Teams.Select(t => t.Name));
    }
}
