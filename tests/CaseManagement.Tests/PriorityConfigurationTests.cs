using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using CaseManagement.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// Priorities ("severities") are ONE master — PrioritySlaRules — and nothing assumes Critical/High/Medium/Low.
/// These tests run against a real, migrated and bootstrap-seeded PostgreSQL database.
/// </summary>
public class PriorityConfigurationTests
{
    internal sealed class World : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public AppDbContext Ctx = null!;
        public SlaRoutingService Sla = null!;
        public ConfigurableSettingsService Settings = null!;
        public Guid DeptA, DeptB, FraudA, FraudB, PaymentA, Actor;

        public static async Task<World> CreateAsync()
        {
            var w = new World { Db = await TempDatabase.CreateAsync() };
            await using (var setup = w.Db.NewContext()) await setup.Database.MigrateAsync();
            w.Ctx = w.Db.NewContext();

            // Seed through the real bootstrap (so the four standard priorities exist) and add two departments.
                        DbSeeder.Run(w.Ctx, SeedMode.Bootstrap, adoptedLegacyDatabase: false);

            w.DeptA = Guid.NewGuid(); w.DeptB = Guid.NewGuid();
            w.FraudA = Guid.NewGuid(); w.FraudB = Guid.NewGuid(); w.PaymentA = Guid.NewGuid();
            w.Ctx.AddRange(
                new Department { Id = w.DeptA, Name = "Contact Center", Code = "CC", CreatedAt = DateTime.UtcNow },
                new Department { Id = w.DeptB, Name = "Fraud Ops", Code = "FO", CreatedAt = DateTime.UtcNow });
            await w.Ctx.SaveChangesAsync();
            w.Ctx.AddRange(
                new DepartmentSubCategory { Id = w.FraudA, DepartmentId = w.DeptA, Name = "Fraud", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow },
                new DepartmentSubCategory { Id = w.FraudB, DepartmentId = w.DeptB, Name = "Fraud", Code = "FB", IsActive = true, CreatedAt = DateTime.UtcNow },
                new DepartmentSubCategory { Id = w.PaymentA, DepartmentId = w.DeptA, Name = "Payment Issue", Code = "P", IsActive = true, CreatedAt = DateTime.UtcNow });
            await w.Ctx.SaveChangesAsync();

            // Configuration changes are audited against a real user (the audit trail has a foreign key).
            w.Actor = Guid.NewGuid();
            w.Ctx.Users.Add(new User { Id = w.Actor, Name = "Admin", Email = "admin@example.test", Role = "Admin", DepartmentId = w.DeptA, CreatedAt = DateTime.UtcNow });
            await w.Ctx.SaveChangesAsync();

            w.Sla = new SlaRoutingService(w.Ctx, NullLogger<SlaRoutingService>.Instance,
                new EscalationService(w.Ctx, new ConfigCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))));
            w.Settings = new ConfigurableSettingsService(new ConfigurableSettingsRepository(w.Ctx), w.Ctx,
                new Mock<INotificationService>().Object, new Mock<IHttpContextAccessor>().Object);
            return w;
        }

        public async Task<PrioritySlaRule> Rule(string name) => await Ctx.PrioritySlaRules.AsNoTracking().FirstAsync(r => r.Priority == name);

        /// <summary>Saves the current configuration back with the given sub-category assignments.</summary>
        public async Task MapAsync(params (string Priority, Guid[] SubCategories)[] assignments)
        {
            var config = await Sla.GetFullConfigurationAsync();
            var request = new UpdateSlaRoutingConfigRequestDto
            {
                PriorityRules = config.PriorityRules,
                BusinessHours = config.BusinessHours,
                EscalationLevels = config.EscalationLevels
            };
            foreach (var rule in request.PriorityRules) rule.AppliedSubCategoryIds = new();
            foreach (var (priority, ids) in assignments)
                request.PriorityRules.First(r => r.Priority == priority).AppliedSubCategoryIds = ids.ToList();
            await Sla.UpdateConfigurationAsync(request, Actor);
        }

        public async ValueTask DisposeAsync()
        {
            await Ctx.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task SubCategoryMapping_Wins_OverTheRequestedPriority_AndNothingIsDefaulted()
    {
        await using var w = await World.CreateAsync();
        await w.MapAsync(("Critical", new[] { w.FraudA }));

        // Mapped: the configured priority is authoritative, even if the caller asks for something else.
        var mapped = await w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "fraud", "Low");
        Assert.Equal(("Critical", "SubCategoryMapping"), (mapped.Priority, mapped.Source));

        // Unmapped: the request is used, returned in the configured spelling.
        var requested = await w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Payment Issue", "hIgH");
        Assert.Equal(("High", "Requested"), (requested.Priority, requested.Source));

        // Unknown requested priority: rejected with the list of configured ones (it used to become "Medium").
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Payment Issue", "Urgentish"));
        Assert.Contains("not a configured priority", unknown.Message);
        Assert.Contains("Critical", unknown.Message);

        // Unmapped and nothing requested: an explicit error, never a guessed default.
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Payment Issue", null));
        Assert.Contains("priority is required", none.Message);
    }

    [PostgresFact]
    public async Task TheSameSubCategoryNameInTwoDepartments_CanHaveDifferentPriorities()
    {
        // Mappings used to be keyed by NAME (globally unique), so this was impossible.
        await using var w = await World.CreateAsync();
        await w.MapAsync(("Critical", new[] { w.FraudA }), ("Low", new[] { w.FraudB }));

        Assert.Equal("Critical", (await w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Fraud", null)).Priority);
        Assert.Equal("Low", (await w.Sla.ResolveEffectivePriorityAsync(w.DeptB, "Fraud", null)).Priority);
    }

    [PostgresFact]
    public async Task RenamingASubCategory_KeepsItsPriority_AndDeletingOneRemovesTheMapping()
    {
        await using var w = await World.CreateAsync();
        await w.MapAsync(("High", new[] { w.PaymentA }));

        var renamed = await w.Settings.UpdateSubCategoryAsync(w.PaymentA, new UpdateDepartmentSubCategoryDto { Name = "Payments & Transfers", Code = "P", IsActive = true });
        Assert.NotNull(renamed);
        Assert.Equal("High", (await w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Payments & Transfers", null)).Priority);

        Assert.True(await w.Settings.DeleteSubCategoryAsync(w.PaymentA));
        Assert.Equal(0L, await w.Db.ScalarAsync<long>($"SELECT count(*) FROM \"PriorityCategoryMappings\" WHERE \"DepartmentSubCategoryId\" = '{w.PaymentA}'"));
    }

    [PostgresFact]
    public async Task ASubCategory_CannotBeAssignedTwice_AndUnknownIdsAreRejected()
    {
        await using var w = await World.CreateAsync();

        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.MapAsync(("Critical", new[] { w.FraudA }), ("High", new[] { w.FraudA })));
        Assert.Contains("only one priority", twice.Message);
        Assert.Contains("Contact Center / Fraud", twice.Message);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => w.MapAsync(("High", new[] { Guid.NewGuid() })));
        Assert.Contains("no longer exist", unknown.Message);

        // A failed save changes nothing.
        Assert.Equal(0L, await w.Db.ScalarAsync<long>("SELECT count(*) FROM \"PriorityCategoryMappings\""));
    }

    [PostgresFact]
    public async Task TheSlaVersion_OnlyMoves_WhenATargetActuallyChanges()
    {
        await using var w = await World.CreateAsync();
        var before = (await w.Rule("High")).Version;

        await w.MapAsync(("High", new[] { w.FraudA }));                 // mapping change only
        Assert.Equal(before, (await w.Rule("High")).Version);

        var config = await w.Sla.GetFullConfigurationAsync();
        var high = config.PriorityRules.First(r => r.Priority == "High");
        high.ExternalResolutionValue += 1;                               // a real target change
        await w.Sla.UpdateConfigurationAsync(new UpdateSlaRoutingConfigRequestDto
        {
            PriorityRules = config.PriorityRules, BusinessHours = config.BusinessHours, EscalationLevels = config.EscalationLevels
        }, w.Actor);
        Assert.Equal(before + 1, (await w.Rule("High")).Version);
    }

    [PostgresFact]
    public async Task AddingACustomPriority_MakesItAvailableEverywhere_WithoutCodeChanges()
    {
        await using var w = await World.CreateAsync();

        var created = await w.Settings.AddSeverityAsync(new CreateSeverityDto { Name = "Urgent", InternalHours = 1, ExternalHours = 2, FirstResponseMinutes = 15 });
        Assert.Equal("Urgent", created.Name);

        // 1. It is a priority rule (the same rows the Cases SLA & Routing screen edits)...
        var config = await w.Sla.GetFullConfigurationAsync();
        var rule = config.PriorityRules.Single(r => r.Priority == "Urgent");
        Assert.Equal(60, rule.InternalResolutionMinutes);
        Assert.Equal(120, rule.ExternalResolutionMinutes);
        Assert.Equal(15, rule.FirstResponseMinutes);
        // 2. ...appended after the existing ones...
        Assert.Equal("Urgent", config.PriorityRules.Last().Priority);
        // 3. ...it can be mapped to a sub-category and resolved for a new case,
        await w.MapAsync(("Urgent", new[] { w.FraudA }));
        Assert.Equal("Urgent", (await w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Fraud", null)).Priority);
        // 4. ...and requested by name.
        Assert.Equal("Urgent", (await w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Payment Issue", "urgent")).Priority);
        // 5. The case SLA snapshot comes from its rule, not from any built-in number.
        var snapshot = await w.Sla.GetActivePrioritySlaRuleAsync("Urgent");
        Assert.Equal(120, snapshot.ExternalResolutionMinutes);

        // And the SLA screen's validator no longer insists on exactly four named priorities.
        var validation = await new UpdateSlaRoutingConfigRequestDtoValidator().ValidateAsync(new UpdateSlaRoutingConfigRequestDto
        {
            PriorityRules = config.PriorityRules, BusinessHours = config.BusinessHours, EscalationLevels = config.EscalationLevels
        });
        Assert.DoesNotContain(validation.Errors, e => e.ErrorMessage.Contains("exactly 4"));
        Assert.DoesNotContain(validation.Errors, e => e.ErrorMessage.Contains("strictly: Critical"));
    }

    [PostgresFact]
    public async Task UnknownConfigurationIsAnError_NotAHardCodedFallback()
    {
        await using var w = await World.CreateAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sla.GetActivePrioritySlaRuleAsync("P0-Blocker"));
        Assert.Contains("No SLA rule is configured", ex.Message);
    }

    [PostgresFact]
    public async Task InactivePriorities_CannotBeChosen_ButStayOnExistingCases()
    {
        await using var w = await World.CreateAsync();
        var low = await w.Rule("Low");
        await w.Settings.UpdateSeverityAsync(low.Id, new UpdateSeverityDto { Name = "Low", DisplayOrder = low.DisplayOrder, IsActive = false });

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Sla.ResolveEffectivePriorityAsync(w.DeptA, "Payment Issue", "Low"));
        Assert.Equal("Low", (await w.Sla.GetActivePrioritySlaRuleAsync("Low")).Priority);   // the rule still exists for snapshots/history
    }

    [PostgresFact]
    public async Task Rename_Delete_AndLastPriorityRules_ProtectTheDataThatReferencesAPriority()
    {
        await using var w = await World.CreateAsync();

        // A routing rule that matches on "High" and a case that uses "High".
        var userId = Guid.NewGuid(); var customerId = Guid.NewGuid();
        w.Ctx.AddRange(
            new User { Id = userId, Name = "U", Email = "u@example.test", Role = "Agent", DepartmentId = w.DeptA, CreatedAt = DateTime.UtcNow },
            new Customer { Id = customerId, FullName = "C", PhoneNumber = "+60 1", PreferredLanguage = "English", CreatedAt = DateTime.UtcNow });
        await w.Ctx.SaveChangesAsync();
        w.Ctx.Add(new RoutingRule { Id = Guid.NewGuid(), Name = "High to CC", TargetDepartmentId = w.DeptA, ConditionsJson = "{\"matchType\":\"ALL\",\"priority\":\"High\"}", EvaluationOrder = 1, CreatedAt = DateTime.UtcNow });
        w.Ctx.Add(new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-00001", CaseType = "Complaint", Title = "t", Description = "d", Status = CaseStatus.Open,
            Severity = "High", DepartmentId = w.DeptA, CustomerId = customerId, OwnerId = userId, CreatedAt = DateTime.UtcNow
        });
        await w.Ctx.SaveChangesAsync();
        var high = await w.Rule("High");

        // Cannot delete a priority that cases use.
        var inUse = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Settings.DeleteSeverityAsync(high.Id));
        Assert.Contains("1 case(s)", inUse.Message);

        // Renaming follows through to cases and to the routing rule that matches on it.
        await w.Settings.UpdateSeverityAsync(high.Id, new UpdateSeverityDto { Name = "Elevated", DisplayOrder = high.DisplayOrder, IsActive = true });
        Assert.Equal("Elevated", await w.Db.ScalarAsync<string>("SELECT \"Severity\" FROM \"Cases\" LIMIT 1"));
        Assert.Contains("Elevated", await w.Db.ScalarAsync<string>("SELECT \"ConditionsJson\" FROM \"RoutingRules\" LIMIT 1"));

        // Cannot delete a priority a routing rule matches on (even with no cases on it).
        await using (var ctx = w.Db.NewContext())
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"Cases\" SET \"Severity\" = 'Low'");
        var matched = await Assert.ThrowsAsync<InvalidOperationException>(() => w.Settings.DeleteSeverityAsync(high.Id));
        Assert.Contains("High to CC", matched.Message);
    }

    [PostgresFact]
    public async Task TheLastActivePriority_CannotBeDeactivatedOrDeleted()
    {
        await using var w = await World.CreateAsync();
        var all = (await w.Settings.GetSeverityConfigurationsAsync()).ToList();
        foreach (var keep in all.Skip(1))
            await w.Settings.DeleteSeverityAsync(keep.Id);

        var last = all[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Settings.DeleteSeverityAsync(last.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Settings.UpdateSeverityAsync(last.Id, new UpdateSeverityDto { Name = last.Name, DisplayOrder = last.DisplayOrder, IsActive = false }));
    }
}
