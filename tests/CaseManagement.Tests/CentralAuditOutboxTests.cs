using System.Text.Json;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Audit;
using CaseManagement.Api.Services.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OmniConnect.Outbox;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Case Management's events leave for the platform's Audit and Notification services through its own outbox: stored here first
/// (with the change, or right after it), then delivered by the platform's dispatcher.
/// </summary>
/// <remarks>
/// Worth testing because a central trail is read far more widely than a case: a copy that carried a note's text or a customer's
/// name would leak it platform-wide, a copy saved apart from its event could exist for an event that never happened (or be
/// missing for one that did), and a copy written twice on a retried save would double the trail.
/// </remarks>
public class CentralAuditOutboxTests : IDisposable
{
    private readonly string dbName = $"central-audit-{Guid.NewGuid()}";
    private readonly OutboxSignal<AppDbContext> signal = new();
    private readonly AppDbContext db;

    public CentralAuditOutboxTests() => db = Context();

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName)
        .AddInterceptors(new CaseAuditInterceptor(new HttpContextAccessor(), Options.Create(new HostIntegrationOptions { AppKey = "casemanagement" }), signal))
        .Options);

    private static AuditRequestContext Nobody => new(null, null, null, null, null);

    private static CaseEvent Event(EventType type, string? action = null, string? oldValue = null, string? newValue = null, string message = "", Guid? caseId = null, string? module = null) =>
        new() { Id = Guid.NewGuid(), CaseId = caseId ?? Guid.NewGuid(), EventType = type, ActionType = action, OldValue = oldValue, NewValue = newValue, Message = message, Module = module, CreatedAt = DateTime.UtcNow };

    // ------------------------------------------------------------------------------------------------ the central row

    [Fact]
    public void A_status_change_is_filed_under_the_platform_key_with_its_before_and_after()
    {
        var row = CentralCaseAudit.For(Event(EventType.Update, "STATUS_CHANGED", "In Progress", "Resolved"), "C-01234", Nobody, "casemanagement");

        Assert.Equal(("casemanagement.case.status_changed", "CRUD", "Cases", "Case", "C-01234"), (row.Action, row.ActionCategory, row.Module, row.EntityType, row.EntityLabel));
        Assert.Equal("Status changed: case C-01234 (In Progress → Resolved).", row.Details);
        Assert.Equal(("System", (Guid?)null), (row.ActorName, row.ActorUserId));   // background work has no person behind it
    }

    [Fact]
    public void A_note_or_a_customer_reply_never_carries_its_text()
    {
        var note = CentralCaseAudit.For(Event(EventType.Note, message: "Customer Jane Tan says her NRIC 900101-14-1234 is wrong"), "C-1", Nobody, "casemanagement");
        var reply = CentralCaseAudit.For(Event(EventType.Comment, "EMAIL_REPLY_RECEIVED", "x", "jane@customer.test", "Jane wrote back"), "C-1", Nobody, "casemanagement");

        Assert.Equal("Note added: case C-1.", note.Details);
        Assert.Equal("casemanagement.case.email_received", reply.Action);
        Assert.DoesNotContain("jane", reply.Details!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Opening_a_case_is_a_ViewDetails_row()
    {
        Assert.Equal(("casemanagement.case.viewed", "ViewDetails"), (CentralCaseAudit.For(Event(EventType.View), "C-1", Nobody, "casemanagement") is var r ? (r.Action, r.ActionCategory) : default));
    }

    [Fact]
    public void A_configuration_change_is_a_Configuration_row_on_its_page_and_an_email_review_quotes_no_sender()
    {
        var config = Event(EventType.Other, "CREATE", message: "Created escalation level 'Level 4'.", module: "SlaEscalation");
        config.CaseId = null;
        config.EntityName = "Escalation Level";
        var review = Event(EventType.Other, "EMAIL_APPROVED", message: "Approved the email from jane@customer.test", module: "EmailReview");
        review.CaseId = null;

        var c = CentralCaseAudit.For(config, null, Nobody, "casemanagement");
        var r = CentralCaseAudit.For(review, null, Nobody, "casemanagement");

        Assert.Equal(("casemanagement.escalation_level.created", "Configuration", "Escalation Maintenance"), (c.Action, c.ActionCategory, c.Module));
        Assert.Equal("Created escalation level 'Level 4'.", c.Details);
        Assert.Equal(("casemanagement.email_review.email_approved", "Inbox"), (r.Action, r.Module));
        Assert.DoesNotContain("jane", r.Details!, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------------ same save, once

    [Fact]
    public async Task Every_new_event_gets_its_central_copy_in_the_same_save_and_a_retried_save_adds_none_twice()
    {
        var caseId = Guid.NewGuid();
        db.Cases.Add(new Case { Id = caseId, CaseNumber = "C-07777", Title = "t" });
        var e = Event(EventType.Create, caseId: caseId);
        db.CaseEvents.Add(e);

        await db.SaveChangesAsync();
        db.CaseEvents.Add(Event(EventType.View, caseId: caseId));
        db.ChangeTracker.Entries<CaseEvent>().First(x => x.Entity.Id == e.Id).State = EntityState.Unchanged;
        await db.SaveChangesAsync();

        var rows = db.OutboxMessages.AsNoTracking().Where(m => m.Topic == OutboxTopics.Audit).ToList();
        Assert.Equal(2, rows.Count);
        var created = JsonSerializer.Deserialize<AuditEventContract>(rows.Single(r => r.EventId == e.Id).Payload, Outbox<AppDbContext>.Json)!;
        Assert.Equal(("casemanagement.case.created", "C-07777", e.Id), (created.Action, created.EntityLabel, created.EventId));
        Assert.Equal(OutboxStates.Pending, rows[0].Status);
    }

    [Fact]
    public async Task A_case_saved_earlier_is_still_named_by_its_number()
    {
        var caseId = Guid.NewGuid();
        db.Cases.Add(new Case { Id = caseId, CaseNumber = "C-08888", Title = "t" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.CaseEvents.Add(Event(EventType.Assign, oldValue: "Ann", newValue: "Lee", caseId: caseId));
        await db.SaveChangesAsync();

        var row = JsonSerializer.Deserialize<AuditEventContract>(db.OutboxMessages.AsNoTracking().Single(m => m.Topic == OutboxTopics.Audit).Payload, Outbox<AppDbContext>.Json)!;
        Assert.Equal("Assigned: case C-08888 (Ann → Lee).", row.Details);
    }

    // ------------------------------------------------------------------------------------------------ notifications

    [Fact]
    public async Task A_notification_raised_in_a_request_is_written_to_the_outbox_when_the_request_ends()
    {
        var hostId = Guid.NewGuid();
        var person = new User { Id = Guid.NewGuid(), Name = "Lee", Email = "l@x.test", Role = "Team Lead", ExternalUserId = hostId.ToString(), IsActive = true };
        db.Users.Add(person);
        await db.SaveChangesAsync();
        var sink = new OutboxNotificationSink(new Factory(Context), TestNotificationOutbox.Store(db), NullLogger<OutboxNotificationSink>.Instance);

        sink.Enqueue(new CaseNotificationRequest(CaseNotificationKinds.Nudge, [person.Id], Body: "Please pick up your queue"));
        Assert.Empty(TestNotificationOutbox.Rows(db));   // nothing is written while the request is still running
        await sink.DisposeAsync();

        var row = TestNotificationOutbox.Rows(db).Single();
        var sent = TestNotificationOutbox.Sent(db).Single();
        Assert.Equal((OutboxTopics.Notification, "remote.casemanagement.cases:agent.nudge"), (row.Topic, sent.Kind));
        Assert.Equal(new[] { hostId }, sent.SubjectUserIds);
    }

    [Fact]
    public void Notifications_and_audit_rows_have_separate_tables_in_the_database()
    {
        using var app = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=unused").Options);
        using var notifications = new NotificationOutboxDbContext(new DbContextOptionsBuilder<NotificationOutboxDbContext>().UseNpgsql("Host=unused").Options);

        Assert.Equal("AuditOutboxMessages", app.Model.FindEntityType(typeof(OutboxMessage))!.GetTableName());
        Assert.Equal("NotificationOutboxMessages", notifications.Model.FindEntityType(typeof(OutboxMessage))!.GetTableName());
        // The notification table is created by AppDbContext's migrations, with the columns the outbox library reads.
        var created = app.Model.FindEntityType(typeof(NotificationOutboxTable))!;
        Assert.Equal("NotificationOutboxMessages", created.GetTableName());
        Assert.Equal(
            notifications.Model.FindEntityType(typeof(OutboxMessage))!.GetProperties().Select(p => p.GetColumnName()).OrderBy(n => n),
            created.GetProperties().Select(p => p.GetColumnName()).OrderBy(n => n));
    }

    private sealed class Factory(Func<AppDbContext> create) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => create();
    }
}
