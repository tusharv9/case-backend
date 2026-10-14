using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Strategies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// Manual assign / reassign / transfer use the SAME eligibility rules as automatic routing, enforced by the server
/// (Phase 1 of the CM transformation). On a real migrated PostgreSQL database.
/// </summary>
public partial class AssignmentEligibilityTests
{
    internal sealed class World : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public Guid Loans, Cards, Archived;              // three teams (Archived is inactive)
        public Guid LoansLead, L1, L2, L3, CardsLead, C1, Outsider, Admin;
        public Guid Customer;

        public AppDbContext Ctx() => Db.NewContext();

        public static async Task<World> CreateAsync()
        {
            var w = new World { Db = await TempDatabase.CreateAsync() };
            await using var ctx = w.Db.NewContext();
            await ctx.Database.MigrateAsync();
            DbSeeder.Run(ctx, SeedMode.Bootstrap, adoptedLegacyDatabase: false);
            await ctx.AssignmentConfigurations.Where(c => c.DepartmentId == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Algorithm, "LeastOccupancy").SetProperty(c => c.MaxConcurrentCapacity, 2));

            w.Loans = Guid.NewGuid(); w.Cards = Guid.NewGuid(); w.Archived = Guid.NewGuid();
            ctx.Departments.AddRange(
                new Department { Id = w.Loans, Name = "Loans", Code = "LN", CreatedAt = DateTime.UtcNow, IsActive = true },
                new Department { Id = w.Cards, Name = "Cards", Code = "CD", CreatedAt = DateTime.UtcNow, IsActive = true },
                new Department { Id = w.Archived, Name = "Archived", Code = "AR", CreatedAt = DateTime.UtcNow, IsActive = false });

            Guid U(string name, UserStatus status = UserStatus.Available, bool active = true)
            {
                var id = Guid.NewGuid();
                ctx.Users.Add(new User { Id = id, Name = name, Email = name.ToLower().Replace(' ', '.') + "@x.test", Role = "Agent", Status = status, IsActive = active, CreatedAt = DateTime.UtcNow });
                return id;
            }
            w.LoansLead = U("Loans Lead"); w.L1 = U("Loan One"); w.L2 = U("Loan Two", UserStatus.Away); w.L3 = U("Loan Three");
            w.CardsLead = U("Cards Lead"); w.C1 = U("Card One"); w.Outsider = U("Out Sider"); w.Admin = U("Ad Min");
            w.Customer = Guid.NewGuid();
            ctx.Customers.Add(new Customer { Id = w.Customer, FullName = "Cust", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60" + Random.Shared.NextInt64(1000000000, 1999999999), CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();

            await ctx.Departments.Where(d => d.Id == w.Loans).ExecuteUpdateAsync(u => u.SetProperty(d => d.OwnerId, w.LoansLead));
            await ctx.Departments.Where(d => d.Id == w.Cards).ExecuteUpdateAsync(u => u.SetProperty(d => d.OwnerId, w.CardsLead));

            void M(Guid team, Guid user, bool assignable = true, string role = "Agent") =>
                ctx.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), DepartmentId = team, UserId = user, MemberRole = role, IsAssignable = assignable, IsActive = true, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
            M(w.Loans, w.LoansLead, assignable: false, role: "Team Lead");
            M(w.Loans, w.L1); M(w.Loans, w.L2); M(w.Loans, w.L3, assignable: false);   // L3 opted out of AUTOMATIC routing only
            M(w.Cards, w.CardsLead, assignable: false, role: "Team Lead");
            M(w.Cards, w.C1);
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

        public CaseService Cases(AppDbContext ctx)
        {
            var env = new Mock<IWebHostEnvironment>();
            env.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());
            return new CaseService(new CaseRepository(ctx), new Mock<INotificationService>().Object, new Mock<IConfigurableSettingsService>().Object,
                ctx, env.Object, routingEngine: Engine(ctx));
        }

        /// <summary>Stores a case directly (no routing) so each test controls exactly who holds what.</summary>
        public async Task<Guid> NewCaseAsync(Guid team, Guid owner, CaseStatus status = CaseStatus.Open)
        {
            await using var ctx = Ctx();
            var id = Guid.NewGuid();
            ctx.Cases.Add(new Case
            {
                Id = id, CaseNumber = "T-" + Guid.NewGuid().ToString("N")[..8], Title = "Loan query", CaseType = "Inquiry", Severity = "High",
                DepartmentId = team, OwnerId = owner, CustomerId = Customer, Status = status, SlaStartTime = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
                FirstResponseTargetMinutes = 30, InternalResolutionTargetMinutes = 120, ExternalResolutionTargetMinutes = 240, FirstResponseStatus = "Pending",
            });
            await ctx.SaveChangesAsync();
            return id;
        }

        public async Task<Case> GetAsync(Guid id)
        {
            await using var ctx = Ctx();
            return await ctx.Cases.AsNoTracking().FirstAsync(c => c.Id == id);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    private static AssignCaseDto Assign(Guid to, Guid by, string reason = "Workload rebalancing") => new() { OwnerId = to, UserId = by, Reason = reason };

    // ------------------------------------------------------------------------------------------ the list the UI shows

    [PostgresFact]
    public async Task EligibleAssignees_AreTheCasesTeamOnly_WithRealStatusWorkloadAndReasons()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);
        await w.NewCaseAsync(w.Loans, w.L1);   // L1 now holds 2 = capacity

        await using var ctx = w.Ctx();
        var list = await w.Cases(ctx).GetEligibleAssigneesAsync(caseId);

        Assert.Equal("Loans", list.DepartmentName);
        Assert.Equal(2, list.Capacity);
        // Members and the lead of Loans — never people from Cards or outside every team.
        Assert.Equal(new[] { w.LoansLead, w.L1, w.L2, w.L3 }.OrderBy(x => x), list.Candidates.Select(c => c.UserId).OrderBy(x => x));

        var byId = list.Candidates.ToDictionary(c => c.UserId);
        Assert.True(byId[w.LoansLead].IsEligible && byId[w.LoansLead].IsTeamLead);
        Assert.True(byId[w.L3].IsEligible);                                    // not auto-assignable, but a person may choose them
        Assert.Equal("CurrentOwner", byId[w.L1].IneligibleReason);
        Assert.Equal(2, byId[w.L1].OpenCases);
        Assert.Equal("Unavailable", byId[w.L2].IneligibleReason);
        Assert.Equal("Away", byId[w.L2].Status);                               // straight from Users.Status, not invented
        Assert.True(list.Candidates.First().IsEligible);                       // eligible people are listed first
    }

    // ------------------------------------------------------------------------------------------ the server enforces it

    [PostgresFact]
    public async Task Assigning_ToSomeoneOutsideTheCasesTeam_IsRejected_AndNothingChanges()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);

        foreach (var target in new[] { w.C1, w.Outsider })
        {
            await using var ctx = w.Ctx();
            var ex = await Assert.ThrowsAsync<AssignmentNotAllowedException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(target, w.L1)));
            Assert.Equal("NotTeamMember", ex.ReasonCode);
        }

        Assert.Equal(w.L1, (await w.GetAsync(caseId)).OwnerId);
        await using var check = w.Ctx();
        Assert.False(await check.CaseEvents.AnyAsync(e => e.CaseId == caseId && e.EventType == EventType.Assign));
    }

    [PostgresFact]
    public async Task Assigning_ToAnUnavailableDeactivatedOrFullAgent_IsRejectedWithTheReason()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);

        await using (var ctx = w.Ctx())
            Assert.Equal("Unavailable", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L2, w.L1)))).ReasonCode);

        await using (var ctx = w.Ctx())
        {
            await ctx.Users.Where(u => u.Id == w.L3).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
            Assert.Equal("UserInactive", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L1)))).ReasonCode);
        }

        await w.NewCaseAsync(w.Loans, w.LoansLead);
        await w.NewCaseAsync(w.Loans, w.LoansLead);   // the lead is at capacity (2)
        await using (var ctx = w.Ctx())
            Assert.Equal("AtCapacity", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.LoansLead, w.L1)))).ReasonCode);

        Assert.Equal(w.L1, (await w.GetAsync(caseId)).OwnerId);
    }

    [PostgresFact]
    public async Task Assigning_ToAnEligibleMember_Works_KeepsTheSlaClock_AndRecordsTheHistory()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);
        var before = await w.GetAsync(caseId);

        await using (var ctx = w.Ctx())
            await w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L1, "Specialist required"));

        var after = await w.GetAsync(caseId);
        Assert.Equal(w.L3, after.OwnerId);
        Assert.Equal(before.SlaStartTime, after.SlaStartTime);

        await using var check = w.Ctx();
        var evt = await check.CaseEvents.SingleAsync(e => e.CaseId == caseId && e.EventType == EventType.Assign);
        Assert.Contains("Loan Three", evt.Message);
        Assert.Contains("Specialist required", evt.Message);
        Assert.Equal(w.L1, evt.UserId);
    }

    // ------------------------------------------------------------------------------------------ who may reassign

    [PostgresFact]
    public async Task OnlyTheOwnerTeamMembersTheLeadOrCasesAssign_MayChangeTheHolder()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);

        // Someone from another team, without cases.assign: refused before anything else is checked.
        await using (var ctx = w.Ctx())
            await Assert.ThrowsAsync<AssignmentForbiddenException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.C1)));
        await using (var ctx = w.Ctx())
            await Assert.ThrowsAsync<AssignmentForbiddenException>(() => w.Cases(ctx).TransferDepartmentAsync(caseId, new TransferDepartmentDto { DepartmentId = w.Cards, Reason = "Incorrect routing" }, w.C1));

        // A team member may.
        await using (var ctx = w.Ctx())
            await w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L2));
        // So may the holder of cases.assign — but still only to someone eligible.
        await using (var ctx = w.Ctx())
            await w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.LoansLead, w.Admin), callerMayAssignAnyCase: true);
        await using (var ctx = w.Ctx())
            await Assert.ThrowsAsync<AssignmentNotAllowedException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.C1, w.Admin), callerMayAssignAnyCase: true));

        Assert.Equal(w.LoansLead, (await w.GetAsync(caseId)).OwnerId);
    }

    [PostgresFact]
    public async Task ClosedCases_CannotBeReassigned()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1, CaseStatus.Closed);
        await using var ctx = w.Ctx();
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L1)));
    }

    // ------------------------------------------------------------------------------------------ transfer

    [PostgresFact]
    public async Task Transfer_GoesToTheReceivingLead_AndEligibilityFollowsTheNewTeam()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);

        await using (var ctx = w.Ctx())
            await w.Cases(ctx).TransferDepartmentAsync(caseId, new TransferDepartmentDto { DepartmentId = w.Cards, Reason = "Incorrect routing" }, w.L1);

        var moved = await w.GetAsync(caseId);
        Assert.Equal(w.Cards, moved.DepartmentId);
        Assert.Equal(w.CardsLead, moved.OwnerId);

        // The team changed, so the old team's people are no longer valid holders; the new team's are.
        await using (var ctx = w.Ctx())
            Assert.Equal("NotTeamMember", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(
                () => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.CardsLead)))).ReasonCode);
        await using (var ctx = w.Ctx())
            await w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.C1, w.CardsLead));
        Assert.Equal(w.C1, (await w.GetAsync(caseId)).OwnerId);
    }

    [PostgresFact]
    public async Task Transfer_ToATeamWithoutALead_UsesTheTeamsAlgorithm_AndNeverLeavesTheCaseWithAnOutsider()
    {
        await using var w = await World.CreateAsync();
        await using (var ctx = w.Ctx())
            await ctx.Departments.Where(d => d.Id == w.Cards).ExecuteUpdateAsync(u => u.SetProperty(d => d.OwnerId, (Guid?)null));

        var first = await w.NewCaseAsync(w.Loans, w.L1);
        await using (var ctx = w.Ctx())
            await w.Cases(ctx).TransferDepartmentAsync(first, new TransferDepartmentDto { DepartmentId = w.Cards, Reason = "Incorrect routing" }, w.L1);
        Assert.Equal(w.C1, (await w.GetAsync(first)).OwnerId);   // the only assignable Cards member

        // Card One goes Away: nobody in Cards can take a case, and there is no lead — the transfer is refused, nothing changes.
        await using (var ctx = w.Ctx())
            await ctx.Users.Where(u => u.Id == w.C1).ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, UserStatus.Away));
        var second = await w.NewCaseAsync(w.Loans, w.L1);
        await using (var ctx = w.Ctx())
            Assert.Equal("NoEligibleAssignee", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(
                () => w.Cases(ctx).TransferDepartmentAsync(second, new TransferDepartmentDto { DepartmentId = w.Cards, Reason = "Incorrect routing" }, w.L1))).ReasonCode);

        var unchanged = await w.GetAsync(second);
        Assert.Equal(w.Loans, unchanged.DepartmentId);
        Assert.Equal(w.L1, unchanged.OwnerId);
    }

    [PostgresFact]
    public async Task Transfer_ToAnInactiveTeam_IsRefused()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);
        await using var ctx = w.Ctx();
        Assert.Equal("TeamInactive", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(
            () => w.Cases(ctx).TransferDepartmentAsync(caseId, new TransferDepartmentDto { DepartmentId = w.Archived, Reason = "Incorrect routing" }, w.L1))).ReasonCode);
        Assert.Equal(w.Loans, (await w.GetAsync(caseId)).DepartmentId);
    }

    // ------------------------------------------------------------------------------------------ reasons (CM-06)

    [PostgresFact]
    public async Task Reasons_MustComeFromTheConfiguredList_AndOtherNeedsAnExplanation()
    {
        await using var w = await World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);

        await using (var ctx = w.Ctx())
        {
            var unknown = await Assert.ThrowsAsync<FieldValidationException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L1, "Because I said so")));
            Assert.Equal("reason", unknown.Errors[0].Field);
            var noDetail = await Assert.ThrowsAsync<FieldValidationException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L1, "Others")));
            Assert.Equal("reasonDetail", noDetail.Errors[0].Field);
        }

        await using (var ctx = w.Ctx())
            await w.Cases(ctx).AssignCaseAsync(caseId, new AssignCaseDto { OwnerId = w.L3, UserId = w.L1, Reason = "Others", ReasonDetail = "Customer asked for a Malay speaker" });

        await using var check = w.Ctx();
        var evt = await check.CaseEvents.SingleAsync(e => e.CaseId == caseId && e.EventType == EventType.Assign);
        Assert.Contains("Others — Customer asked for a Malay speaker", evt.Message);
    }

    [PostgresFact]
    public async Task EscalationHasItsOwnReasons_AndIsNoLongerAnAssignmentReason()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();

        var escalationReasons = await ctx.LookupValues.Where(v => v.TypeCode == "ESCALATION_REASON" && v.IsActive).ToListAsync();
        Assert.Contains(escalationReasons, v => v.Value == "Other" && v.RequiresDetail);
        Assert.True(escalationReasons.Count >= 2);

        // Deactivated, not deleted: history that used it stays readable and an administrator can turn it back on.
        var assignmentEscalation = await ctx.LookupValues.SingleAsync(v => v.TypeCode == "ASSIGNMENT_REASON" && v.Value == "Escalation");
        Assert.False(assignmentEscalation.IsActive);

        var caseId = await w.NewCaseAsync(w.Loans, w.L1);
        await Assert.ThrowsAsync<FieldValidationException>(() => w.Cases(ctx).AssignCaseAsync(caseId, Assign(w.L3, w.L1, "Escalation")));
    }

    // ------------------------------------------------------------------------------------------ one rule set

    [PostgresFact]
    public async Task AutomaticRouting_AndTheManualList_JudgeAgentsTheSameWay()
    {
        await using var w = await World.CreateAsync();
        await using var ctx = w.Ctx();
        var eligibility = new AssignmentEligibilityService(ctx);

        var automatic = await new AgentPoolService(eligibility).GetAsync(w.Loans, 2);
        var manual = await eligibility.EvaluateTeamAsync(w.Loans, await eligibility.GetManualPolicyAsync());

        // Automatic: only assignable, online members (L1). Manual adds the lead and L3 (not auto-assignable), never Away L2.
        Assert.Equal(new[] { w.L1 }, automatic.Eligible.Select(e => e.User.Id).ToArray());
        Assert.Equal(new[] { w.LoansLead, w.L1, w.L3 }.OrderBy(x => x), manual.Eligible.Select(e => e.User.Id).OrderBy(x => x));
    }
}
