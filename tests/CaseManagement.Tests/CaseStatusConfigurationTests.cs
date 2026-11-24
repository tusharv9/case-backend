using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Statuses;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>Configurable Settings → Status: administrators add statuses and choose whether the SLA runs in them; the workflow follows.</summary>
/// <remarks>
/// <para>The five workflow statuses carry Handle, Escalate and Resolve, so they must stay put (renamed, never deleted or
/// recategorised). A status an administrator adds must work everywhere a system one does: the Status menu offers it only where a
/// move is allowed, the case keeps the system status of its category (so every existing rule still applies), and the SLA pauses
/// or runs as configured — one pause across two paused statuses, never reset.</para>
/// <para>Nothing a case refers to may vanish: a status in use cannot be deleted, only deactivated.</para>
/// </remarks>
public class CaseStatusConfigurationTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly ConfigCache cache = new(new MemoryCache(new MemoryCacheOptions()));
    private readonly Team team;
    private readonly User agent;

    public CaseStatusConfigurationTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"statuses-{Guid.NewGuid()}")
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        agent = new User { Id = Guid.NewGuid(), Name = "Agent Ann", Email = "ann@x.test", Role = "Agent", ExternalUserId = "ann" };
        team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", IsActive = true };
        db.AddRange(agent, team);
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private sealed class Caller(Guid id) : ICurrentUserAccessor
    {
        public Guid? LocalUserId => id;
        public HostPrincipal? Principal => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string> { "*" };
        public bool Has(string permission) => true;
    }

    private CaseStatusAdminService Admin() => new(db, cache, new Mock<INotificationService>().Object, new Caller(agent.Id));

    private CaseService Cases() =>
        new(new CaseRepository(db), new Mock<INotificationService>().Object, new Mock<IConfigurableSettingsService>().Object, db,
            new Mock<IWebHostEnvironment>().Object, configCache: cache, currentUser: new Caller(agent.Id), statusCatalog: new CaseStatusCatalog(db, cache));

    private Case AddCase(CaseStatus status)
    {
        var c = new Case
        {
            Id = Guid.NewGuid(), CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..6], Title = "t", TeamId = team.Id, OwnerId = agent.Id,
            Status = status, Severity = "High", SlaStartTime = DateTime.UtcNow.AddHours(-1), InternalResolutionTargetMinutes = 600,
            ExternalResolutionTargetMinutes = 1200, CreatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.Cases.Add(c);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return c;
    }

    private Task<Case> Reload(Case c) => db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id);

    private Task Move(Case c, string status) =>
        Cases().UpdateCaseStatusAsync(c.Id, new UpdateCaseStatusDto { Status = status, UserId = agent.Id });

    private async Task<string> AddStatus(string label, string category, string sla, params (string From, string To)[] moves)
    {
        var created = await Admin().CreateAsync(new SaveCaseStatusDto { Label = label, Category = category, SlaBehaviour = sla });
        var existing = (await Admin().GetAsync(false)).Transitions;
        var all = existing.Concat(moves.Select(m => new CaseStatusTransitionDto
        {
            From = m.From == "@" ? created.Key : m.From, To = m.To == "@" ? created.Key : m.To,
        })).ToList();
        await Admin().SaveTransitionsAsync(new SaveCaseStatusTransitionsDto { Transitions = all });
        return created.Key;
    }

    // ------------------------------------------------------------------------------------------------ the catalog

    [Fact]
    public async Task With_nothing_configured_the_five_statuses_and_todays_moves_apply()
    {
        var snapshot = await new CaseStatusCatalog(db, cache).GetAsync();

        Assert.Equal(new[] { "Open", "InProgress", "WaitingOnCustomer", "Escalated", "Resolved" },
            snapshot.Definitions.Where(d => d.IsActive).Select(d => d.Key));
        Assert.True(snapshot.CanMove("InProgress", "WaitingOnCustomer"));
        Assert.True(snapshot.CanMove("WaitingOnCustomer", "InProgress"));
        Assert.False(snapshot.CanMove("InProgress", "Resolved"));
        Assert.Equal("Paused", snapshot.Find("WaitingOnCustomer")!.SlaBehaviour);
    }

    [Fact]
    public async Task A_new_status_gets_a_stable_key_and_a_name_may_be_used_once()
    {
        var created = await Admin().CreateAsync(new SaveCaseStatusDto { Label = "Pending documents", Category = "Waiting", SlaBehaviour = "Paused" });

        Assert.Equal("PendingDocuments", created.Key);
        Assert.False(created.IsSystem);
        var duplicate = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Admin().CreateAsync(new SaveCaseStatusDto { Label = "PENDING DOCUMENTS", Category = "Active", SlaBehaviour = "Running" }));
        Assert.Contains(duplicate.Errors, e => e.Field == "label");
    }

    [Fact]
    public async Task The_workflow_statuses_are_protected_but_can_be_renamed()
    {
        await Admin().UpdateAsync("InProgress", new SaveCaseStatusDto { Label = "Working on it" });

        await Assert.ThrowsAsync<FieldValidationException>(() => Admin().UpdateAsync("InProgress", new SaveCaseStatusDto { Category = "Waiting" }));
        await Assert.ThrowsAsync<FieldValidationException>(() => Admin().UpdateAsync("InProgress", new SaveCaseStatusDto { SlaBehaviour = "Paused" }));
        await Assert.ThrowsAsync<FieldValidationException>(() => Admin().UpdateAsync("Escalated", new SaveCaseStatusDto { IsActive = false }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Admin().DeleteAsync("Resolved"));
        Assert.Equal("Working on it", (await new CaseStatusCatalog(db, cache).GetAsync()).LabelOf("InProgress"));
    }

    [Fact]
    public async Task Waiting_on_Customer_may_be_set_to_keep_the_SLA_running()
    {
        var updated = await Admin().UpdateAsync("WaitingOnCustomer", new SaveCaseStatusDto { SlaBehaviour = "Running" });

        Assert.Equal("Running", updated.SlaBehaviour);
    }

    [Fact]
    public async Task Moves_into_Resolved_Escalated_or_Open_are_actions_not_menu_entries()
    {
        var error = await Assert.ThrowsAsync<FieldValidationException>(() => Admin().SaveTransitionsAsync(new SaveCaseStatusTransitionsDto
        {
            Transitions = [new() { From = "InProgress", To = "Resolved" }, new() { From = "Open", To = "InProgress" }],
        }));

        Assert.Equal(2, error.Errors.Count);
    }

    // ------------------------------------------------------------------------------------------------ cases moving

    [Fact]
    public async Task A_case_moved_to_a_paused_custom_status_pauses_the_SLA_and_keeps_its_category()
    {
        var key = await AddStatus("Pending documents", "Waiting", "Paused", ("InProgress", "@"), ("@", "InProgress"));
        var c = AddCase(CaseStatus.InProgress);

        await Move(c, key);

        var after = await Reload(c);
        Assert.Equal(CaseStatus.WaitingOnCustomer, after.Status);
        Assert.Equal(key, after.StatusKey);
        Assert.NotNull(after.SlaPausedAt);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.Message == "Status changed from In Progress to Pending documents"));
    }

    [Fact]
    public async Task Going_back_to_work_resumes_the_SLA_and_clears_the_custom_status()
    {
        var key = await AddStatus("Pending documents", "Waiting", "Paused", ("InProgress", "@"), ("@", "InProgress"));
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);

        await Move(c, "InProgress");

        var after = await Reload(c);
        Assert.Equal(CaseStatus.InProgress, after.Status);
        Assert.Null(after.StatusKey);
        Assert.Null(after.SlaPausedAt);
    }

    [Fact]
    public async Task Moving_between_two_paused_statuses_keeps_one_pause()
    {
        var key = await AddStatus("Pending documents", "Waiting", "Paused", ("WaitingOnCustomer", "@"), ("@", "InProgress"));
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, "WaitingOnCustomer");
        var pausedAt = (await Reload(c)).SlaPausedAt;

        await Move(c, key);

        Assert.Equal(pausedAt, (await Reload(c)).SlaPausedAt);
    }

    [Fact]
    public async Task A_running_custom_status_keeps_the_clock_going()
    {
        var key = await AddStatus("Under review", "Active", "Running", ("InProgress", "@"));
        var c = AddCase(CaseStatus.InProgress);

        await Move(c, key);

        var after = await Reload(c);
        Assert.Equal(CaseStatus.InProgress, after.Status);
        Assert.Equal(key, after.StatusKey);
        Assert.Null(after.SlaPausedAt);
    }

    [Fact]
    public async Task A_move_the_configuration_does_not_allow_is_refused_naming_the_allowed_ones()
    {
        var key = await AddStatus("Under review", "Active", "Running");   // no moves into it
        var c = AddCase(CaseStatus.InProgress);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Move(c, key));

        Assert.Contains("cannot be moved to Under review", error.Message);
        Assert.Contains("Waiting on Customer", error.Message);
    }

    [Fact]
    public async Task The_status_menu_offers_the_configured_moves()
    {
        var key = await AddStatus("Under review", "Active", "Running", ("InProgress", "@"));
        var c = AddCase(CaseStatus.InProgress);

        var details = await Cases().GetCaseDetailsAsync(c.Id);

        Assert.Equal(new[] { "WaitingOnCustomer", key }, details!.Workflow!.AllowedStatuses);
    }

    [Fact]
    public async Task A_status_in_use_cannot_be_deleted_only_deactivated_and_its_cases_keep_it()
    {
        var key = await AddStatus("Under review", "Active", "Running", ("InProgress", "@"));
        var c = AddCase(CaseStatus.InProgress);
        await Move(c, key);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Admin().DeleteAsync(key));
        await Admin().UpdateAsync(key, new SaveCaseStatusDto { IsActive = false });

        Assert.Equal(key, (await Reload(c)).StatusKey);
        var other = AddCase(CaseStatus.InProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Move(other, key));   // no longer offered
    }

    [Fact]
    public async Task Resolving_a_case_in_a_custom_status_clears_it()
    {
        var c = new Case { Status = CaseStatus.InProgress };
        c.StatusKey = "UnderReview";

        c.Status = CaseStatus.Resolved;

        Assert.Null(c.StatusKey);
        Assert.Equal("Resolved", c.EffectiveStatusKey);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_list_filters_by_a_custom_status_and_a_system_status_excludes_it()
    {
        var key = await AddStatus("Under review", "Active", "Running", ("InProgress", "@"));
        var reviewed = AddCase(CaseStatus.InProgress);
        var working = AddCase(CaseStatus.InProgress);
        await Move(reviewed, key);

        var repo = new CaseRepository(db);
        var custom = await repo.GetPaginatedBoardCasesAsync(status: key);
        var system = await repo.GetPaginatedBoardCasesAsync(status: "In Progress");

        Assert.Equal(new[] { reviewed.Id }, custom.Items.Select(i => i.Id));
        Assert.Equal(new[] { working.Id }, system.Items.Select(i => i.Id));
    }
}
