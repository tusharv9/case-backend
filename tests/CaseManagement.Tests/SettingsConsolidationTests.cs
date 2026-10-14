using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Phase 5 (CM-07 / CM-08): the Case Management "Master Data & Dropdowns" screen is gone, so what it did now lives where the data
/// belongs — priorities with their SLA targets, case types and sub-categories through the field editor — and renames never orphan
/// the cases and rules that store these names as text. On a real migrated PostgreSQL database.
/// </summary>
public class SettingsConsolidationTests
{
    private static async Task<UpdateSlaRoutingConfigRequestDto> CurrentAsync(PriorityConfigurationTests.World w)
    {
        var config = await w.Sla.GetFullConfigurationAsync();
        return new UpdateSlaRoutingConfigRequestDto { PriorityRules = config.PriorityRules, BusinessHours = config.BusinessHours, EscalationLevels = config.EscalationLevels };
    }

    private static PrioritySlaRuleDto NewPriority(string name, int fr = 15, int internalH = 1, int externalH = 2) => new()
    {
        Id = Guid.Empty, Priority = name, IsActive = true,
        FirstResponseValue = fr, FirstResponseUnit = "Minutes",
        InternalResolutionValue = internalH, InternalResolutionUnit = "Hours",
        ExternalResolutionValue = externalH, ExternalResolutionUnit = "Hours",
    };

    private static async Task<Guid> CaseAsync(PriorityConfigurationTests.World w, Guid department, string caseType, string? subcategory)
    {
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "C", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60" + Random.Shared.NextInt64(1000000000, 1999999999), CreatedAt = DateTime.UtcNow };
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "T-" + Guid.NewGuid().ToString("N")[..8], Title = "t", CaseType = caseType, Severity = "High",
            Subcategory = subcategory, DepartmentId = department, OwnerId = w.Actor, CustomerId = customer.Id, Status = CaseStatus.Open,
            SlaStartTime = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, FirstResponseStatus = "Pending",
        };
        await using var ctx = w.Db.NewContext();
        ctx.Customers.Add(customer);
        ctx.Cases.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private static async Task<T> FreshAsync<T>(PriorityConfigurationTests.World w, Func<AppDbContext, Task<T>> read)
    {
        await using var ctx = w.Db.NewContext();
        return await read(ctx);
    }

    // ------------------------------------------------------------------------------------------ priorities live with their SLA

    [PostgresFact]
    public async Task APriorityIsAddedOnTheSlaMatrix_WithItsOwnTargets_AndCanBeDeactivated()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();
        var request = await CurrentAsync(w);
        request.PriorityRules.Add(NewPriority("Urgent", fr: 10, internalH: 1, externalH: 3));
        await w.Sla.UpdateConfigurationAsync(request, w.Actor);

        var urgent = await w.Rule("Urgent");
        Assert.True(urgent.IsActive);
        Assert.Equal(10, urgent.FirstResponseMinutes);
        Assert.Equal(180, urgent.ExternalResolutionMinutes);

        var again = await CurrentAsync(w);
        again.PriorityRules.Single(r => r.Priority == "Urgent").IsActive = false;
        await w.Sla.UpdateConfigurationAsync(again, w.Actor);
        Assert.False((await FreshAsync(w, ctx => ctx.PrioritySlaRules.SingleAsync(r => r.Priority == "Urgent"))).IsActive);
    }

    [PostgresFact]
    public async Task DuplicateNames_AndDeactivatingEveryPriority_AreRefused()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();

        var duplicate = await CurrentAsync(w);
        duplicate.PriorityRules.Add(NewPriority("high"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sla.UpdateConfigurationAsync(duplicate, w.Actor));

        var allOff = await CurrentAsync(w);
        foreach (var r in allOff.PriorityRules) r.IsActive = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sla.UpdateConfigurationAsync(allOff, w.Actor));
        Assert.True(await FreshAsync(w, ctx => ctx.PrioritySlaRules.AllAsync(r => r.IsActive)));
    }

    [PostgresFact]
    public async Task APriorityWithoutSlaTargets_IsRefused_NoHiddenDefaults()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Settings.AddSeverityAsync(new CreateSeverityDto { Name = "Someday" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Settings.AddSeverityAsync(new CreateSeverityDto { Name = "Someday", InternalHours = 4, ExternalHours = 8 }));
        Assert.False(await FreshAsync(w, ctx => ctx.PrioritySlaRules.AnyAsync(r => r.Priority == "Someday")));
    }

    // ------------------------------------------------------------------------------------------ renames follow the name

    [PostgresFact]
    public async Task RenamingACaseType_UpdatesItsCases_RoutingRules_AndExactSkillRules()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();
        var complaint = await FreshAsync(w, ctx => ctx.CaseTypeConfigs.SingleAsync(c => c.Name == "Complaint"));
        var caseId = await CaseAsync(w, w.DeptA, "Complaint", "Fraud");
        await using (var ctx = w.Db.NewContext())
        {
            ctx.RoutingRules.Add(new RoutingRule { Id = Guid.NewGuid(), Name = "Complaints", ConditionsJson = "{\"matchType\":\"ALL\",\"caseType\":\"Complaint\"}", TargetDepartmentId = w.DeptA, CreatedAt = DateTime.UtcNow });
            ctx.SkillRules.Add(new SkillRule { Id = Guid.NewGuid(), SkillName = "Care", MatchField = "CaseType", MatchType = "Equals", MatchValue = "Complaint", IsActive = true, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        await w.Settings.UpdateCaseTypeAsync(complaint.Id, new UpdateCaseTypeConfigDto { Code = complaint.Code, Name = "Grievance", Prefix = complaint.Prefix, DisplayOrder = complaint.DisplayOrder, IsActive = true });

        Assert.Equal("Grievance", await FreshAsync(w, ctx => ctx.Cases.Where(c => c.Id == caseId).Select(c => c.CaseType).SingleAsync()));
        Assert.Contains("\"Grievance\"", await FreshAsync(w, ctx => ctx.RoutingRules.Where(r => r.Name == "Complaints").Select(r => r.ConditionsJson).SingleAsync()));
        Assert.Equal("Grievance", await FreshAsync(w, ctx => ctx.SkillRules.Where(r => r.SkillName == "Care").Select(r => r.MatchValue).SingleAsync()));
    }

    [PostgresFact]
    public async Task RenamingASubCategory_UpdatesItsDepartmentsCases_AndLeavesSharedNamesInRulesAlone()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();
        var caseA = await CaseAsync(w, w.DeptA, "Complaint", "Fraud");
        var caseB = await CaseAsync(w, w.DeptB, "Complaint", "Fraud");
        await using (var ctx = w.Db.NewContext())
        {
            ctx.RoutingRules.Add(new RoutingRule { Id = Guid.NewGuid(), Name = "Fraud cases", ConditionsJson = "{\"matchType\":\"ALL\",\"category\":\"Fraud\"}", TargetDepartmentId = w.DeptB, CreatedAt = DateTime.UtcNow });
            ctx.RoutingRules.Add(new RoutingRule { Id = Guid.NewGuid(), Name = "Payments", ConditionsJson = "{\"matchType\":\"ALL\",\"category\":\"Payment Issue\"}", TargetDepartmentId = w.DeptA, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        // "Fraud" exists in both departments: renaming department A's one moves A's cases only, and the shared rule stays.
        await w.Settings.UpdateSubCategoryAsync(w.FraudA, new UpdateDepartmentSubCategoryDto { Name = "Card Fraud", Code = "F", DisplayOrder = 1, IsActive = true });
        Assert.Equal("Card Fraud", await FreshAsync(w, ctx => ctx.Cases.Where(c => c.Id == caseA).Select(c => c.Subcategory).SingleAsync()));
        Assert.Equal("Fraud", await FreshAsync(w, ctx => ctx.Cases.Where(c => c.Id == caseB).Select(c => c.Subcategory).SingleAsync()));
        Assert.Contains("\"Fraud\"", await FreshAsync(w, ctx => ctx.RoutingRules.Where(r => r.Name == "Fraud cases").Select(r => r.ConditionsJson).SingleAsync()));

        // "Payment Issue" is unique: its rule follows the rename.
        await w.Settings.UpdateSubCategoryAsync(w.PaymentA, new UpdateDepartmentSubCategoryDto { Name = "Payment Problem", Code = "P", DisplayOrder = 2, IsActive = true });
        Assert.Contains("\"Payment Problem\"", await FreshAsync(w, ctx => ctx.RoutingRules.Where(r => r.Name == "Payments").Select(r => r.ConditionsJson).SingleAsync()));
    }

    // ------------------------------------------------------------------------------------------ case list filters

    [PostgresFact]
    public async Task TheCaseListFilters_AreConfiguration_IncludingPriorityAndChannel()
    {
        await using var w = await PriorityConfigurationTests.World.CreateAsync();
        var filters = await FreshAsync(w, ctx => ctx.FieldConfigurations.Where(f => f.ModuleKey == "CaseManagement" && f.SectionKey == "Filters").OrderBy(f => f.DisplayOrder).ToListAsync());
        Assert.Contains(filters, f => f.ApiField == "severity" && f.IsVisible);
        Assert.Contains(filters, f => f.ApiField == "sourceChannel" && f.IsVisible);
        Assert.Equal(filters.Count, filters.Select(f => f.DisplayOrder).Distinct().Count());   // display order stays unique
    }
}
