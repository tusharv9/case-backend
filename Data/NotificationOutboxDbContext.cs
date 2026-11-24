namespace CaseManagement.Api.Data;

using Microsoft.EntityFrameworkCore;
using OmniConnect.Outbox;

/// <summary>
/// The notification outbox (<c>NotificationOutboxMessages</c>): notifications waiting for the platform's Notification Service.
/// Kept apart from the audit outbox (<c>AuditOutboxMessages</c>, on <see cref="AppDbContext"/>) so each has its own table, its
/// own dispatcher and its own summary.
/// </summary>
/// <remarks>
/// <para><b>Same database, one table.</b> The platform's outbox library works one table per context, so this small context
/// maps only that table, in Case Management's own database. It has no migrations of its own: <see cref="AppDbContext"/>'s
/// migrations create the table (<see cref="NotificationOutboxTable"/>), so one <c>--migrate-only</c> still prepares everything.</para>
/// <para><b>With the change, or right after it.</b> <see cref="Services.Notifications.NotificationOutboxStore"/> writes here
/// inside the transaction of the change being saved when there is one (an escalation), on the same connection, so the two
/// commit together.</para>
/// </remarks>
public class NotificationOutboxDbContext(DbContextOptions<NotificationOutboxDbContext> options) : DbContext(options), IOutboxContext
{
    public const string Table = "NotificationOutboxMessages";

    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddOutbox(Table);
        // The table is created by AppDbContext's migrations (see NotificationOutboxTable), never by this context.
        modelBuilder.Entity<OutboxMessage>().ToTable(Table, t => t.ExcludeFromMigrations());
    }
}

/// <summary>
/// The notification outbox's table as <see cref="AppDbContext"/>'s migrations create it: the same columns and indexes the
/// outbox library maps (<see cref="OutboxModelBuilderExtensions.AddOutbox"/>). Only for the schema — nothing reads or writes
/// through this type; <see cref="NotificationOutboxDbContext"/> does.
/// </summary>
public class NotificationOutboxTable
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string Status { get; set; } = OutboxStates.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    internal static void Map(ModelBuilder modelBuilder)
    {
        const string table = NotificationOutboxDbContext.Table;
        modelBuilder.Entity<NotificationOutboxTable>(e =>
        {
            e.ToTable(table);
            e.HasKey(m => m.Id);
            e.Property(m => m.Topic).HasMaxLength(50).IsRequired();
            e.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(m => m.Status).HasMaxLength(20).IsRequired();
            e.Property(m => m.LastError).HasMaxLength(2000);
            e.HasIndex(m => new { m.Status, m.NextAttemptAt }).HasFilter("\"Status\" IN ('pending', 'sending')").HasDatabaseName($"IX_{table}_Due");
            e.HasIndex(m => m.EventId).HasDatabaseName($"IX_{table}_EventId");
            e.HasIndex(m => m.CreatedAt).HasDatabaseName($"IX_{table}_CreatedAt");
        });
    }
}
