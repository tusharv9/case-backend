using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Phase 7: the one-off data reconciliation. A dry run writes nothing; applying fixes only what is safe (statuses, cases held outside
/// their team → the team's lead, blank sub-categories), audits every case it touches, and leaves decisions to people.
/// Reuses <see cref="AssignmentEligibilityTests.World"/> (Loans: lead + L1..L3; Cards: lead + C1; Archived: inactive).
/// </summary>
public class DataReconciliationTests
{
    private static async Task<(AssignmentEligibilityTests.World W, Guid Outsider, Guid HeldByLead, Guid Healthy, Guid InArchived)> SetupAsync()
    {
        var w = await AssignmentEligibilityTests.World.CreateAsync();
        var outsider = await w.NewCaseAsync(w.Loans, w.C1);          // C1 is in Cards, not Loans
        var heldByLead = await w.NewCaseAsync(w.Loans, w.LoansLead); // the lead may hold the team's cases
        var healthy = await w.NewCaseAsync(w.Loans, w.L1);
        var inArchived = await w.NewCaseAsync(w.Archived, w.L1);     // an inactive team: needs a person to decide

        await using var ctx = w.Ctx();
        await ctx.Database.ExecuteSqlRawAsync(@"UPDATE ""Users"" SET ""Status"" = '0' WHERE ""Name"" = 'Loan One'");
        await ctx.Database.ExecuteSqlRawAsync(@"UPDATE ""Users"" SET ""Status"" = 'Sleeping' WHERE ""Name"" = 'Card One'");
        await ctx.Cases.Where(c => c.Id == healthy).ExecuteUpdateAsync(u => u.SetProperty(c => c.Subcategory, "   "));
        await ctx.Cases.Where(c => c.Id == heldByLead).ExecuteUpdateAsync(u => u.SetProperty(c => c.Subcategory, "General Request"));
        return (w, outsider, heldByLead, healthy, inArchived);
    }

    private static async Task<string?> RawStatusAsync(AssignmentEligibilityTests.World w, string name) =>
        await w.Db.ScalarAsync<string>($"SELECT \"Status\" FROM \"Users\" WHERE \"Name\" = '{name}'");

    [PostgresFact]
    public async Task ADryRun_ReportsEverything_AndWritesNothing()
    {
        var (w, outsider, _, _, inArchived) = await SetupAsync();
        await using var _ = w;
        await using var ctx = w.Ctx();

        var report = await new DataReconciliationService(ctx, w.Engine(ctx)).RunAsync(apply: false, actorId: null);

        Assert.False(report.Applied);
        Assert.Contains(report.UserStatuses, s => s.User == "Loan One" && s.StoredValue == "0" && s.NewValue == "Available");
        Assert.Contains(report.UserStatuses, s => s.User == "Card One" && s.NewValue == "Offline");   // unknown → never offered for work
        Assert.Contains(report.CasesHeldOutsideTheirTeam, f => f.CaseId == outsider && f.NewHolder == "Loans Lead");
        Assert.Contains(report.CasesNeedingADecision, f => f.CaseId == inArchived);
        Assert.Equal(1, report.BlankSubcategoriesToClear);
        Assert.Contains(report.UnknownSubcategories, u => u.Subcategory == "General Request" && u.OpenCases == 1);
        Assert.Contains(report.Teams, t => t.Team == "Archived" && t.Assessment.StartsWith("Inactive but still referenced"));

        // Nothing changed.
        Assert.Equal("0", await RawStatusAsync(w, "Loan One"));
        Assert.Equal(w.C1, (await w.GetAsync(outsider)).OwnerId);
        Assert.False(await ctx.CaseEvents.AnyAsync(e => e.CaseId == outsider));
    }

    [PostgresFact]
    public async Task Applying_FixesOnlyWhatIsSafe_AndAuditsEveryCaseItMoves()
    {
        var (w, outsider, heldByLead, healthy, inArchived) = await SetupAsync();
        await using var _ = w;
        await using (var ctx = w.Ctx())
            await new DataReconciliationService(ctx, w.Engine(ctx)).RunAsync(apply: true, actorId: w.Admin);

        Assert.Equal("Available", await RawStatusAsync(w, "Loan One"));
        Assert.Equal("Offline", await RawStatusAsync(w, "Card One"));

        var moved = await w.GetAsync(outsider);
        Assert.Equal(w.LoansLead, moved.OwnerId);
        await using var check = w.Ctx();
        var evt = await check.CaseEvents.SingleAsync(e => e.CaseId == outsider && e.EventType == EventType.Assign);
        Assert.Equal(w.Admin, evt.UserId);
        Assert.Contains("data reconciliation", evt.Message);

        Assert.Equal(w.LoansLead, (await w.GetAsync(heldByLead)).OwnerId);               // already valid: untouched
        Assert.Equal("General Request", (await w.GetAsync(heldByLead)).Subcategory);    // needs a decision: untouched
        Assert.Null((await w.GetAsync(healthy)).Subcategory);                            // blank → cleared
        Assert.Equal(w.L1, (await w.GetAsync(inArchived)).OwnerId);                      // inactive team: left for a person

        // Running again finds nothing left to fix automatically.
        await using var again = w.Ctx();
        var second = await new DataReconciliationService(again, w.Engine(again)).RunAsync(apply: false, actorId: null);
        Assert.Empty(second.UserStatuses);
        Assert.Empty(second.CasesHeldOutsideTheirTeam);
        Assert.Equal(0, second.BlankSubcategoriesToClear);
    }

    [PostgresFact]
    public async Task Applying_NeedsAnActor()
    {
        var (w, _, _, _, _) = await SetupAsync();
        await using var _w = w;
        await using var ctx = w.Ctx();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DataReconciliationService(ctx, w.Engine(ctx)).RunAsync(apply: true, actorId: null));
    }

    [PostgresFact]
    public async Task CasesGoToAnAvailableAgentWithRoom_BeforeTheLeadHoldsTheRest()
    {
        var (w, outsider, _, _, _) = await SetupAsync();
        await using var _w = w;
        var second = await w.NewCaseAsync(w.Loans, w.C1);   // another Loans case held by a Cards agent
        await using (var ctx = w.Ctx())
            await ctx.AssignmentConfigurations.Where(c => c.DepartmentId == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.MaxConcurrentCapacity, 3));

        // L1 is the only available, assignable Loans agent and holds 2 open cases (capacity 3): the oldest case goes to L1,
        // after which L1 is full and the team lead holds the next one.
        await using (var ctx = w.Ctx())
        {
            var dry = await new DataReconciliationService(ctx, w.Engine(ctx)).RunAsync(apply: false, actorId: null);
            Assert.Equal("Loan One", dry.CasesHeldOutsideTheirTeam.Single(f => f.CaseId == outsider).NewHolder);
            var held = dry.CasesHeldOutsideTheirTeam.Single(f => f.CaseId == second);
            Assert.Equal("Loans Lead", held.NewHolder);
            Assert.StartsWith("held by the team lead", held.How);
        }
        Assert.Equal(w.C1, (await w.GetAsync(outsider)).OwnerId);   // the dry run kept nothing

        await using (var ctx = w.Ctx())
            await new DataReconciliationService(ctx, w.Engine(ctx)).RunAsync(apply: true, actorId: w.Admin);
        Assert.Equal(w.L1, (await w.GetAsync(outsider)).OwnerId);
        Assert.Equal(w.LoansLead, (await w.GetAsync(second)).OwnerId);
    }
}
