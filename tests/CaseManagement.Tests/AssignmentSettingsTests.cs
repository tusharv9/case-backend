using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Configurable Settings → Assign &amp; Reassign (CM-05): what is saved is what Assign / Reassign enforces, on a real PostgreSQL database.
/// Reuses the teams of <see cref="AssignmentEligibilityTests.World"/>: Loans (lead + L1, L2 Away, L3 not auto-assignable),
/// Cards (lead + C1), Archived (inactive); global capacity 2.
/// </summary>
public class AssignmentSettingsTests
{
    private static AssignmentSettingsService Settings(AssignmentEligibilityTests.World w, Api.Data.AppDbContext ctx) => new(ctx, w.Engine(ctx));

    private static UpdateAssignmentSettingsDto From(AssignmentSettingsDto s, Action<UpdateAssignmentSettingsDto>? change = null)
    {
        var dto = new UpdateAssignmentSettingsDto
        {
            CrossTeamMode = s.CrossTeamMode, CrossTeamDepartmentIds = s.CrossTeamDepartmentIds.ToList(), IncludeTeamLeads = s.IncludeTeamLeads,
            RequireAvailable = s.RequireAvailable, EnforceCapacity = s.EnforceCapacity, DefaultAlgorithm = s.DefaultAlgorithm,
            DefaultCapacity = s.DefaultCapacity, ExpectedVersion = s.Version,
        };
        change?.Invoke(dto);
        return dto;
    }

    private static async Task<AssignmentSettingsDto> SaveAsync(AssignmentEligibilityTests.World w, Action<UpdateAssignmentSettingsDto> change)
    {
        await using var ctx = w.Ctx();
        var service = Settings(w, ctx);
        return await service.SaveAsync(From(await service.GetAsync(), change), w.Admin);
    }

    private static async Task<EligibleAssigneesDto> ListAsync(AssignmentEligibilityTests.World w, Guid caseId, bool mayAssignAny = false)
    {
        await using var ctx = w.Ctx();
        return await w.Cases(ctx).GetEligibleAssigneesAsync(caseId, mayAssignAny);
    }

    [PostgresFact]
    public async Task TheMigrationKeepsTodaysBehaviour_AndSavedSettingsSurviveAReload()
    {
        await using var w = await AssignmentEligibilityTests.World.CreateAsync();
        await using (var ctx = w.Ctx())
        {
            var initial = await Settings(w, ctx).GetAsync();
            Assert.Equal(CrossTeamModes.Disabled, initial.CrossTeamMode);
            Assert.True(initial.IncludeTeamLeads && initial.RequireAvailable && initial.EnforceCapacity);
            Assert.All(initial.Teams.SelectMany(t => t.Members), m => Assert.True(m.CanBeManuallyAssigned));
            Assert.Equal(2, initial.Teams.Single(t => t.Id == w.Loans).Capacity);
        }

        await SaveAsync(w, d => { d.CrossTeamMode = CrossTeamModes.SelectedTeams; d.CrossTeamDepartmentIds = new() { w.Cards }; d.IncludeTeamLeads = false; d.DefaultCapacity = 7; });

        await using var reload = w.Ctx();
        var saved = await Settings(w, reload).GetAsync();
        Assert.Equal(CrossTeamModes.SelectedTeams, saved.CrossTeamMode);
        Assert.Equal(new[] { w.Cards }, saved.CrossTeamDepartmentIds);
        Assert.False(saved.IncludeTeamLeads);
        Assert.Equal(7, saved.DefaultCapacity);
        // The default routing strategy is the SAME global row automatic routing uses (no second store).
        Assert.Equal(7, await reload.AssignmentConfigurations.Where(c => c.DepartmentId == null).Select(c => c.MaxConcurrentCapacity).SingleAsync());
        Assert.True(await reload.CaseEvents.AnyAsync(e => e.EntityName == "Assign & Reassign settings"));
    }

    [PostgresFact]
    public async Task AStaleEditor_CannotOverwriteANewerSave()
    {
        await using var w = await AssignmentEligibilityTests.World.CreateAsync();
        AssignmentSettingsDto loadedEarlier;
        await using (var ctx = w.Ctx()) loadedEarlier = await Settings(w, ctx).GetAsync();

        await SaveAsync(w, d => d.IncludeTeamLeads = false);   // someone else saves first

        await using var late = w.Ctx();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Settings(w, late).SaveAsync(From(loadedEarlier, d => d.RequireAvailable = false), w.Admin));
        var now = await Settings(w, late).GetAsync();
        Assert.False(now.IncludeTeamLeads);
        Assert.True(now.RequireAvailable);
    }

    [PostgresFact]
    public async Task SelectingMembers_AndExcludingLeads_ChangesWhoCanBeChosen_ButNotAutomaticRouting()
    {
        await using var w = await AssignmentEligibilityTests.World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L3);

        await SaveAsync(w, d => { d.IncludeTeamLeads = false; d.MemberSelections = new() { new() { DepartmentId = w.Loans, UserId = w.L1, CanBeManuallyAssigned = false } }; });

        var list = (await ListAsync(w, caseId)).Candidates.ToDictionary(c => c.UserId);
        Assert.Equal("NotSelected", list[w.L1].IneligibleReason);
        Assert.Equal("LeadExcluded", list[w.LoansLead].IneligibleReason);

        await using (var ctx = w.Ctx())
            Assert.Equal("NotSelected", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(
                () => w.Cases(ctx).AssignCaseAsync(caseId, new AssignCaseDto { OwnerId = w.L1, UserId = w.L3, Reason = "Workload rebalancing" }))).ReasonCode);

        // Automatic routing still gives L1 cases: "can be chosen" and "receives cases automatically" are separate settings.
        await using var check = w.Ctx();
        var pool = await new AgentPoolService(check).GetAsync(w.Loans, 5);
        Assert.Contains(pool.Eligible, e => e.User.Id == w.L1);
    }

    [PostgresFact]
    public async Task CrossTeam_IsOffByDefault_ThenOnlyForTheChosenTeams_AndNeedsTheAssignPermission()
    {
        await using var w = await AssignmentEligibilityTests.World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);
        AssignCaseDto ToC1() => new() { OwnerId = w.C1, UserId = w.L1, Reason = "Specialist required" };

        // Off (the default): Cards people are not offered and are refused.
        Assert.DoesNotContain((await ListAsync(w, caseId, true)).Candidates, c => c.UserId == w.C1);
        await using (var ctx = w.Ctx())
            Assert.Equal("NotTeamMember", (await Assert.ThrowsAsync<AssignmentNotAllowedException>(() => w.Cases(ctx).AssignCaseAsync(caseId, ToC1(), true))).ReasonCode);

        // Selected teams = Cards: offered as cross-team; without cases.assign the server refuses and the list says why.
        await SaveAsync(w, d => { d.CrossTeamMode = CrossTeamModes.SelectedTeams; d.CrossTeamDepartmentIds = new() { w.Cards }; });
        var offered = (await ListAsync(w, caseId)).Candidates.Single(c => c.UserId == w.C1);
        Assert.True(offered.IsCrossTeam);
        Assert.Equal("Cards", offered.HomeTeamName);
        Assert.Equal("NeedsAssignPermission", offered.IneligibleReason);
        await using (var ctx = w.Ctx())
            await Assert.ThrowsAsync<AssignmentForbiddenException>(() => w.Cases(ctx).AssignCaseAsync(caseId, ToC1(), callerMayAssignAnyCase: false));

        await using (var ctx = w.Ctx())
            await w.Cases(ctx).AssignCaseAsync(caseId, ToC1(), callerMayAssignAnyCase: true);
        var assigned = await w.GetAsync(caseId);
        Assert.Equal(w.C1, assigned.OwnerId);
        Assert.Equal(w.Loans, assigned.DepartmentId);   // cross-team assignment does not move the case (that is a transfer)
    }

    [PostgresFact]
    public async Task AvailabilityAndCapacityRules_CanBeRelaxed_ByTheAdministrator()
    {
        await using var w = await AssignmentEligibilityTests.World.CreateAsync();
        var caseId = await w.NewCaseAsync(w.Loans, w.L1);
        await w.NewCaseAsync(w.Loans, w.L3);
        await w.NewCaseAsync(w.Loans, w.L3);   // L3 at capacity (2)

        var before = (await ListAsync(w, caseId)).Candidates.ToDictionary(c => c.UserId);
        Assert.Equal("Unavailable", before[w.L2].IneligibleReason);
        Assert.Equal("AtCapacity", before[w.L3].IneligibleReason);

        await SaveAsync(w, d => { d.RequireAvailable = false; d.EnforceCapacity = false; });

        var after = (await ListAsync(w, caseId)).Candidates.ToDictionary(c => c.UserId);
        Assert.True(after[w.L2].IsEligible);
        Assert.True(after[w.L3].IsEligible);
    }

    [PostgresFact]
    public async Task InvalidSettings_AreRefused_AndNothingIsSaved()
    {
        await using var w = await AssignmentEligibilityTests.World.CreateAsync();

        var none = await Assert.ThrowsAsync<FieldValidationException>(() => SaveAsync(w, d => { d.CrossTeamMode = CrossTeamModes.SelectedTeams; d.CrossTeamDepartmentIds = new(); }));
        Assert.Equal("crossTeamDepartmentIds", none.Errors[0].Field);
        await Assert.ThrowsAsync<FieldValidationException>(() => SaveAsync(w, d => { d.CrossTeamMode = CrossTeamModes.SelectedTeams; d.CrossTeamDepartmentIds = new() { w.Archived }; }));
        await Assert.ThrowsAsync<FieldValidationException>(() => SaveAsync(w, d => d.CrossTeamMode = "Everyone"));
        await Assert.ThrowsAsync<FieldValidationException>(() => SaveAsync(w, d => d.MemberSelections = new() { new() { DepartmentId = w.Cards, UserId = w.L1, CanBeManuallyAssigned = false } }));

        // An invalid routing strategy rolls back the whole save (the policy change with it).
        await Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync(w, d => { d.IncludeTeamLeads = false; d.DefaultAlgorithm = "Lottery"; }));

        await using var ctx = w.Ctx();
        var unchanged = await Settings(w, ctx).GetAsync();
        Assert.True(unchanged.IncludeTeamLeads);
        Assert.Equal(CrossTeamModes.Disabled, unchanged.CrossTeamMode);
    }
}
