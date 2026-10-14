using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// Teams, membership, routing, assignment, skills and the Team Monitor, on a real migrated PostgreSQL database.
/// Team membership (TeamMembers) is the only thing that makes someone eligible for a team's cases.
/// </summary>
public class TeamsRoutingTests
{
    private sealed class World : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public Guid Team, Lead, Admin;
        public Guid A, B, C;   // three agents in the team

        public AppDbContext Ctx() => Db.NewContext();

        public static async Task<World> CreateAsync(UserStatus status = UserStatus.Available)
        {
            var w = new World { Db = await TempDatabase.CreateAsync() };
            await using var ctx = w.Db.NewContext();
            await ctx.Database.MigrateAsync();
            DbSeeder.Run(ctx, SeedMode.Bootstrap, adoptedLegacyDatabase: false);

            // A round-the-clock UTC calendar keeps clock-dependent assertions independent of when the test runs.
            ctx.BusinessHours.ExecuteDelete();
            foreach (var d in Enum.GetValues<DayOfWeek>())
                ctx.BusinessHours.Add(new BusinessHour { DayOfWeek = d, DayName = d.ToString(), IsEnabled = true, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(24), CreatedAt = DateTime.UtcNow });
            ctx.BusinessCalendarSettings.ExecuteUpdate(s => s.SetProperty(x => x.TimeZoneId, "UTC"));

            w.Team = Guid.NewGuid(); w.Lead = Guid.NewGuid(); w.Admin = Guid.NewGuid();
            w.A = Guid.NewGuid(); w.B = Guid.NewGuid(); w.C = Guid.NewGuid();
            ctx.Departments.Add(new Department { Id = w.Team, Name = "Support", Code = "SUP", Function = "Support", CreatedAt = DateTime.UtcNow, IsActive = true });
            await ctx.SaveChangesAsync();
            User U(Guid id, string name, string role) => new() { Id = id, Name = name, Email = name.ToLower().Replace(' ', '.') + "@x.test", Role = role, Status = status, CreatedAt = DateTime.UtcNow };
            ctx.Users.AddRange(U(w.Lead, "Lead Lee", "Team Lead"), U(w.Admin, "Admin Ann", "Admin"), U(w.A, "Agent A", "Agent"), U(w.B, "Agent B", "Agent"), U(w.C, "Agent C", "Agent"));
            await ctx.SaveChangesAsync();
            await ctx.Departments.Where(d => d.Id == w.Team).ExecuteUpdateAsync(u => u.SetProperty(d => d.OwnerId, w.Lead));
            ctx.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), DepartmentId = w.Team, UserId = w.Lead, MemberRole = "Team Lead", IsActive = true, IsAssignable = false, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
            ctx.TeamMembers.AddRange(new[] { w.A, w.B, w.C }.Select(id => new TeamMember { Id = Guid.NewGuid(), DepartmentId = w.Team, UserId = id, MemberRole = "Agent", IsActive = true, IsAssignable = true, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow }));
            await ctx.SaveChangesAsync();
            return w;
        }

        public RoutingEngineService Engine(AppDbContext ctx)
        {
            var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
            var strategies = new IAssignmentStrategy[]
            {
                new RoundRobinAssignmentStrategy(ctx), new LeastOccupancyAssignmentStrategy(), new SkillBasedAssignmentStrategy(ctx, new SkillService(ctx, cache)),
            };
            return new RoutingEngineService(ctx, strategies, new AgentPoolService(ctx));
        }

        public TeamService Teams(AppDbContext ctx) => new(ctx, Engine(ctx));

        public async Task SetAlgorithm(string algorithm, int capacity = 5)
        {
            await using var ctx = Ctx();
            await ctx.AssignmentConfigurations.Where(c => c.DepartmentId == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Algorithm, algorithm).SetProperty(c => c.MaxConcurrentCapacity, capacity));
        }

        public Case NewCase(Guid ownerId, string title = "Card problem", string channel = "Voice", string subcategory = "General") => new()
        {
            Id = Guid.NewGuid(), CaseNumber = "T-" + Guid.NewGuid().ToString("N")[..8], Title = title, CaseType = "Complaint", Severity = "High",
            SourceChannel = channel, Subcategory = subcategory, DepartmentId = Team, OwnerId = ownerId, Status = CaseStatus.Open,
            CustomerId = Guid.Empty, SlaStartTime = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            FirstResponseTargetMinutes = 30, InternalResolutionTargetMinutes = 120, ExternalResolutionTargetMinutes = 240, FirstResponseStatus = "Pending",
        };

        /// <summary>Routes and stores one case the way case creation does: in a transaction, so the team lock holds until commit.</summary>
        public async Task<(Case Case, RoutingDecisionResult Decision)> RouteAndStoreAsync(string title = "Card problem", string channel = "Voice", Customer? customer = null)
        {
            await using var ctx = Ctx();
            var cust = customer ?? new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60" + Random.Shared.NextInt64(1000000000, 1999999999), CreatedAt = DateTime.UtcNow };
            if (customer == null) ctx.Customers.Add(cust);
            var c = NewCase(Lead, title, channel);
            c.CustomerId = cust.Id;

            var strategy = ctx.Database.CreateExecutionStrategy();
            RoutingDecisionResult? decision = null;
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await ctx.Database.BeginTransactionAsync();
                decision = await Engine(ctx).RouteAndAssignCaseAsync(c, cust);
                ctx.Cases.Add(c);
                await ctx.SaveChangesAsync();
                await tx.CommitAsync();
            });
            return (c, decision!);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    // ------------------------------------------------------------------------------------------ eligibility

    [PostgresFact]
    public async Task OnlyActiveAssignableOnlineMembersBelowCapacityAreEligible()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        await ctx.Users.Where(u => u.Id == w.B).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, UserStatus.Away));            // not online
        await ctx.TeamMembers.Where(m => m.UserId == w.C).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsAssignable, false));       // does not take cases

        var pool = await new AgentPoolService(ctx).GetAsync(w.Team, 5);
        Assert.Equal(new[] { w.A }, pool.Eligible.Select(e => e.User.Id).ToArray());
        Assert.Equal(NoAgentReason.None, pool.Reason);
    }

    [PostgresFact]
    public async Task MembershipIsTheOnlySource_AHomeTeamAloneDoesNotMakeSomeoneEligible()
    {
        await using var w = await World.CreateAsync();
        var outsider = Guid.NewGuid();
        await using (var ctx = w.Ctx())
        {
            // Their "home department" is the team, but they are not a member (the old code treated that as membership).
            ctx.Users.Add(new User { Id = outsider, Name = "Outsider", Email = "o@x.test", Role = "Agent", DepartmentId = w.Team, Status = UserStatus.Available, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
        await using var check = w.Ctx();
        var pool = await new AgentPoolService(check).GetAsync(w.Team, 5);
        Assert.DoesNotContain(pool.Eligible, e => e.User.Id == outsider);

        // Removing a membership removes eligibility straight away.
        await w.Teams(check).RemoveMemberAsync(w.Team, w.A, w.Admin);
        var after = await new AgentPoolService(check).GetAsync(w.Team, 5);
        Assert.DoesNotContain(after.Eligible, e => e.User.Id == w.A);
    }

    [PostgresFact]
    public async Task ADeactivatedHostUser_IsNeverAssigned()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        await ctx.Users.Where(u => u.Id == w.A).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        var pool = await new AgentPoolService(ctx).GetAsync(w.Team, 5);
        Assert.DoesNotContain(pool.Eligible, e => e.User.Id == w.A);
    }

    // ------------------------------------------------------------------------------------------ algorithms

    [PostgresFact]
    public async Task RoundRobin_RotatesThroughEligibleAgents()
    {
        await using var w = await World.CreateAsync();
        await w.SetAlgorithm("RoundRobin");
        var owners = new List<Guid>();
        for (var i = 0; i < 6; i++) owners.Add((await w.RouteAndStoreAsync()).Case.OwnerId);

        Assert.Equal(new[] { w.A, w.B, w.C }.OrderBy(g => g).ToArray(), owners.Take(3).OrderBy(g => g).ToArray());   // all three in the first lap
        Assert.Equal(owners.Take(3), owners.Skip(3));                                                              // then the same order again
    }

    [PostgresFact]
    public async Task EveryAlgorithm_RespectsCapacity_AndUnassignedCasesAreHeldByTheLeadWithAReason()
    {
        foreach (var algorithm in new[] { "RoundRobin", "LeastOccupancy", "SkillBased" })
        {
            await using var w = await World.CreateAsync();
            await w.SetAlgorithm(algorithm, capacity: 1);

            var first = new List<Guid>();
            for (var i = 0; i < 3; i++) first.Add((await w.RouteAndStoreAsync()).Case.OwnerId);
            Assert.Equal(3, first.Distinct().Count());                          // capacity 1 each: three cases, three different agents
            Assert.DoesNotContain(w.Lead, first);

            var (held, decision) = await w.RouteAndStoreAsync();                // everyone is full now
            Assert.Equal(w.Lead, held.OwnerId);                                 // held by the team lead — not the creator, not an overloaded agent
            Assert.Equal(w.Lead, decision.HeldByUserId);
            Assert.Contains("at capacity", decision.HeldReason);
            Assert.Contains("Pending agent assignment", decision.RoutingLogMessage);
        }
    }

    [PostgresFact]
    public async Task WhenNobodyIsOnline_ThePoolSaysWhy()
    {
        await using var w = await World.CreateAsync(UserStatus.Offline);
        var (c, decision) = await w.RouteAndStoreAsync();
        Assert.Equal(w.Lead, c.OwnerId);
        Assert.Contains("available", decision.HeldReason);
    }

    [PostgresFact]
    public async Task LeastOccupancy_PicksTheLeastLoadedAgent()
    {
        await using var w = await World.CreateAsync();
        await w.SetAlgorithm("LeastOccupancy");
        await using (var ctx = w.Ctx())
        {
            var cust = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = "N1", PhoneNumber = "+60111111111", CreatedAt = DateTime.UtcNow };
            ctx.Customers.Add(cust);
            foreach (var (agent, n) in new[] { (w.A, 3), (w.B, 1), (w.C, 2) })
                for (var i = 0; i < n; i++) { var c = w.NewCase(agent); c.CustomerId = cust.Id; ctx.Cases.Add(c); }
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(w.B, (await w.RouteAndStoreAsync()).Case.OwnerId);
    }

    [PostgresFact]
    public async Task SkillBased_UsesTheConfiguredSkillRules_NotKeywordsInCode()
    {
        await using var w = await World.CreateAsync();
        await w.SetAlgorithm("SkillBased");
        await using (var ctx = w.Ctx())
        {
            var skills = new SkillService(ctx, new ConfigCache(new MemoryCache(new MemoryCacheOptions())));
            await skills.ReplaceRulesAsync(new[] { new SkillRuleDto { SkillName = "Mortgages", MatchField = "Title", MatchType = "Contains", MatchValue = "mortgage" } }, w.Admin);
            await skills.ReplaceAgentSkillsAsync(w.C, new[] { new AgentSkillDto { SkillName = "mortgages", ProficiencyLevel = 3 } }, w.Admin);
            await skills.ReplaceAgentSkillsAsync(w.A, new[] { new AgentSkillDto { SkillName = "Fraud", ProficiencyLevel = 5 } }, w.Admin);
        }

        // A mortgage case goes to the agent with that skill even though the agent with a "fraud" skill is more proficient overall.
        var (mortgage, d1) = await w.RouteAndStoreAsync(title: "Mortgage rate question");
        Assert.Equal(w.C, mortgage.OwnerId);
        Assert.Contains("matched skills: Mortgages", d1.RoutingLogMessage);

        // The old hard-coded "fraud" keyword means nothing now: no rule asks for it, so work is shared by load.
        var (fraud, d2) = await w.RouteAndStoreAsync(title: "Suspected fraud on my account");
        Assert.Contains("no skills required", d2.RoutingLogMessage);
        Assert.NotEqual(w.C, fraud.OwnerId);   // C already holds the mortgage case; least loaded wins
    }

    [PostgresFact]
    public async Task SkillRules_AreValidated()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var skills = new SkillService(ctx, new ConfigCache(new MemoryCache(new MemoryCacheOptions())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => skills.ReplaceRulesAsync(new[] { new SkillRuleDto { SkillName = "X", MatchField = "Mood", MatchValue = "angry" } }, w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => skills.ReplaceRulesAsync(new[] { new SkillRuleDto { SkillName = "", MatchField = "Title", MatchValue = "x" } }, w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => skills.ReplaceAgentSkillsAsync(w.A, new[] { new AgentSkillDto { SkillName = "Fraud", ProficiencyLevel = 9 } }, w.Admin));
    }

    // ------------------------------------------------------------------------------------------ concurrency

    [PostgresFact]
    public async Task SimultaneousCases_AreSpreadFairly_AndNeverExceedCapacity()
    {
        await using var w = await World.CreateAsync();
        await w.SetAlgorithm("LeastOccupancy", capacity: 4);

        // 12 cases created at the same moment for 3 agents with room for 4 each: exactly 4 apiece.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => w.RouteAndStoreAsync())));

        var perAgent = results.GroupBy(r => r.Case.OwnerId).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new[] { w.A, w.B, w.C }.OrderBy(g => g), perAgent.Keys.OrderBy(g => g));
        Assert.All(perAgent.Values, n => Assert.Equal(4, n));
    }

    [PostgresFact]
    public async Task SimultaneousRoundRobin_NeverHandsTwoCasesToTheSamePositionTwice()
    {
        await using var w = await World.CreateAsync();
        await w.SetAlgorithm("RoundRobin", capacity: 50);
        var results = await Task.WhenAll(Enumerable.Range(0, 9).Select(_ => Task.Run(() => w.RouteAndStoreAsync())));
        Assert.All(results.GroupBy(r => r.Case.OwnerId), g => Assert.Equal(3, g.Count()));
    }

    // ------------------------------------------------------------------------------------------ settings

    [PostgresFact]
    public async Task ATeamCanHaveItsOwnSettings_AndGoBackToTheGlobalOnes()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var engine = w.Engine(ctx);

        var global = await engine.GetAssignmentConfigAsync(w.Team);
        Assert.False(global.IsTeamOverride);
        Assert.Equal("RoundRobin", global.Algorithm);

        var own = await engine.UpdateAssignmentConfigAsync(new UpdateAssignmentConfigDto { Algorithm = "leastoccupancy", MaxConcurrentCapacity = 2 }, w.Admin, w.Team);
        Assert.True(own.IsTeamOverride);
        Assert.Equal("LeastOccupancy", own.Algorithm);
        Assert.Equal("RoundRobin", (await engine.GetAssignmentConfigAsync(null)).Algorithm);   // the global one is untouched

        // And it is what routing really uses for that team.
        Assert.Equal("LeastOccupancy", (await w.RouteAndStoreAsync()).Decision.AlgorithmUsed);

        await engine.ClearTeamAssignmentConfigAsync(w.Team, w.Admin);
        Assert.False((await engine.GetAssignmentConfigAsync(w.Team)).IsTeamOverride);
        Assert.Equal("RoundRobin", (await w.RouteAndStoreAsync()).Decision.AlgorithmUsed);
    }

    [PostgresFact]
    public async Task AssignmentSettings_AreValidated_NotSilentlyCorrected()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var engine = w.Engine(ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UpdateAssignmentConfigAsync(new UpdateAssignmentConfigDto { Algorithm = "Random" }, w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UpdateAssignmentConfigAsync(new UpdateAssignmentConfigDto { Algorithm = "RoundRobin", MaxConcurrentCapacity = 0 }, w.Admin));
    }

    // ------------------------------------------------------------------------------------------ rules

    private static CreateRoutingRuleDto Rule(string name, Guid target, RuleConditionsDto conditions, int? order = null) =>
        new() { Name = name, TargetDepartmentId = target, Conditions = conditions, EvaluationOrder = order };

    [PostgresFact]
    public async Task RuleConditions_AreExact_NotContains()
    {
        await using var w = await World.CreateAsync();
        var other = Guid.NewGuid();
        await using (var ctx = w.Ctx())
        {
            ctx.Departments.Add(new Department { Id = other, Name = "Fraud Desk", Code = "FD", CreatedAt = DateTime.UtcNow });
            ctx.DepartmentSubCategories.AddRange(
                new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = w.Team, Name = "Fraud", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow },
                new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = w.Team, Name = "Fraud Prevention", Code = "FP", IsActive = true, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
            await w.Engine(ctx).CreateRuleAsync(Rule("Fraud", other, new RuleConditionsDto { Category = "Fraud" }), w.Admin);
        }

        // "Fraud Prevention" must not trigger a rule for "Fraud" (the old code used Contains).
        await using var run = w.Ctx();
        var engine = w.Engine(run);
        var prevention = w.NewCase(w.Lead, subcategory: "Fraud Prevention");
        Assert.Equal(w.Team, (await engine.RouteAndAssignCaseAsync(prevention, null)).TargetDepartmentId);

        var exact = w.NewCase(w.Lead, subcategory: "fraud");   // case-insensitive
        Assert.Equal(other, (await engine.RouteAndAssignCaseAsync(exact, null)).TargetDepartmentId);
    }

    [PostgresFact]
    public async Task ARuleOnAChannel_DoesNotMatchACaseWithNoChannel()
    {
        // Regression: Contains("") is true, so a case with an optional blank channel matched every channel rule.
        await using var w = await World.CreateAsync();
        var other = Guid.NewGuid();
        await using var ctx = w.Ctx();
        ctx.Departments.Add(new Department { Id = other, Name = "Social Desk", Code = "SD", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        var engine = w.Engine(ctx);
        await engine.CreateRuleAsync(Rule("Social", other, new RuleConditionsDto { Channel = "Social" }), w.Admin);

        var blank = w.NewCase(w.Lead, channel: "");
        Assert.Equal(w.Team, (await engine.RouteAndAssignCaseAsync(blank, null)).TargetDepartmentId);
        var social = w.NewCase(w.Lead, channel: "Social");
        Assert.Equal(other, (await engine.RouteAndAssignCaseAsync(social, null)).TargetDepartmentId);
    }

    [PostgresFact]
    public async Task Rules_AreValidated_AgainstTheVocabulary()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var engine = w.Engine(ctx);

        var vocab = await engine.GetVocabularyAsync();
        Assert.Contains("Voice", vocab.Channels);
        Assert.Contains("Critical", vocab.Priorities);
        Assert.Contains("Complaint", vocab.CaseTypes);
        Assert.Contains(vocab.Departments, d => d.Id == w.Team);
        Assert.Equal(new[] { "RoundRobin", "SkillBased", "LeastOccupancy" }, vocab.Algorithms);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateRuleAsync(Rule("bad", w.Team, new RuleConditionsDto { Priority = "Whenever" }), w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateRuleAsync(Rule("bad", w.Team, new RuleConditionsDto { Channel = "Carrier Pigeon" }), w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateRuleAsync(Rule("bad", Guid.NewGuid(), new RuleConditionsDto()), w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateRuleAsync(Rule("bad", w.Team, new RuleConditionsDto { MatchType = "SOMETIMES" }), w.Admin));

        await ctx.Departments.Where(d => d.Id == w.Team).ExecuteUpdateAsync(u => u.SetProperty(d => d.IsActive, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateRuleAsync(Rule("inactive", w.Team, new RuleConditionsDto()), w.Admin));
    }

    [PostgresFact]
    public async Task WithNoRuleAndAnInactiveOriginalTeam_RoutingFailsLoudly_NoHiddenDefaultTeam()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        await ctx.Departments.Where(d => d.Id == w.Team).ExecuteUpdateAsync(u => u.SetProperty(d => d.IsActive, false));
        // Another active team exists, but nothing silently falls back to it (the old code picked "CC" or the first active team).
        ctx.Departments.Add(new Department { Id = Guid.NewGuid(), Name = "Contact Center", Code = "CC", CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Engine(ctx).RouteAndAssignCaseAsync(w.NewCase(w.Lead), null));
    }

    // ------------------------------------------------------------------------------------------ teams

    [PostgresFact]
    public async Task Teams_HaveOneMembershipList_TheLeadIsAMember_AndCountsComeFromTheDatabase()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var svc = w.Teams(ctx);

        var created = await svc.CreateTeamAsync(new CreateTeamDto
        {
            Name = "Team Seven", TeamLeadId = w.Lead,
            Members = new() { new() { UserId = w.A, IsAssignable = true }, new() { UserId = w.B, IsAssignable = false } },
            AssignmentAlgorithm = "LeastOccupancy", MaxConcurrentCapacity = 2,
        }, w.Admin);

        Assert.Equal(3, created.MemberCount);                                  // A, B and the lead
        Assert.True(created.Members.Single(m => m.UserId == w.Lead).IsLead);
        Assert.False(created.Members.Single(m => m.UserId == w.B).IsAssignable);
        Assert.True(created.HasOwnAssignmentSettings);
        Assert.Equal("LeastOccupancy", created.AssignmentAlgorithm);
        Assert.Equal(2, created.MaxConcurrentCapacity);

        // Changing only the lead keeps the existing members.
        var updated = await svc.UpdateTeamAsync(created.Id, new UpdateTeamDto { TeamLeadId = w.C }, w.Admin);
        Assert.Equal(4, updated.MemberCount);
        Assert.True(updated.Members.Single(m => m.UserId == w.C).IsLead);

        // Back to the global settings.
        var global = await svc.UpdateTeamAsync(created.Id, new UpdateTeamDto { UseGlobalAssignmentSettings = true }, w.Admin);
        Assert.False(global.HasOwnAssignmentSettings);
        Assert.Equal("RoundRobin", global.AssignmentAlgorithm);

        // Teams are no longer filtered by hard-coded codes: one called "CS" is listed like any other.
        await svc.CreateTeamAsync(new CreateTeamDto { Name = "Campaign Studio", Code = "CS" }, w.Admin);
        Assert.Contains((await svc.GetTeamsAsync()), t => t.Code == "CS");
    }

    [PostgresFact]
    public async Task ATeamThatRoutingRulesPointAt_OrThatHasHistory_CannotBeDeleted()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var svc = w.Teams(ctx);
        var target = (await svc.CreateTeamAsync(new CreateTeamDto { Name = "Target" }, w.Admin)).Id;
        await w.Engine(ctx).CreateRuleAsync(Rule("To target", target, new RuleConditionsDto { Channel = "Email" }), w.Admin);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteTeamAsync(target, w.Admin));
        Assert.Contains("routing rules", ex.Message);

        var empty = (await svc.CreateTeamAsync(new CreateTeamDto { Name = "Empty" }, w.Admin)).Id;
        await svc.DeleteTeamAsync(empty, w.Admin);
        Assert.Null(await svc.GetTeamByIdAsync(empty));

        await w.RouteAndStoreAsync();   // the Support team now has case history
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteTeamAsync(w.Team, w.Admin));
    }

    [PostgresFact]
    public async Task ADeactivatedUser_CannotJoinATeamOrLeadOne()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        await ctx.Users.Where(u => u.Id == w.C).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
        var svc = w.Teams(ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddMemberAsync(w.Team, new AddTeamMemberDto { UserId = w.C }, w.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateTeamAsync(new CreateTeamDto { Name = "X", TeamLeadId = w.C }, w.Admin));
    }

    // ------------------------------------------------------------------------------------------ monitoring

    private TeamMonitoringService Monitor(AppDbContext ctx, World w)
    {
        var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
        var escalation = new EscalationService(ctx, cache);
        return new TeamMonitoringService(ctx, new SlaClockProvider(new BusinessTimeService(ctx, cache), escalation), w.Engine(ctx),
            Options.Create(new TeamMonitoringOptions()));
    }

    [PostgresFact]
    public async Task TheMonitor_InventsNothing_WhenThereIsNoData()
    {
        await using var w = await World.CreateAsync(UserStatus.Offline);
        await using var ctx = w.Ctx();
        var o = await Monitor(ctx, w).GetOverviewAsync();

        Assert.Equal(0, o.Summary.OnlineAgentsCount);
        Assert.Equal(4, o.Summary.TotalAgentsCount);                 // the 3 agents plus the lead, who is a member
        Assert.Null(o.Summary.LongestQueueWaitMinutes);              // not "38"
        Assert.Null(o.Summary.AvgResolutionMinutes);                 // not "6m 12s"
        Assert.Null(o.Summary.OccupancyPercent);                     // not 78%
        Assert.Empty(o.SlaAtRisk);
        Assert.All(o.Queues, q => Assert.Equal(0, q.WaitingCount));  // not fabricated per-channel defaults
        Assert.All(o.Agents, a => Assert.Equal(0, a.HandledTodayCount));
    }

    [PostgresFact]
    public async Task TheMonitor_ComputesFromRealCases_AndFiltersByTeam()
    {
        await using var w = await World.CreateAsync();
        var other = Guid.NewGuid();
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = "NM1", PhoneNumber = "+60122222222", CreatedAt = DateTime.UtcNow };
        await using (var ctx = w.Ctx())
        {
            ctx.Departments.Add(new Department { Id = other, Name = "Other Team", Code = "OT", CreatedAt = DateTime.UtcNow });
            ctx.Customers.Add(customer);
            await ctx.SaveChangesAsync();

            Case Make(Guid dept, Guid owner, int ageMinutes, CaseStatus status, string channel)
            {
                var c = w.NewCase(owner, channel: channel);
                c.DepartmentId = dept; c.CustomerId = customer.Id; c.Status = status;
                c.SlaStartTime = DateTime.UtcNow.AddMinutes(-ageMinutes); c.CreatedAt = c.SlaStartTime;
                return c;
            }

            // A: one fresh open case, one far past its SLA (breached), one answered and waiting on the customer.
            ctx.Cases.Add(Make(w.Team, w.A, 5, CaseStatus.Open, "Voice"));
            ctx.Cases.Add(Make(w.Team, w.A, 600, CaseStatus.InProgress, "Email"));
            // B: one case resolved today (took 90 minutes).
            var resolved = Make(w.Team, w.B, 90, CaseStatus.Resolved, "Voice");
            resolved.ResolvedAt = DateTime.UtcNow; resolved.FirstResponseActualAt = resolved.CreatedAt.AddMinutes(1);
            ctx.Cases.Add(resolved);
            // Another team's case must not show up when filtering.
            ctx.Cases.Add(Make(other, w.C, 200, CaseStatus.Open, "Voice"));
            await ctx.SaveChangesAsync();
            // The audit base class stamps CreatedAt on insert; give the cases the ages the scenario describes.
            await ctx.Cases.ExecuteUpdateAsync(u => u.SetProperty(c => c.CreatedAt, c => c.SlaStartTime));
        }

        await using var read = w.Ctx();
        var monitor = Monitor(read, w);

        var team = await monitor.GetOverviewAsync(w.Team);
        Assert.Equal("Support", team.TeamName);
        Assert.Equal(4, team.Summary.TotalAgentsCount);

        var agentA = team.Agents.Single(a => a.UserId == w.A);
        Assert.Equal(2, agentA.OpenCasesCount);
        Assert.Equal(1, agentA.BreachedCasesCount);                    // the 600-minute-old case, by the SLA clock
        Assert.Equal(1, team.Agents.Single(a => a.UserId == w.B).HandledTodayCount);
        Assert.Equal(1, team.Summary.ResolvedTodayCount);
        Assert.InRange(team.Summary.AvgResolutionMinutes!.Value, 89, 91);

        Assert.Equal("Voice", team.Summary.LongestQueueChannel);       // only the fresh one is still waiting for a first answer
        Assert.InRange(team.Summary.LongestQueueWaitMinutes!.Value, 4, 7);
        var voice = team.Queues.Single(q => q.Channel == "Voice");
        Assert.Equal(1, voice.WaitingCount);
        Assert.Equal(1, voice.OpenCount);
        Assert.Contains(team.SlaAtRisk, c => c.Health == "Breached");

        // Occupancy = open cases held by available agents / their capacity (3 online × 5, plus the lead's own 5).
        Assert.InRange(team.Summary.OccupancyPercent!.Value, 10, 25);

        var all = await monitor.GetOverviewAsync();
        Assert.Equal(3, all.Agents.Single(a => a.UserId == w.C).OpenCasesCount + 2);   // C holds the other team's case (1) → proves unfiltered scope
        await Assert.ThrowsAsync<KeyNotFoundException>(() => monitor.GetOverviewAsync(Guid.NewGuid()));
    }
}
