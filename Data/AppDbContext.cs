namespace CaseManagement.Api.Data;

using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Case> Cases { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Department> Departments { get; set; } = null!;
    public DbSet<TeamMember> TeamMembers { get; set; } = null!;
    public DbSet<CaseEvent> CaseEvents { get; set; } = null!;
    public DbSet<CaseParticipant> CaseParticipants { get; set; } = null!;
    public DbSet<LinkedCase> LinkedCases { get; set; } = null!;
    public DbSet<CaseChildRelation> CaseChildRelations { get; set; } = null!;
    public DbSet<NotificationItem> Notifications { get; set; } = null!;
    public DbSet<FieldConfiguration> FieldConfigurations { get; set; } = null!;
    public DbSet<LookupType> LookupTypes { get; set; } = null!;
    public DbSet<LookupValue> LookupValues { get; set; } = null!;
    public DbSet<Country> Countries { get; set; } = null!;
    public DbSet<UserPreference> UserPreferences { get; set; } = null!;
    public DbSet<CustomerCustomAttribute> CustomerCustomAttributes { get; set; } = null!;
    public DbSet<CaseCustomAttribute> CaseCustomAttributes { get; set; } = null!;
    public DbSet<CaseAttachment> CaseAttachments { get; set; } = null!;
    public DbSet<CaseCollaborationActivity> CaseCollaborationActivities { get; set; } = null!;

    public DbSet<CaseTypeConfig> CaseTypeConfigs { get; set; } = null!;
    public DbSet<DepartmentSubCategory> DepartmentSubCategories { get; set; } = null!;

    // Cases SLA & Routing
    public DbSet<PrioritySlaRule> PrioritySlaRules { get; set; } = null!;
    public DbSet<PriorityCategoryMapping> PriorityCategoryMappings { get; set; } = null!;
    public DbSet<BusinessHour> BusinessHours { get; set; } = null!;
    public DbSet<PublicHoliday> PublicHolidays { get; set; } = null!;
    public DbSet<BusinessCalendarSetting> BusinessCalendarSettings { get; set; } = null!;
    public DbSet<EscalationLevelConfig> EscalationLevelConfigs { get; set; } = null!;

    // Case Routing & Automatic Assignment Engine
    public DbSet<RoutingRule> RoutingRules { get; set; } = null!;
    public DbSet<AssignmentConfiguration> AssignmentConfigurations { get; set; } = null!;
    public DbSet<ManualAssignmentSettings> ManualAssignmentSettings { get; set; } = null!;
    public DbSet<ManualAssignmentCrossTeam> ManualAssignmentCrossTeams { get; set; } = null!;
    public DbSet<AgentSkill> AgentSkills { get; set; } = null!;
    public DbSet<SkillRule> SkillRules { get; set; } = null!;
    public DbSet<TeamAssignmentPointer> TeamAssignmentPointers { get; set; } = null!;

    // Bookkeeping for one-time seed steps (see DbSeeder)
    public DbSet<SeedHistoryEntry> SeedHistory { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Host-user projection: the Host's user id is unique among linked users.
        modelBuilder.Entity<User>()
            .HasIndex(u => u.ExternalUserId)
            .IsUnique()
            .HasFilter(@"""ExternalUserId"" IS NOT NULL");

        modelBuilder.Entity<SeedHistoryEntry>(entity =>
        {
            entity.ToTable("SeedHistory");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(200);
        });

        modelBuilder.Entity<FieldConfiguration>()
            .HasIndex(fc => new { fc.ModuleKey, fc.SectionKey, fc.ApiField })
            .IsUnique();

        modelBuilder.Entity<UserPreference>(entity =>
        {
            entity.Property(p => p.Key).HasMaxLength(100);
            entity.Property(p => p.Value).HasMaxLength(4000);
            entity.HasIndex(p => new { p.UserId, p.Key }).IsUnique();
            entity.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Country>(entity =>
        {
            entity.HasIndex(c => c.Iso2).IsUnique();
            entity.Property(c => c.Iso2).HasMaxLength(2);
            entity.Property(c => c.Iso3).HasMaxLength(3);
            entity.Property(c => c.DialCode).HasMaxLength(8);
        });

        modelBuilder.Entity<LookupType>()
            .HasIndex(lt => lt.Code)
            .IsUnique();

        modelBuilder.Entity<LookupValue>()
            .HasIndex(lv => new { lv.LookupTypeId, lv.Value })
            .IsUnique();

        modelBuilder.Entity<LookupValue>()
            .HasIndex(lv => lv.TypeCode);

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.NRIC).IsRequired(false);
            entity.Property(c => c.Passport).IsRequired(false);
            entity.Property(c => c.AccountNumber).IsRequired(false);
            entity.Property(c => c.IdType).HasDefaultValue("NRIC Number");

            entity.HasIndex(c => c.NRIC)
                .IsUnique()
                .HasFilter(@"""NRIC"" IS NOT NULL AND ""NRIC"" <> ''")
                .HasDatabaseName("IX_Customers_NRIC_Unique");

            entity.HasIndex(c => c.Passport)
                .IsUnique()
                .HasFilter(@"""Passport"" IS NOT NULL AND ""Passport"" <> ''")
                .HasDatabaseName("IX_Customers_Passport_Unique");

            entity.HasIndex(c => c.AccountNumber)
                .IsUnique()
                .HasFilter(@"""AccountNumber"" IS NOT NULL AND ""AccountNumber"" <> ''")
                .HasDatabaseName("IX_Customers_AccountNumber_Unique");

            entity.Property(c => c.PhoneCountryIso2).HasMaxLength(2).HasDefaultValue("MY");

            entity.HasIndex(c => c.PhoneNumber)
                .IsUnique()
                .HasFilter(@"""PhoneNumber"" IS NOT NULL AND ""PhoneNumber"" <> ''")
                .HasDatabaseName("IX_Customers_PhoneNumber_Unique");
        });

        modelBuilder.Entity<CaseCustomAttribute>(entity =>
        {
            entity.HasOne(a => a.Case)
                .WithMany(c => c.CustomAttributes)
                .HasForeignKey(a => a.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.CaseId, a.FieldKey }).IsUnique();
        });

        modelBuilder.Entity<CustomerCustomAttribute>()
            .HasOne(cca => cca.Customer)
            .WithMany(c => c.CustomAttributes)
            .HasForeignKey(cca => cca.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CaseParticipant>()
            .HasKey(cp => new { cp.CaseId, cp.UserId });

        modelBuilder.Entity<LinkedCase>()
            .HasKey(lc => new { lc.CaseId, lc.TargetCaseId });

        modelBuilder.Entity<LinkedCase>()
            .HasOne(lc => lc.Case)
            .WithMany(c => c.LinkedCases)
            .HasForeignKey(lc => lc.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LinkedCase>()
            .HasOne(lc => lc.TargetCase)
            .WithMany(c => c.LinkedToCases)
            .HasForeignKey(lc => lc.TargetCaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseChildRelation>()
            .HasIndex(cr => cr.ChildId)
            .IsUnique();

        modelBuilder.Entity<CaseAttachment>()
            .HasOne(ca => ca.Case)
            .WithMany(c => c.Attachments)
            .HasForeignKey(ca => ca.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CaseAttachment>()
            .HasOne(ca => ca.UploadedByUser)
            .WithMany()
            .HasForeignKey(ca => ca.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseCollaborationActivity>()
            .HasOne(a => a.Case)
            .WithMany()
            .HasForeignKey(a => a.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CaseCollaborationActivity>()
            .HasOne(a => a.ActorUser)
            .WithMany()
            .HasForeignKey(a => a.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseCollaborationActivity>()
            .HasOne(a => a.TargetUser)
            .WithMany()
            .HasForeignKey(a => a.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseCollaborationActivity>()
            .HasIndex(a => new { a.CaseId, a.CreatedAt })
            .IsDescending(false, true);

        // --- Performance indexes ---
        // Mirrored as idempotent CREATE INDEX IF NOT EXISTS statements in Program.cs, because
        // this project provisions with EnsureCreated() rather than Migrate(); the SQL block
        // is what brings already-provisioned databases up to this shape.

        // Board listing orders by CreatedAt DESC, optionally filtered by department.
        modelBuilder.Entity<Case>()
            .HasIndex(c => c.CreatedAt)
            .IsDescending();

        modelBuilder.Entity<Case>()
            .HasIndex(c => new { c.DepartmentId, c.CreatedAt })
            .IsDescending(false, true);

        // List View / Board columns filter by status and page newest first.
        modelBuilder.Entity<Case>()
            .HasIndex(c => new { c.Status, c.CreatedAt })
            .IsDescending(false, true);

        modelBuilder.Entity<Case>()
            .HasIndex(c => c.Severity);

        // Audit trail pages a global CreatedAt DESC ordering; case detail reads one case's timeline.
        modelBuilder.Entity<CaseEvent>()
            .HasIndex(ce => ce.CreatedAt)
            .IsDescending();

        modelBuilder.Entity<CaseEvent>()
            .HasIndex(ce => new { ce.CaseId, ce.CreatedAt })
            .IsDescending(false, true);

        // Notification list is always scoped to one recipient, newest first.
        modelBuilder.Entity<NotificationItem>()
            .HasIndex(n => new { n.RecipientUserId, n.CreatedAt })
            .IsDescending(false, true);

        // Unread badge is polled continuously; a partial index keeps it small.
        modelBuilder.Entity<NotificationItem>()
            .HasIndex(n => n.RecipientUserId)
            .HasFilter(@"""IsRead"" = false")
            .HasDatabaseName("IX_Notifications_Unread");

        // An event is notified to a recipient at most once, however many times the monitor sees it.
        modelBuilder.Entity<NotificationItem>()
            .HasIndex(n => new { n.RecipientUserId, n.EventKey })
            .IsUnique()
            .HasFilter("\"EventKey\" IS NOT NULL")
            .HasDatabaseName("UX_Notifications_Recipient_EventKey");

        modelBuilder.Entity<CaseChildRelation>()
            .HasOne(cr => cr.ParentCase)
            .WithMany(c => c.ChildRelations)
            .HasForeignKey(cr => cr.ParentCaseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CaseChildRelation>()
            .HasOne(cr => cr.LinkedCase)
            .WithMany()
            .HasForeignKey(cr => cr.LinkedCaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseChildRelation>()
            .HasOne(cr => cr.CreatedByUser)
            .WithMany()
            .HasForeignKey(cr => cr.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseChildRelation>()
            .Property(cr => cr.RelationType)
            .HasConversion<string>();
            
        modelBuilder.Entity<Case>()
            .HasOne(c => c.Owner)
            .WithMany()
            .HasForeignKey(c => c.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Department>()
            .HasOne(d => d.Owner)
            .WithMany()
            .HasForeignKey(d => d.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CaseEvent>()
            .HasOne(ce => ce.Case)
            .WithMany(c => c.Events)
            .HasForeignKey(ce => ce.CaseId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CaseEvent>()
            .HasOne(ce => ce.User)
            .WithMany()
            .HasForeignKey(ce => ce.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Case>()
            .HasOne(c => c.ParentCase)
            .WithMany(c => c.Subcases)
            .HasForeignKey(c => c.ParentCaseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Case>()
            .HasOne(c => c.LinkedSourceCase)
            .WithMany()
            .HasForeignKey(c => c.LinkedSourceCaseId)
            .OnDelete(DeleteBehavior.Restrict);
            
        var userStatusConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<UserStatus, string>(
            v => v.ToString(),
            v => ParseUserStatus(v)
        );

        var caseStatusConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<CaseStatus, string>(
            v => v.ToString(),
            v => ParseCaseStatus(v)
        );

        // Enums mapping to strings
        modelBuilder.Entity<Case>()
            .Property(c => c.Status)
            .HasConversion(caseStatusConverter);
            
        var eventTypeConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<EventType, string>(
            v => v.ToString(),
            v => ParseEventType(v)
        );

        modelBuilder.Entity<CaseEvent>()
            .Property(ce => ce.EventType)
            .HasConversion(eventTypeConverter);
            
        modelBuilder.Entity<CaseParticipant>()
            .Property(cp => cp.Role)
            .HasConversion<string>();
            
        modelBuilder.Entity<User>()
            .Property(u => u.Status)
            .HasConversion(userStatusConverter);

        // Cases SLA & Routing Configurations
        modelBuilder.Entity<PrioritySlaRule>()
            .HasIndex(r => r.Priority)
            .IsUnique();

        // The sub-category code is its business identifier: unique across departments when set.
        modelBuilder.Entity<DepartmentSubCategory>()
            .HasIndex(s => s.Code)
            .IsUnique()
            .HasFilter("\"Code\" <> ''");

        // One priority per sub-category.
        modelBuilder.Entity<PriorityCategoryMapping>()
            .HasIndex(m => m.DepartmentSubCategoryId)
            .IsUnique();

        modelBuilder.Entity<PriorityCategoryMapping>()
            .HasOne(m => m.PrioritySlaRule)
            .WithMany(r => r.CategoryMappings)
            .HasForeignKey(m => m.PrioritySlaRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PriorityCategoryMapping>()
            .HasOne(m => m.DepartmentSubCategory)
            .WithMany()
            .HasForeignKey(m => m.DepartmentSubCategoryId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BusinessHour>()
            .HasIndex(b => b.DayOfWeek)
            .IsUnique();

        modelBuilder.Entity<PublicHoliday>()
            .HasIndex(h => h.HolidayDate)
            .IsUnique();

        modelBuilder.Entity<EscalationLevelConfig>()
            .HasIndex(e => e.LevelNumber)
            .IsUnique();

        modelBuilder.Entity<EscalationLevelConfig>()
            .HasOne(e => e.TargetUser)
            .WithMany()
            .HasForeignKey(e => e.TargetUserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // Teams (Departments) and Team Members
        modelBuilder.Entity<TeamMember>()
            .HasIndex(tm => new { tm.DepartmentId, tm.UserId })
            .IsUnique();

        modelBuilder.Entity<TeamMember>()
            .HasOne(tm => tm.Department)
            .WithMany(d => d.Members)
            .HasForeignKey(tm => tm.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TeamMember>()
            .HasOne(tm => tm.User)
            .WithMany()
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Manual assignment policy (one row) and the teams cross-team assignment may draw on
        modelBuilder.Entity<ManualAssignmentSettings>()
            .Property(m => m.CrossTeamMode).HasMaxLength(32);
        modelBuilder.Entity<ManualAssignmentCrossTeam>()
            .HasIndex(c => new { c.SettingsId, c.DepartmentId })
            .IsUnique();
        modelBuilder.Entity<ManualAssignmentCrossTeam>()
            .HasOne(c => c.Settings)
            .WithMany(s => s.CrossTeams)
            .HasForeignKey(c => c.SettingsId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ManualAssignmentCrossTeam>()
            .HasOne(c => c.Department)
            .WithMany()
            .HasForeignKey(c => c.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Routing Rules Configuration
        modelBuilder.Entity<RoutingRule>()
            .HasIndex(r => r.EvaluationOrder);

        modelBuilder.Entity<RoutingRule>()
            .HasOne(r => r.TargetDepartment)
            .WithMany()
            .HasForeignKey(r => r.TargetDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssignmentConfiguration>()
            .HasOne(ac => ac.Department)
            .WithMany()
            .HasForeignKey(ac => ac.DepartmentId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // Most reads about "work in progress" (an agent's load, a team's queue, the monitor, the worker) touch only OPEN cases, which are
        // a small, bounded slice of a table that grows forever. Indexing just that slice keeps those reads flat as history piles up.
        const string openOnly = "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')";
        modelBuilder.Entity<Case>().HasIndex(c => new { c.OwnerId, c.Status }).HasFilter(openOnly).HasDatabaseName("IX_Cases_Open_Owner");
        modelBuilder.Entity<Case>().HasIndex(c => new { c.DepartmentId, c.Status }).HasFilter(openOnly).HasDatabaseName("IX_Cases_Open_Department");
        modelBuilder.Entity<Case>().HasIndex(c => c.SlaStartTime).HasFilter(openOnly).HasDatabaseName("IX_Cases_Open_SlaStart");

        // The dashboard counts breached outcomes; they are few, so a partial index keeps that count instant.
        modelBuilder.Entity<Case>()
            .HasIndex(c => c.SlaOutcome)
            .HasFilter("\"SlaOutcome\" = 'Breached'")
            .HasDatabaseName("IX_Cases_SlaOutcome_Breached");

        // At most one settings row per team (the row without a team is the global default).
        modelBuilder.Entity<AssignmentConfiguration>()
            .HasIndex(ac => ac.DepartmentId)
            .IsUnique()
            .HasFilter("\"DepartmentId\" IS NOT NULL");

        modelBuilder.Entity<AgentSkill>()
            .HasIndex(s => new { s.UserId, s.SkillName })
            .IsUnique();

        modelBuilder.Entity<AgentSkill>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TeamAssignmentPointer>()
            .HasKey(p => p.DepartmentId);

        modelBuilder.Entity<TeamAssignmentPointer>()
            .HasOne(p => p.Department)
            .WithMany()
            .HasForeignKey(p => p.DepartmentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TeamAssignmentPointer>()
            .HasOne(p => p.LastAssignedUser)
            .WithMany()
            .HasForeignKey(p => p.LastAssignedUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges()
    {
        UpdateAuditableEntities();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateAuditableEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void UpdateAuditableEntities()
    {
        var entries = ChangeTracker.Entries<AuditableEntity>();

        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    private static UserStatus ParseUserStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return UserStatus.Available;
        if (Enum.TryParse<UserStatus>(value, true, out var status) && Enum.IsDefined(status)) return status;
        // An unrecognised stored value must not make someone look available for work (--reconcile-data rewrites such rows).
        return UserStatus.Offline;
    }


    private static EventType ParseEventType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return EventType.Other;
        if (Enum.TryParse<EventType>(value, true, out var eventType)) return eventType;
        return EventType.Other;
    }

    private static CaseStatus ParseCaseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CaseStatus.Open;
        if (Enum.TryParse<CaseStatus>(value, true, out var status)) return status;
        if (string.Equals(value, "In_Progress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "In Progress", StringComparison.OrdinalIgnoreCase)) return CaseStatus.InProgress;
        if (string.Equals(value, "Waiting_On_Customer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Waiting on Customer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "WaitingOnCustomer", StringComparison.OrdinalIgnoreCase)) return CaseStatus.WaitingOnCustomer;
        return CaseStatus.Open;
    }
}
