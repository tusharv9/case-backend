#pragma warning disable EF1001 // reads the in-memory store's name, to open the notification outbox on the same test database
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.InMemory.Infrastructure.Internal;
using OmniConnect.Outbox;

namespace CaseManagement.Tests;

/// <summary>The notification outbox (NotificationOutboxMessages) of a test's in-memory database, and what it holds.</summary>
public static class TestNotificationOutbox
{
    public static DbContextOptions<NotificationOutboxDbContext> Options(AppDbContext db) =>
        new DbContextOptionsBuilder<NotificationOutboxDbContext>()
            .UseInMemoryDatabase(db.GetService<IDbContextOptions>().FindExtension<InMemoryOptionsExtension>()!.StoreName).Options;

    public static NotificationOutboxStore Store(AppDbContext db) =>
        new(new Factory(Options(db)), new OutboxSignal<NotificationOutboxDbContext>(),
            Microsoft.Extensions.Options.Options.Create(new HostIntegrationOptions { AppKey = "casemanagement" }));

    /// <summary>The rows waiting in the notification outbox.</summary>
    public static List<OutboxMessage> Rows(AppDbContext db)
    {
        using var outbox = new NotificationOutboxDbContext(Options(db));
        // The in-memory provider keeps rows per type, not per table, so both outboxes share one set here: keep this one's topic.
        return outbox.OutboxMessages.AsNoTracking().Where(m => m.Topic == OutboxTopics.Notification).ToList();
    }

    /// <summary>The notifications waiting, as the Notification Service will receive them.</summary>
    public static List<NotificationEventContract> Sent(AppDbContext db) =>
        Rows(db).Select(r => System.Text.Json.JsonSerializer.Deserialize<NotificationEventContract>(r.Payload, Outbox<NotificationOutboxDbContext>.Json)!).ToList();

    private sealed class Factory(DbContextOptions<NotificationOutboxDbContext> options) : IDbContextFactory<NotificationOutboxDbContext>
    {
        public NotificationOutboxDbContext CreateDbContext() => new(options);
    }
}
