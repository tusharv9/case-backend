using System.Text.Json;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.Controllers;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Case Management's notifications delivered by OmniConnect's central system instead of a table of its own.
/// </summary>
/// <remarks>
/// AuthService refuses a kind that is not declared, so every event the code raises must map to a declared kind. People must be
/// sent by their Host id (Case Management's own ids mean nothing there) and someone the Host does not know must be skipped,
/// not fail the batch. A card must never carry a case title or customer name. SLA breaches must still be said once per cycle,
/// reminded on a cooldown and capped — now that the old notifications table that remembered this is gone.
/// </remarks>
public class CentralNotificationsTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly RecordingNotificationSink sink = new();
    private readonly Guid owner = Guid.NewGuid(), stranger = Guid.NewGuid(), team = Guid.NewGuid(), caseId = Guid.NewGuid();
    private readonly string ownerHostId = Guid.NewGuid().ToString();

    public CentralNotificationsTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"central-notes-{Guid.NewGuid()}").Options);
        db.Users.AddRange(
            new User { Id = owner, Name = "Olivia Owner", Email = "o@x.test", Role = "Agent", ExternalUserId = ownerHostId, IsActive = true },
            new User { Id = stranger, Name = "Old Copy", Email = "s@x.test", Role = "Agent", ExternalUserId = null, IsActive = true });
        db.Teams.Add(new Team { Id = team, Name = "Service Team", Code = "ST", IsActive = true });
        db.Cases.Add(new Case { Id = caseId, CaseNumber = "C-01001", Title = "WhatsApp conversation with Jane Customer", CaseType = "Complaint",
            Severity = "High", TeamId = team, OwnerId = owner, Status = CaseStatus.InProgress, SlaStartTime = DateTime.UtcNow.AddHours(-3) });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private NotificationService Service(NotificationOptions? options = null) =>
        new(db, new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions())), Options.Create(options ?? new NotificationOptions()), sink);

    private static CaseNotificationDispatcher Dispatcher() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new HostIntegrationOptions { AppKey = "casemanagement" }), NullLogger<CaseNotificationDispatcher>.Instance);

    [Fact]
    public void Every_event_the_code_raises_maps_to_a_declared_kind_and_every_kind_is_on_by_default()
    {
        var declared = CaseNotificationKinds.All.Select(k => k.Key).ToHashSet();

        Assert.All(CaseNotificationKinds.ByLegacyType.Values, kind => Assert.Contains(kind, declared));
        Assert.All(CaseNotificationKinds.All, k => Assert.True(k.DefaultEnabled));
        Assert.Equal(declared.Count, CaseNotificationKinds.All.Count);   // no key declared twice
    }

    [Fact]
    public void The_kinds_are_published_on_the_cases_module_only_so_a_resync_brings_them_to_AuthService()
    {
        var json = JsonSerializer.SerializeToElement(PermissionsController.Build());

        foreach (var module in json.GetProperty("modules").EnumerateArray())
        {
            var count = module.GetProperty("notifications").GetArrayLength();
            Assert.Equal(module.GetProperty("key").GetString() == "cases" ? CaseNotificationKinds.All.Count : 0, count);
        }
    }

    [Fact]
    public async Task An_event_is_queued_with_its_kind_and_an_unknown_one_is_not_sent()
    {
        await Service().CreateNotificationAsync(owner, "CASE_ASSIGNED", "New Case Assigned", "Case C-01001 (WhatsApp conversation with Jane Customer)", caseId, "C-01001");
        await Service().CreateNotificationAsync(owner, "SOMETHING_NEW", "x", "y", caseId);

        var request = Assert.Single(sink.Requests);
        Assert.Equal(CaseNotificationKinds.Assigned, request.Kind);
        Assert.Null(request.Body);   // the old title and message (which named the customer) are not sent
    }

    [Fact]
    public async Task A_card_is_sent_to_the_hosts_id_names_the_case_by_number_and_never_carries_the_title()
    {
        var built = await Dispatcher().BuildAsync(db, new CaseNotificationRequest(CaseNotificationKinds.Assigned, [owner, stranger], caseId,
            ActorExternalId: Guid.NewGuid().ToString(), ActorName: "Lena Lead"), default);

        Assert.NotNull(built);
        Assert.Equal("remote.casemanagement.cases:case.assigned", built!.Kind);
        Assert.Equal(new[] { Guid.Parse(ownerHostId) }, built.SubjectUserIds);          // the person without a Host id is skipped
        Assert.Equal("C-01001", built.Values["record"]);
        Assert.Equal("Complaint", built.Values["caseType"]);
        Assert.DoesNotContain(built.Values.Values, v => v != null && v.Contains("Jane Customer"));
        Assert.Equal($"/apps/casemanagement/case-management/{caseId}", built.Link);
        Assert.Single(built.ExcludedUserIds);                                              // the actor is never told about their own action
    }

    [Fact]
    public async Task Nobody_the_host_knows_means_nothing_is_sent()
    {
        Assert.Null(await Dispatcher().BuildAsync(db, new CaseNotificationRequest(CaseNotificationKinds.Assigned, [stranger], caseId), default));
    }

    [Fact]
    public void The_same_event_key_gives_the_same_event_id_so_AuthService_writes_it_once()
    {
        Assert.Equal(CaseNotificationDispatcher.DeterministicGuid("case.sla_breached:k"), CaseNotificationDispatcher.DeterministicGuid("case.sla_breached:k"));
        Assert.NotEqual(CaseNotificationDispatcher.DeterministicGuid("case.sla_breached:k"), CaseNotificationDispatcher.DeterministicGuid("case.sla_breached:k2"));
    }

    [Fact]
    public async Task A_breach_is_said_once_per_cycle_then_reminded_on_a_cooldown_up_to_the_maximum()
    {
        var c = await db.Cases.AsNoTracking().SingleAsync();
        var candidate = new[] { new SlaBreachCandidate(c.Id, c.CaseNumber, c.Title, owner, c.SlaStartTime) };
        var svc = Service(new NotificationOptions { BreachReminderCooldownMinutes = 10, BreachMaxReminders = 1, BreachGroupingThreshold = 0 });
        var now = DateTime.UtcNow;

        await svc.PublishSlaBreachNotificationsAsync(candidate, now);
        await svc.PublishSlaBreachNotificationsAsync(candidate, now.AddMinutes(5));
        await svc.PublishSlaBreachNotificationsAsync(candidate, now.AddMinutes(15));
        await svc.PublishSlaBreachNotificationsAsync(candidate, now.AddMinutes(60));

        Assert.Equal(new[] { CaseNotificationKinds.SlaBreached, CaseNotificationKinds.SlaReminder }, sink.Requests.Select(r => r.Kind));
    }

    [Fact]
    public async Task A_new_SLA_cycle_is_reported_again()
    {
        var c = await db.Cases.AsNoTracking().SingleAsync();
        var svc = Service(new NotificationOptions { BreachGroupingThreshold = 0 });

        await svc.PublishSlaBreachNotificationsAsync(new[] { new SlaBreachCandidate(c.Id, c.CaseNumber, c.Title, owner, c.SlaStartTime) }, DateTime.UtcNow);
        await svc.PublishSlaBreachNotificationsAsync(new[] { new SlaBreachCandidate(c.Id, c.CaseNumber, c.Title, owner, c.SlaStartTime.AddHours(2)) }, DateTime.UtcNow);

        Assert.Equal(2, sink.Requests.Count(r => r.Kind == CaseNotificationKinds.SlaBreached));
    }

    [Fact]
    public async Task Several_cases_breaching_together_are_one_notification_with_a_count()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            db.Cases.Add(new Case { Id = id, CaseNumber = $"C-{id:N}"[..8], Title = "t", TeamId = team, OwnerId = owner, Status = CaseStatus.Open, SlaStartTime = DateTime.UtcNow.AddHours(-2) });
        await db.SaveChangesAsync();

        await Service(new NotificationOptions { BreachGroupingThreshold = 3 })
            .PublishSlaBreachNotificationsAsync(ids.Select(id => new SlaBreachCandidate(id, "n", "t", owner, DateTime.UtcNow.AddHours(-2))).ToList(), DateTime.UtcNow);

        var request = Assert.Single(sink.Requests);
        Assert.Equal(CaseNotificationKinds.SlaBreachedMany, request.Kind);
        Assert.Equal("3", request.Values!["count"]);
    }

    [Fact]
    public async Task An_accepted_help_request_tells_the_person_who_asked()
    {
        await Service().CreateNotificationAsync(owner, "CASE_SWARM_ACCEPTED", "Swarm Accepted", "x", caseId, "C-01001");

        Assert.Equal(CaseNotificationKinds.SwarmAccepted, Assert.Single(sink.Requests).Kind);
    }

    [Fact]
    public async Task A_bulk_reassign_of_one_case_names_that_case()
    {
        await Service().CreateBulkReassignedNotificationAsync(owner, new[] { (caseId, "C-01001") });

        var request = Assert.Single(sink.Requests);
        Assert.Equal(CaseNotificationKinds.Reassigned, request.Kind);
        Assert.Equal(caseId, request.CaseId);
    }

    [Fact]
    public async Task A_bulk_reassign_of_several_cases_is_one_notification_with_the_count_and_the_case_numbers()
    {
        var cases = Enumerable.Range(1, 7).Select(i => (Guid.NewGuid(), $"C-0{i}")).ToList();

        await Service().CreateBulkReassignedNotificationAsync(owner, cases);

        var request = Assert.Single(sink.Requests);
        Assert.Equal(CaseNotificationKinds.ReassignedMany, request.Kind);
        Assert.Equal("7", request.Values!["count"]);
        Assert.Equal("C-01, C-02, C-03, C-04, C-05 and 2 more", request.Values["cases"]);
    }
}
