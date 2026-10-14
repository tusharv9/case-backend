using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Reconciling the case classification with the business workbook: categories = teams (existing ones renamed in place),
/// sub-categories with their workbook ID, description and default priority, and the SLA matrix. On a real migrated PostgreSQL
/// database. Reuses <see cref="PriorityConfigurationTests.World"/>: "Contact Center" (CC) with Fraud + Payment Issue,
/// "Fraud Ops" (FO) with Fraud, the four bootstrap priorities.
/// </summary>
public class WorkbookReconciliationTests
{
    private const string Categories = """
        Case Categories & Sub-categories — Banking,,,,,,
        "Draft for Tech Team review.",,,,,,
        ,,,,,,
        Category ID,Category,Sub-category ID,Sub-category,Description,Default Priority,Remarks
        FD,Fraud & Disputes,FD-01,Unauthorised transaction,Customer disputes a transaction they did not make,Critical,
        FD,Fraud & Disputes,FD-02,Card fraud,"Suspected card compromise, or skimming",Critical,
        CF,Complaints & Feedback,CF-01,Staff conduct,Complaint about staff behaviour or conduct,High,
        KC,KYC & Compliance,KC-01,Document update,Update expired or missing identity documents,Medium,
        """;

    private const string Sla = """
        SLA Matrix (proposed),,,,
        ,,,,
        Priority,Response SLA (hrs),Resolution SLA (hrs),,
        Critical,1,4,,
        High,2,24,,
        Medium,4,48,,
        Low,8,120,,
        ,,,,
        Blue cells are inputs.,,,,
        """;

    private sealed class Setup : IAsyncDisposable
    {
        public PriorityConfigurationTests.World W = null!;
        public string Folder = null!;
        public Guid Sales, SalesRule, FraudRule, CaseInFraudOps;
        public WorkbookReconciliationService.Workbook Workbook = null!;

        public WorkbookReconciliationService.Decisions Decisions() => new()
        {
            RenameTeams = new(StringComparer.OrdinalIgnoreCase) { ["FD"] = "FO", ["CF"] = "CC" },
            DeactivateTeams = new() { "SA" },
        };

        public async ValueTask DisposeAsync()
        {
            await W.DisposeAsync();
            try { Directory.Delete(Folder, true); } catch (IOException) { }
        }
    }

    private static async Task<Setup> CreateAsync(string? categories = null)
    {
        var s = new Setup { W = await PriorityConfigurationTests.World.CreateAsync() };
        s.Folder = Directory.CreateTempSubdirectory("workbook-").FullName;
        await File.WriteAllTextAsync(Path.Combine(s.Folder, "Case Categories-Table 1.csv"), categories ?? Categories);
        await File.WriteAllTextAsync(Path.Combine(s.Folder, "SLA Matrix-Table 1.csv"), Sla);

        s.Sales = Guid.NewGuid(); s.SalesRule = Guid.NewGuid(); s.FraudRule = Guid.NewGuid(); s.CaseInFraudOps = Guid.NewGuid();
        await using var ctx = s.W.Db.NewContext();
        ctx.Departments.Add(new Department { Id = s.Sales, Name = "Sales", Code = "SA", IsActive = true, CreatedAt = DateTime.UtcNow });
        ctx.RoutingRules.AddRange(
            new RoutingRule { Id = s.FraudRule, Name = "Fraud desk", ConditionsJson = "{\"MatchType\":\"ANY\",\"Department\":\"Fraud Ops\",\"Category\":\"Fraud\"}", TargetDepartmentId = s.W.DeptB, IsActive = true, CreatedAt = DateTime.UtcNow },
            new RoutingRule { Id = s.SalesRule, Name = "Sales desk", ConditionsJson = "{\"MatchType\":\"ALL\",\"Department\":\"SA\"}", TargetDepartmentId = s.Sales, IsActive = true, CreatedAt = DateTime.UtcNow });
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60" + Random.Shared.NextInt64(1000000000, 1999999999), CreatedAt = DateTime.UtcNow };
        ctx.Customers.Add(customer);
        ctx.Cases.Add(new Case
        {
            Id = s.CaseInFraudOps, CaseNumber = "T-" + Guid.NewGuid().ToString("N")[..8], Title = "t", CaseType = "Complaint", Severity = "High",
            Subcategory = "Fraud", DepartmentId = s.W.DeptB, OwnerId = s.W.Actor, CustomerId = customer.Id, Status = CaseStatus.Open,
            SlaStartTime = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, FirstResponseStatus = "Pending",
        });
        await ctx.SaveChangesAsync();
        s.Workbook = WorkbookReconciliationService.ReadWorkbook(s.Folder);
        return s;
    }

    [Fact]
    public void TheWorksheets_AreReadFromTheirHeaderRows_IncludingQuotedDescriptions()
    {
        var folder = Directory.CreateTempSubdirectory("workbook-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Case Categories-Table 1.csv"), Categories);
            File.WriteAllText(Path.Combine(folder, "SLA Matrix-Table 1.csv"), Sla);
            var wb = WorkbookReconciliationService.ReadWorkbook(folder);

            Assert.Equal(4, wb.Rows.Count);
            Assert.Equal("Suspected card compromise, or skimming", wb.Rows[1].Description);
            Assert.Equal(new[] { "Critical", "High", "Medium", "Low" }, wb.Sla.Select(r => r.Priority));
            Assert.Equal(120m, wb.Sla[3].ResolutionHours);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AnInconsistentWorkbook_IsRefused()
    {
        var folder = Directory.CreateTempSubdirectory("workbook-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Case Categories.csv"), Categories
                .Replace("CF,Complaints & Feedback,CF-01", "CF,Complaints & Feedback,KC-09")
                .Replace("Update expired or missing identity documents,Medium", "Update expired or missing identity documents,Someday"));
            File.WriteAllText(Path.Combine(folder, "SLA Matrix.csv"), Sla);
            var ex = Assert.Throws<InvalidOperationException>(() => WorkbookReconciliationService.ReadWorkbook(folder));
            Assert.Contains("KC-09 does not belong to category CF", ex.Message);
            Assert.Contains("'Someday' is not in the 'SLA Matrix'", ex.Message);
        }
        finally { Directory.Delete(folder, true); }
    }

    [PostgresFact]
    public async Task ADryRun_ReportsThePlan_AndWritesNothing()
    {
        await using var s = await CreateAsync();
        await using var ctx = s.W.Db.NewContext();

        var report = await new WorkbookReconciliationService(ctx).RunAsync(s.Workbook, s.Decisions(), apply: false, actorId: null);

        Assert.False(report.Applied);
        Assert.Contains(report.Teams, t => t.Action == "Renamed" && t.Code == "FD" && t.Name == "Fraud & Disputes" && t.Before == "Fraud Ops (FO)");
        Assert.Contains(report.Teams, t => t.Action == "Renamed" && t.Code == "CF" && t.Before == "Contact Center (CC)");
        Assert.Contains(report.Teams, t => t.Action == "Created" && t.Code == "KC");
        Assert.Contains(report.Teams, t => t.Action == "Deactivated" && t.Code == "SA");
        Assert.Equal(4, report.SubCategories.Count(c => c.Action == "Created"));
        Assert.Equal(3, report.RetiredSubCategories.Count);
        Assert.Contains(report.RetiredSubCategories, r => r.Team == "Fraud & Disputes" && r.Name == "Fraud" && r.Cases == 1 && r.OpenCases == 1);
        Assert.Equal(3, report.After.ActiveDepartments);
        Assert.Equal(4, report.After.ActiveMappedSubCategories);

        // Nothing changed.
        await using var check = s.W.Db.NewContext();
        Assert.Equal("Fraud Ops", (await check.Departments.SingleAsync(d => d.Id == s.W.DeptB)).Name);
        Assert.True((await check.Departments.SingleAsync(d => d.Id == s.Sales)).IsActive);
        Assert.False(await check.DepartmentSubCategories.AnyAsync(c => c.Code == "FD-01"));
        Assert.Equal(30, (await check.PrioritySlaRules.SingleAsync(r => r.Priority == "Critical")).FirstResponseMinutes);
    }

    [PostgresFact]
    public async Task Applying_ReconcilesTeamsSubCategoriesAndSla_KeepsHistory_AndASecondRunFindsNothing()
    {
        await using var s = await CreateAsync();
        int internalBefore, versionBefore;
        await using (var ctx = s.W.Db.NewContext())
        {
            var before = await ctx.PrioritySlaRules.AsNoTracking().SingleAsync(r => r.Priority == "Critical");
            (internalBefore, versionBefore) = (before.InternalResolutionMinutes, before.Version);
            await new WorkbookReconciliationService(ctx).RunAsync(s.Workbook, s.Decisions(), apply: true, actorId: s.W.Actor);
        }

        await using var check = s.W.Db.NewContext();
        // Teams: renamed in place (same id, so members, cases and history stay), new categories created, the extra one archived.
        var fd = await check.Departments.SingleAsync(d => d.Id == s.W.DeptB);
        Assert.Equal(("Fraud & Disputes", "FD", true), (fd.Name, fd.Code, fd.IsActive));
        Assert.Equal("Complaints & Feedback", (await check.Departments.SingleAsync(d => d.Id == s.W.DeptA)).Name);
        Assert.True(await check.Departments.AnyAsync(d => d.Code == "KC" && d.Name == "KYC & Compliance" && d.IsActive));
        Assert.False((await check.Departments.SingleAsync(d => d.Id == s.Sales)).IsActive);

        // Routing rules follow the rename; the archived team's rule is switched off.
        Assert.Contains("\"Department\":\"Fraud & Disputes\"", (await check.RoutingRules.SingleAsync(r => r.Id == s.FraudRule)).ConditionsJson);
        Assert.False((await check.RoutingRules.SingleAsync(r => r.Id == s.SalesRule)).IsActive);

        // Sub-categories: workbook ID, description, order and default priority; old ones deactivated, cases untouched.
        var fd01 = await check.DepartmentSubCategories.SingleAsync(c => c.Code == "FD-01");
        Assert.Equal((s.W.DeptB, "Unauthorised transaction", "Customer disputes a transaction they did not make", 1, true), (fd01.DepartmentId, fd01.Name, fd01.Description, fd01.DisplayOrder, fd01.IsActive));
        Assert.Equal("Critical", await check.PriorityCategoryMappings.Where(m => m.DepartmentSubCategoryId == fd01.Id).Select(m => m.PrioritySlaRule.Priority).SingleAsync());
        Assert.Equal(2, (await check.DepartmentSubCategories.SingleAsync(c => c.Code == "FD-02")).DisplayOrder);
        Assert.False((await check.DepartmentSubCategories.SingleAsync(c => c.Id == s.W.FraudB)).IsActive);
        Assert.Equal("Fraud", (await check.Cases.SingleAsync(c => c.Id == s.CaseInFraudOps)).Subcategory);
        Assert.Equal(4, await check.DepartmentSubCategories.CountAsync(c => c.IsActive));

        // SLA: response → first response, resolution → external; internal kept; the version moves so open cases keep their snapshot.
        var critical = await check.PrioritySlaRules.SingleAsync(r => r.Priority == "Critical");
        Assert.Equal((60, 240, internalBefore, versionBefore + 1), (critical.FirstResponseMinutes, critical.ExternalResolutionMinutes, critical.InternalResolutionMinutes, critical.Version));
        Assert.Equal(120 * 60, (await check.PrioritySlaRules.SingleAsync(r => r.Priority == "Low")).ExternalResolutionMinutes);

        // Audited against the actor.
        var events = await check.CaseEvents.Where(e => e.Message.Contains("(workbook reconciliation)")).ToListAsync();
        Assert.All(events, e => Assert.Equal(s.W.Actor, e.UserId));
        Assert.Contains(events, e => e.Module == "Teams" && e.Message.StartsWith("Renamed team 'Fraud Ops' (FO) to 'Fraud & Disputes' (FD)"));
        Assert.Contains(events, e => e.Module == "SlaEscalation" && e.EntityName == "Priority Critical");

        // Re-running finds nothing to do.
        await using var again = s.W.Db.NewContext();
        var second = await new WorkbookReconciliationService(again).RunAsync(s.Workbook, s.Decisions(), apply: false, actorId: null);
        Assert.All(second.Teams, t => Assert.StartsWith("Unchanged", t.Action));
        Assert.All(second.SubCategories, c => Assert.Equal("Unchanged", c.Action));
        Assert.Empty(second.RetiredSubCategories);
        Assert.Empty(second.RoutingRules);
        Assert.All(second.Priorities, p => Assert.Equal("Unchanged", p.Action));
    }

    [PostgresFact]
    public async Task AfterApplying_ChoosingASubCategory_ResolvesItsDefaultPriority_AndThatPrioritysSla()
    {
        await using var s = await CreateAsync();
        await using (var ctx = s.W.Db.NewContext())
            await new WorkbookReconciliationService(ctx).RunAsync(s.Workbook, s.Decisions(), apply: true, actorId: s.W.Actor);

        // The workbook's default priority is authoritative, whatever was requested.
        var resolution = await s.W.Sla.ResolveEffectivePriorityAsync(s.W.DeptB, "Unauthorised transaction", "Low");
        Assert.Equal(("Critical", "SubCategoryMapping"), (resolution.Priority, resolution.Source));
        var preview = await s.W.Sla.PreviewPriorityAsync(s.W.DeptB, "Unauthorised transaction");
        Assert.True(preview.IsMapped);
        Assert.Equal(4, preview.ExternalHours);

        // A retired sub-category is no longer offered.
        var offered = await s.W.Settings.GetSubCategoriesAsync(s.W.DeptB, activeOnly: true);
        Assert.Equal(new[] { "FD-01", "FD-02" }, offered.Select(o => o.Code).OrderBy(c => c));
    }

    [PostgresFact]
    public async Task AnActiveTeamOutsideTheWorkbook_NeedsADecision_AndNothingIsWritten()
    {
        await using var s = await CreateAsync();
        var decisions = s.Decisions();
        decisions.DeactivateTeams.Clear();
        await using (var ctx = s.W.Db.NewContext())
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkbookReconciliationService(ctx).RunAsync(s.Workbook, decisions, apply: true, actorId: s.W.Actor));
            Assert.Contains("Team 'Sales' (SA) is active but is not a workbook category", ex.Message);
        }
        await using var check = s.W.Db.NewContext();
        Assert.Equal("Fraud Ops", (await check.Departments.SingleAsync(d => d.Id == s.W.DeptB)).Name);
        Assert.False(await check.Departments.AnyAsync(d => d.Code == "KC"));
    }

    [PostgresFact]
    public async Task Applying_NeedsAnActor()
    {
        await using var s = await CreateAsync();
        await using var ctx = s.W.Db.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkbookReconciliationService(ctx).RunAsync(s.Workbook, s.Decisions(), apply: true, actorId: null));
    }

    // ------------------------------------------------------------------------------------------ the settings editor

    [PostgresFact]
    public async Task SubCategoryCodesAreUnique_GeneratedOnesGetASuffix_AndDescriptionsSurviveEdits()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();

        var first = await w.Settings.AddSubCategoryAsync(new CreateDepartmentSubCategoryDto { DepartmentId = w.DeptA, Name = "General Inquiry", Description = "Anything else", DisplayOrder = 3 });
        var second = await w.Settings.AddSubCategoryAsync(new CreateDepartmentSubCategoryDto { DepartmentId = w.DeptB, Name = "General Inquiry", DisplayOrder = 2 });
        Assert.Equal(("General_Inquiry", "General_Inquiry_2"), (first.Code, second.Code));
        Assert.Equal("Anything else", first.Description);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Settings.AddSubCategoryAsync(new CreateDepartmentSubCategoryDto { DepartmentId = w.DeptB, Name = "Other", Code = "general_inquiry" }));
        Assert.Contains("already used by 'Contact Center / General Inquiry'", ex.Message);

        // Saving without a description (the list editor's rename / toggle / reorder) keeps it; an empty one clears it.
        var renamed = await w.Settings.UpdateSubCategoryAsync(first.Id, new UpdateDepartmentSubCategoryDto { Name = "General Enquiry", Code = first.Code, DisplayOrder = 3, IsActive = true });
        Assert.Equal(("General Enquiry", "Anything else"), (renamed!.Name, renamed.Description));
        var cleared = await w.Settings.UpdateSubCategoryAsync(first.Id, new UpdateDepartmentSubCategoryDto { Name = "General Enquiry", Code = first.Code, Description = "", DisplayOrder = 3, IsActive = true });
        Assert.Null(cleared!.Description);
    }
}
