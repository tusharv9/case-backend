namespace CaseManagement.Api.Data;

using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext, OmniConnect.Outbox.IOutboxContext
{
    public const string AuditOutboxTable = "AuditOutboxMessages";

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Case> Cases { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Team> Teams { get; set; } = null!;
    public DbSet<TeamMember> TeamMembers { get; set; } = null!;
    public DbSet<CaseEvent> CaseEvents { get; set; } = null!;
    public DbSet<CaseParticipant> CaseParticipants { get; set; } = null!;
    public DbSet<LinkedCase> LinkedCases { get; set; } = null!;
    public DbSet<CaseChildRelation> CaseChildRelations { get; set; } = null!;
    /// <summary>The SLA monitor's memory of what it already said about a breach (notifications themselves live in OmniConnect).</summary>
    public DbSet<CaseSlaAlert> CaseSlaAlerts { get; set; } = null!;
    public DbSet<FieldConfiguration> FieldConfigurations { get; set; } = null!;
    public DbSet<LookupType> LookupTypes { get; set; } = null!;
    public DbSet<LookupValue> LookupValues { get; set; } = null!;
    public DbSet<Country> Countries { get; set; } = null!;
    public DbSet<UserPreference> UserPreferences { get; set; } = null!;
    public DbSet<CustomerCustomAttribute> CustomerCustomAttributes { get; set; } = null!;
    public DbSet<CaseCustomAttribute> CaseCustomAttributes { get; set; } = null!;
    public DbSet<CaseAttachment> CaseAttachments { get; set; } = null!;
    public DbSet<CaseCollaborationActivity> CaseCollaborationActivities { get; set; } = null!;
    public DbSet<CaseSwarmRequest> CaseSwarmRequests { get; set; } = null!;

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
    public DbSet<Department> Departments { get; set; } = null!;
    public DbSet<TeamDepartment> TeamDepartments { get; set; } = null!;
    public DbSet<TeamSettings> TeamSettings { get; set; } = null!;
    public DbSet<TeamCustomAttribute> TeamCustomAttributes { get; set; } = null!;
    public DbSet<TeamAssignmentPointer> TeamAssignmentPointers { get; set; } = null!;

    // Email intake: mailboxes read for new cases, the sender domains whose emails become cases, and every email read.
    public DbSet<EmailMailbox> EmailMailboxes { get; set; } = null!;
    public DbSet<EmailVerifiedDomain> EmailVerifiedDomains { get; set; } = null!;
    public DbSet<InboundEmail> InboundEmails { get; set; } = null!;
    public DbSet<EmailOrganisation> EmailOrganisations { get; set; } = null!;
    public DbSet<EmailVerifiedSender> EmailVerifiedSenders { get; set; } = null!;
    public DbSet<EmailMailboxTeam> EmailMailboxTeams { get; set; } = null!;
    public DbSet<Ticket> Tickets { get; set; } = null!;
    public DbSet<CaseEmailMessage> CaseEmailMessages { get; set; } = null!;
    public DbSet<TicketAttachment> TicketAttachments { get; set; } = null!;
    public DbSet<StoredFile> StoredFiles { get; set; } = null!;
    public DbSet<CaseStatusDefinition> CaseStatusDefinitions { get; set; } = null!;
    public DbSet<CaseStatusTransition> CaseStatusTransitions { get; set; } = null!;
    public DbSet<CaseSlaPause> CaseSlaPauses { get; set; } = null!;
    public DbSet<CaseMeeting> CaseMeetings { get; set; } = null!;
    public DbSet<EscalationLevelRole> EscalationLevelRoles { get; set; } = null!;
    public DbSet<CaseCreationRequest> CaseCreationRequests { get; set; } = null!;
    public DbSet<CaseEscalation> CaseEscalations { get; set; } = null!;
    /// <summary>
    /// The audit outbox (<c>AuditOutboxMessages</c>): each new timeline / audit event's central copy, saved with it and delivered
    /// to the platform's Audit Service (OmniConnect.Outbox). Notifications have their own outbox table (<see cref="NotificationOutboxDbContext"/>).
    /// </summary>
    public DbSet<OmniConnect.Outbox.OutboxMessage> OutboxMessages { get; set; } = null!;

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

        modelBuilder.Entity<CaseSwarmRequest>(entity =>
        {
            entity.HasOne(r => r.Case).WithMany().HasForeignKey(r => r.CaseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(r => r.RequestedByUser).WithMany().HasForeignKey(r => r.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(r => r.RecipientUser).WithMany().HasForeignKey(r => r.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.SetNull);
            entity.Property(r => r.RecipientKind).HasMaxLength(20);
            entity.Property(r => r.Status).HasMaxLength(20);
            entity.Property(r => r.Message).HasMaxLength(2000);
            entity.Property(r => r.ResponseNote).HasMaxLength(2000);
            entity.HasIndex(r => new { r.CaseId, r.CreatedAt }).IsDescending(false, true);
            // "My pending swarm requests" for the recipient.
            entity.HasIndex(r => new { r.RecipientUserId, r.Status });
        });

        // --- Performance indexes ---
        // Mirrored as idempotent CREATE INDEX IF NOT EXISTS statements in Program.cs, because
        // this project provisions with EnsureCreated() rather than Migrate(); the SQL block
        // is what brings already-provisioned databases up to this shape.

        // Board listing orders by CreatedAt DESC, optionally filtered by department.
        modelBuilder.Entity<Case>()
            .HasIndex(c => c.CreatedAt)
            .IsDescending();

        modelBuilder.Entity<Case>()
            .HasIndex(c => new { c.TeamId, c.CreatedAt })
            .IsDescending(false, true);

        // List View / Board columns filter by status and page newest first.
        modelBuilder.Entity<Case>()
            .HasIndex(c => new { c.Status, c.CreatedAt })
            .IsDescending(false, true);

        modelBuilder.Entity<Case>()
            .HasIndex(c => c.Severity);

        // Who a case is for: a local customer, or a Customer 360 customer by identifiers only — never both,
        // never neither. Deleting a local customer still removes their cases, as before the link became optional.
        modelBuilder.Entity<Case>(entity =>
        {
            entity.HasOne(c => c.Customer).WithMany(cu => cu.Cases).HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(c => c.CustomerSource).HasMaxLength(20).HasDefaultValue(CustomerSources.Local);
            entity.Property(c => c.CustomerType).HasMaxLength(20);
            entity.Property(c => c.ExternalCustomerId).HasMaxLength(100);
            entity.Property(c => c.CustomerLookupType).HasMaxLength(60);
            entity.Property(c => c.CustomerLookupKeyEncrypted).HasMaxLength(512);
            entity.Property(c => c.CustomerLookupKeyHash).HasMaxLength(64);
            entity.Property(c => c.SourceTicketNumber).HasMaxLength(20);
            entity.HasIndex(c => c.CustomerLookupKeyHash).HasDatabaseName("IX_Cases_CustomerLookupKeyHash");
            entity.HasIndex(c => new { c.CustomerType, c.ExternalCustomerId }).HasDatabaseName("IX_Cases_CustomerType_ExternalCustomerId");
            entity.ToTable(t => t.HasCheckConstraint("CK_Cases_CustomerReference",
                "(\"CustomerSource\" = 'Local' AND \"CustomerId\" IS NOT NULL AND \"ExternalCustomerId\" IS NULL AND \"CustomerLookupKeyEncrypted\" IS NULL)" +
                " OR (\"CustomerSource\" = 'Customer360' AND \"CustomerId\" IS NULL AND \"CustomerType\" IN ('Individual','NonIndividual')" +
                " AND \"ExternalCustomerId\" IS NOT NULL AND \"CustomerLookupType\" IS NOT NULL AND \"CustomerLookupKeyEncrypted\" IS NOT NULL AND \"CustomerLookupKeyHash\" IS NOT NULL)" +
                // A registered organisation, with the registered sender as contact: no person customer and no Customer 360 identifiers.
                // OrganisationId may become null later (the organisation deleted), so it is required only by the code that makes the case.
                " OR (\"CustomerSource\" = 'Organisation' AND \"CustomerId\" IS NULL AND \"ExternalCustomerId\" IS NULL AND \"CustomerLookupKeyEncrypted\" IS NULL)"));
        });

        // Audit trail pages a global CreatedAt DESC ordering; case detail reads one case's timeline.
        modelBuilder.Entity<CaseEvent>()
            .HasIndex(ce => ce.CreatedAt)
            .IsDescending();

        // The received email a timeline entry shows (its original is rendered from the stored .eml).
        modelBuilder.Entity<CaseEvent>()
            .HasOne<InboundEmail>().WithMany().HasForeignKey(ce => ce.InboundEmailId)
            .IsRequired(false).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<StoredFile>(e =>
        {
            e.HasKey(f => f.Key);
            e.Property(f => f.Key).HasMaxLength(200);
        });

        modelBuilder.Entity<CaseEvent>()
            .HasIndex(ce => new { ce.CaseId, ce.CreatedAt })
            .IsDescending(false, true);

        // One person's activity, newest first (Team Monitoring → agent → Activity, and the audit trail's person filter).
        modelBuilder.Entity<CaseEvent>()
            .HasIndex(ce => new { ce.UserId, ce.CreatedAt })
            .IsDescending(false, true);

        // One row per case: what the SLA monitor already said about its current breach. Goes with the case.
        // Email intake. Addresses, domains and message keys are stored lower-case, so plain unique indexes are case-insensitive.
        modelBuilder.Entity<EmailMailbox>(e =>
        {
            e.HasIndex(m => m.Address).IsUnique();
            e.Property(m => m.Version).IsConcurrencyToken();
            e.Property(m => m.Name).HasMaxLength(100);
            e.Property(m => m.Address).HasMaxLength(254);
            e.Property(m => m.Username).HasMaxLength(254);
            e.Property(m => m.ImapHost).HasMaxLength(253);
            e.Property(m => m.SmtpHost).HasMaxLength(253);
            e.Property(m => m.ImapSecurity).HasMaxLength(20);
            e.Property(m => m.SmtpSecurity).HasMaxLength(20);
            e.Property(m => m.Status).HasMaxLength(20);
            e.Property(m => m.LastError).HasMaxLength(1000);
        });
        modelBuilder.Entity<EmailVerifiedDomain>(e =>
        {
            e.HasIndex(d => d.Domain).IsUnique();
            e.Property(d => d.Domain).HasMaxLength(253);
            e.Property(d => d.OrganisationName).HasMaxLength(200);
            e.Property(d => d.VerificationPolicy).HasMaxLength(20).HasDefaultValue(VerificationPolicies.EntireDomain);
            // An organisation that still has domains cannot be deleted.
            e.HasOne(d => d.Organisation).WithMany().HasForeignKey(d => d.OrganisationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => d.OrganisationId);
        });
        modelBuilder.Entity<EmailOrganisation>(e =>
        {
            e.HasIndex(o => o.NameKey).IsUnique();
            e.Property(o => o.Name).HasMaxLength(200);
            e.Property(o => o.NameKey).HasMaxLength(200);
        });
        modelBuilder.Entity<EmailVerifiedSender>(e =>
        {
            e.HasIndex(s => s.Address).IsUnique();
            e.HasIndex(s => s.VerifiedDomainId);
            e.Property(s => s.Address).HasMaxLength(254);
            e.Property(s => s.DisplayName).HasMaxLength(100);
            e.Property(s => s.Description).HasMaxLength(500);
            e.HasOne(s => s.VerifiedDomain).WithMany(d => d.Senders).HasForeignKey(s => s.VerifiedDomainId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a mailbox or a team clears the mapping; the sender stays registered.
            e.HasOne(s => s.Mailbox).WithMany().HasForeignKey(s => s.MailboxId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.Team).WithMany().HasForeignKey(s => s.TeamId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<InboundEmail>(e =>
        {
            e.HasIndex(m => m.MessageKey).IsUnique();
            e.Property(m => m.Version).IsConcurrencyToken();
            e.HasIndex(m => new { m.Status, m.ReceivedAt });
            e.HasIndex(m => m.ReceivedAt);
            e.HasIndex(m => m.CaseId);
            e.HasIndex(m => m.InternetMessageId);
            e.HasIndex(m => m.AckMessageId);
            e.Property(m => m.MessageKey).HasMaxLength(998);
            e.Property(m => m.FromAddress).HasMaxLength(254);
            e.Property(m => m.Kind).HasMaxLength(20);
            e.Property(m => m.Verification).HasMaxLength(20);
            e.Property(m => m.Status).HasMaxLength(20);
            e.Property(m => m.AckStatus).HasMaxLength(20);
            e.HasOne(m => m.Mailbox).WithMany().HasForeignKey(m => m.MailboxId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.VerifiedDomain).WithMany().HasForeignKey(m => m.VerifiedDomainId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Organisation).WithMany().HasForeignKey(m => m.OrganisationId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.VerifiedSender).WithMany().HasForeignKey(m => m.VerifiedSenderId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(m => m.OrganisationId);
            e.Property(m => m.SenderAlias).HasMaxLength(100);
            e.Property(m => m.Authenticity).HasMaxLength(20).HasDefaultValue(EmailAuthenticity.NotChecked);
            e.Property(m => m.AuthenticationDetail).HasMaxLength(500);
            e.HasOne(m => m.Case).WithMany().HasForeignKey(m => m.CaseId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(m => m.TicketId);
            e.HasOne(m => m.Ticket).WithMany().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.SetNull);
        });

        // Tickets: requests waiting to be triaged into cases (Models/Ticket.cs). The mailbox is the queue; its teams see it.
        modelBuilder.Entity<EmailMailbox>(e =>
        {
            e.Property(m => m.IntakeMode).HasMaxLength(20).HasDefaultValue(MailboxIntakeModes.Tickets);
            e.Property(m => m.DefaultCaseType).HasMaxLength(100);
            e.Property(m => m.DefaultSubCategory).HasMaxLength(200);
            e.Property(m => m.DefaultPriority).HasMaxLength(100);
            e.Property(m => m.DefaultPreferredLanguage).HasMaxLength(100);
        });
        modelBuilder.Entity<EmailMailboxTeam>(e =>
        {
            e.HasKey(x => new { x.MailboxId, x.TeamId });
            e.HasIndex(x => x.TeamId);
            e.HasOne(x => x.Mailbox).WithMany(m => m.Teams).HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Ticket>(e =>
        {
            e.HasIndex(t => t.Sequence).IsUnique();
            e.HasIndex(t => t.TicketNumber).IsUnique();
            e.HasIndex(t => new { t.Status, t.ReceivedAt });
            e.HasIndex(t => new { t.MailboxId, t.ReceivedAt });
            // One ticket makes at most one case, and one email at most one ticket: enforced by the database, whatever reaches it at once.
            e.HasIndex(t => t.CaseId).IsUnique().HasFilter("\"CaseId\" IS NOT NULL");
            e.HasIndex(t => t.InboundEmailId).IsUnique().HasFilter("\"InboundEmailId\" IS NOT NULL");
            e.HasIndex(t => t.ClaimedUntil);
            e.Property(t => t.Version).IsConcurrencyToken();
            e.Property(t => t.TicketNumber).HasMaxLength(20);
            e.Property(t => t.Source).HasMaxLength(20);
            e.Property(t => t.RequesterName).HasMaxLength(200);
            e.Property(t => t.RequesterEmail).HasMaxLength(254);
            e.Property(t => t.RequesterPhone).HasMaxLength(50);
            e.Property(t => t.Subject).HasMaxLength(500);
            e.Property(t => t.Description).HasMaxLength(20000);
            e.Property(t => t.Priority).HasMaxLength(100);
            e.Property(t => t.CaseType).HasMaxLength(100);
            e.Property(t => t.SubCategory).HasMaxLength(200);
            e.Property(t => t.SenderVerification).HasMaxLength(20);
            e.Property(t => t.SenderNote).HasMaxLength(1000);
            e.Property(t => t.Status).HasMaxLength(20);
            e.Property(t => t.CloseReason).HasMaxLength(500);
            e.Property(t => t.SlaOutcome).HasMaxLength(20);
            e.HasIndex(t => new { t.Status, t.AssignDueAt });   // the Overdue filter and count
            e.HasOne(t => t.Mailbox).WithMany().HasForeignKey(t => t.MailboxId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Case).WithMany().HasForeignKey(t => t.CaseId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<TicketAttachment>(e =>
        {
            e.HasIndex(a => a.TicketId);
            e.Property(a => a.FileName).HasMaxLength(255);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.Property(a => a.StorageKey).HasMaxLength(500);
            e.HasOne(a => a.Ticket).WithMany(t => t.Attachments).HasForeignKey(a => a.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CaseSlaAlert>(e =>
        {
            e.HasKey(a => a.CaseId);
            e.HasOne(a => a.Case).WithMany().HasForeignKey(a => a.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

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

        modelBuilder.Entity<Team>()
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
            .IsRequired(false)
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
            .HasConversion(caseStatusConverter)
            .HasField("_status")
            .UsePropertyAccessMode(PropertyAccessMode.Field);   // loading a case must not clear its StatusKey (the setter does)
        modelBuilder.Entity<Case>().Property(c => c.StatusKey).HasMaxLength(50);
        modelBuilder.Entity<Case>().HasIndex(c => c.StatusKey).HasFilter("\"StatusKey\" IS NOT NULL");   // the board and filters by custom status
        modelBuilder.Entity<Case>().Ignore(c => c.EffectiveStatusKey);

        // Configurable statuses (Configurable Settings → Status).
        modelBuilder.Entity<CaseStatusDefinition>(e =>
        {
            e.HasKey(d => d.Key);
            e.Property(d => d.Key).HasMaxLength(50);
            e.Property(d => d.Label).HasMaxLength(60);
            e.Property(d => d.Category).HasMaxLength(20);
            e.Property(d => d.SlaBehaviour).HasMaxLength(20);
            e.Property(d => d.OnPauseExpiry).HasMaxLength(20);
            e.Property(d => d.PauseExpiryStatusKey).HasMaxLength(50);
            e.Property(d => d.Color).HasMaxLength(20);
        });
        modelBuilder.Entity<CaseMeeting>(e =>
        {
            e.HasOne(m => m.Case).WithMany().HasForeignKey(m => m.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Organizer).WithMany().HasForeignKey(m => m.OrganizerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.CaseId, m.StartUtc });
            e.HasIndex(m => m.StartUtc);   // the calendar reads meetings by date range
            e.Property(m => m.Title).HasMaxLength(200);
            e.Property(m => m.TimeZone).HasMaxLength(64);
            e.Property(m => m.Agenda).HasMaxLength(4000);
            e.Property(m => m.JoinUrl).HasMaxLength(1000);
            e.Property(m => m.Provider).HasMaxLength(20);
            e.Property(m => m.ProviderMeetingId).HasMaxLength(200);
            e.Property(m => m.AttendeeEmail).HasMaxLength(254);
            e.Property(m => m.AttendeeName).HasMaxLength(200);
            e.Property(m => m.Status).HasMaxLength(20);
            e.Property(m => m.IcsUid).HasMaxLength(200);
            e.Property(m => m.CancelReason).HasMaxLength(500);
            e.Property(m => m.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<CaseEvent>()
            .HasOne<CaseMeeting>().WithMany().HasForeignKey(ce => ce.MeetingId)
            .IsRequired(false).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CaseEmailMessage>().Property(m => m.CalendarMethod).HasMaxLength(10);

        modelBuilder.Entity<CaseSlaPause>(e =>
        {
            e.HasOne(p => p.Case).WithMany().HasForeignKey(p => p.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.CaseId, p.StartedAt });
            e.HasIndex(p => p.EndedAt).HasFilter("\"EndedAt\" IS NULL");   // the open pauses the pause-limit check reads
            e.Property(p => p.StatusKey).HasMaxLength(50);
            e.Property(p => p.EndReason).HasMaxLength(20);
        });
        modelBuilder.Entity<CaseStatusTransition>(e =>
        {
            e.HasKey(t => new { t.FromKey, t.ToKey });
            e.Property(t => t.FromKey).HasMaxLength(50);
            e.Property(t => t.ToKey).HasMaxLength(50);
            e.HasOne<CaseStatusDefinition>().WithMany().HasForeignKey(t => t.FromKey).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CaseStatusDefinition>().WithMany().HasForeignKey(t => t.ToKey).OnDelete(DeleteBehavior.Cascade);
        });
            
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
        // Ticket SLA (assign-within). No model default on purpose: with one, EF would skip an explicit 0 ("no target") on insert
        // and the database would store the default instead. Existing rows get 30 minutes from the migration itself.
        modelBuilder.Entity<PrioritySlaRule>().Property(r => r.TicketAssignUnit).HasMaxLength(10);
        modelBuilder.Entity<BusinessCalendarSetting>().Property(s => s.TicketDefaultAssignUnit).HasMaxLength(10);

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

        modelBuilder.Entity<EscalationLevelConfig>(e =>
        {
            e.Property(l => l.RoutingStrategy).HasMaxLength(20).HasDefaultValue(EscalationRouting.Team);
            e.Property(l => l.FallbackPolicy).HasMaxLength(20).HasDefaultValue(EscalationFallbacks.NextLevel);
            // AssignOnAuto and ManualAllowed default to true for existing rows in the migration (not here: EF would treat false as "unset").
            e.Property(l => l.TriggerPriorities).HasMaxLength(500);
            e.HasMany(l => l.Roles).WithOne(r => r.Level!).HasForeignKey(r => r.LevelId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CaseCreationRequest>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CreatedAt);
        });

        // A ticket becomes at most one case (the database's backstop behind the ticket's version check).
        modelBuilder.Entity<Case>().HasIndex(c => c.SourceTicketId).IsUnique().HasFilter("\"SourceTicketId\" IS NOT NULL");

        modelBuilder.Entity<EscalationLevelRole>(e =>
        {
            e.Property(r => r.RoleKey).HasMaxLength(100);
            e.Property(r => r.RoleName).HasMaxLength(200);
            e.HasIndex(r => r.RoleKey).IsUnique();   // a role sits at one level only
        });

        modelBuilder.Entity<CaseEscalation>(e =>
        {
            e.HasOne(x => x.Case).WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CaseId, x.Cycle, x.ToLevel }).IsUnique();   // a level is reached once per SLA period
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.ToLevelName).HasMaxLength(200);
            e.Property(x => x.Type).HasMaxLength(20);
            e.Property(x => x.Trigger).HasMaxLength(500);
            e.Property(x => x.ReasonText).HasMaxLength(1000);
            e.Property(x => x.Comment).HasMaxLength(2000);
            e.Property(x => x.FallbackNote).HasMaxLength(1000);
            e.Property(x => x.SlaConsumedPercent).HasPrecision(7, 2);
        });

        OmniConnect.Outbox.OutboxModelBuilderExtensions.AddOutbox(modelBuilder, AuditOutboxTable);
        NotificationOutboxTable.Map(modelBuilder);   // the notification outbox's table, created by these migrations

        // Teams and their members
        modelBuilder.Entity<TeamMember>()
            .HasIndex(tm => new { tm.TeamId, tm.UserId })
            .IsUnique();

        modelBuilder.Entity<TeamMember>()
            .HasOne(tm => tm.Team)
            .WithMany(d => d.Members)
            .HasForeignKey(tm => tm.TeamId)
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
            .HasIndex(c => new { c.SettingsId, c.TeamId })
            .IsUnique();
        modelBuilder.Entity<ManualAssignmentCrossTeam>()
            .HasOne(c => c.Settings)
            .WithMany(s => s.CrossTeams)
            .HasForeignKey(c => c.SettingsId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ManualAssignmentCrossTeam>()
            .HasOne(c => c.Team)
            .WithMany()
            .HasForeignKey(c => c.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        // Departments and teams are independent; TeamDepartments maps them: one team → many departments, one department → one team.
        modelBuilder.Entity<Department>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(120);
            e.Property(d => d.Code).HasMaxLength(20);
            e.Property(d => d.Description).HasMaxLength(500);
        });
        modelBuilder.Entity<TeamDepartment>(e =>
        {
            e.HasKey(l => new { l.TeamId, l.DepartmentId });
            // A department belongs to at most one team. Enforced here, not only in code, so two concurrent saves cannot both win.
            e.HasIndex(l => l.DepartmentId).IsUnique().HasDatabaseName("UX_TeamDepartments_Department");
            // Deleting a team drops its mappings; a department that is still mapped cannot be deleted (unmap or deactivate it).
            e.HasOne(l => l.Team).WithMany(t => t.DepartmentLinks).HasForeignKey(l => l.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Department).WithMany(d => d.TeamLinks).HasForeignKey(l => l.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });

        // A case's business department; a department that cases belong to cannot be deleted (deactivate it instead).
        modelBuilder.Entity<Case>()
            .HasOne(c => c.Department).WithMany().HasForeignKey(c => c.DepartmentId)
            .IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Case>().HasIndex(c => c.DepartmentId);

        // Sub-categories belong to a department.
        modelBuilder.Entity<DepartmentSubCategory>()
            .HasOne(s => s.Department).WithMany().HasForeignKey(s => s.DepartmentId)
            .IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<DepartmentSubCategory>().HasIndex(s => s.DepartmentId);

        // Values of the administrator-defined Create/Edit Team fields: one per team and field, removed with the team.
        modelBuilder.Entity<TeamCustomAttribute>(e =>
        {
            e.HasIndex(a => new { a.TeamId, a.FieldKey }).IsUnique();
            e.Property(a => a.FieldKey).HasMaxLength(100);
            e.HasOne(a => a.Team).WithMany(t => t.CustomAttributes).HasForeignKey(a => a.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        // Team settings (one row); the form rules are a small jsonb list
        modelBuilder.Entity<TeamSettings>()
            .Property(s => s.FormFields)
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => TeamFormFields.Normalise(System.Text.Json.JsonSerializer.Deserialize<List<TeamFormFieldRule>>(v, (System.Text.Json.JsonSerializerOptions?)null)),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<TeamFormFieldRule>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
                    v => TeamFormFields.Normalise(v)));

        // Routing Rules Configuration
        modelBuilder.Entity<RoutingRule>()
            .HasIndex(r => r.EvaluationOrder);

        modelBuilder.Entity<RoutingRule>()
            .HasOne(r => r.TargetTeam)
            .WithMany()
            .HasForeignKey(r => r.TargetTeamId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssignmentConfiguration>()
            .HasOne(ac => ac.Team)
            .WithMany()
            .HasForeignKey(ac => ac.TeamId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // Most reads about "work in progress" (an agent's load, a team's queue, the monitor, the worker) touch only OPEN cases, which are
        // a small, bounded slice of a table that grows forever. Indexing just that slice keeps those reads flat as history piles up.
        const string openOnly = "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')";
        modelBuilder.Entity<Case>().HasIndex(c => new { c.OwnerId, c.Status }).HasFilter(openOnly).HasDatabaseName("IX_Cases_Open_Owner");
        modelBuilder.Entity<Case>().HasIndex(c => new { c.TeamId, c.Status }).HasFilter(openOnly).HasDatabaseName("IX_Cases_Open_Team");
        modelBuilder.Entity<Case>().HasIndex(c => c.SlaStartTime).HasFilter(openOnly).HasDatabaseName("IX_Cases_Open_SlaStart");

        // The dashboard counts breached outcomes; they are few, so a partial index keeps that count instant.
        modelBuilder.Entity<Case>()
            .HasIndex(c => c.SlaOutcome)
            .HasFilter("\"SlaOutcome\" = 'Breached'")
            .HasDatabaseName("IX_Cases_SlaOutcome_Breached");

        // At most one settings row per team (the row without a team is the global default), and one global row per case source.
        modelBuilder.Entity<AssignmentConfiguration>()
            .HasIndex(ac => ac.TeamId)
            .IsUnique()
            .HasFilter("\"TeamId\" IS NOT NULL");
        modelBuilder.Entity<AssignmentConfiguration>()
            .HasIndex(ac => ac.CaseSource)
            .IsUnique()
            .HasFilter("\"TeamId\" IS NULL")
            .HasDatabaseName("IX_AssignmentConfigurations_Global_CaseSource");
        modelBuilder.Entity<AssignmentConfiguration>().Property(ac => ac.CaseSource).HasMaxLength(20).HasDefaultValue(AssignmentCaseSources.Default);
        modelBuilder.Entity<Case>().Property(c => c.ContactEmail).HasMaxLength(254);
        // An organisation or sender that is deleted later leaves its cases and tickets in place, without the link.
        modelBuilder.Entity<Case>().HasOne(c => c.Organisation).WithMany().HasForeignKey(c => c.OrganisationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Case>().HasOne(c => c.ContactSender).WithMany().HasForeignKey(c => c.ContactSenderId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Ticket>().HasOne(t => t.Organisation).WithMany().HasForeignKey(t => t.OrganisationId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Ticket>().HasOne(t => t.VerifiedSender).WithMany().HasForeignKey(t => t.VerifiedSenderId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Case>().HasOne(c => c.ReplyMailbox).WithMany().HasForeignKey(c => c.ReplyMailboxId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CaseEmailMessage>(e =>
        {
            e.HasOne(m => m.Case).WithMany().HasForeignKey(m => m.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.CaseEvent).WithOne(ev => ev.EmailMessage).HasForeignKey<CaseEmailMessage>(m => m.CaseEventId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Mailbox).WithMany().HasForeignKey(m => m.MailboxId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(m => m.MessageId).IsUnique();            // a reply to it finds its case by this
            e.HasIndex(m => new { m.Status, m.NextAttemptAt }); // the sender's queue
            e.HasIndex(m => m.CaseId);
            e.Property(m => m.ToAddress).HasMaxLength(254);
            e.Property(m => m.Subject).HasMaxLength(500);
            e.Property(m => m.MessageId).HasMaxLength(500);
            e.Property(m => m.Status).HasMaxLength(20);
            e.Property(m => m.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<TeamAssignmentPointer>()
            .HasKey(p => p.TeamId);

        modelBuilder.Entity<TeamAssignmentPointer>()
            .HasOne(p => p.Team)
            .WithMany()
            .HasForeignKey(p => p.TeamId)
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
        TrackStatusAndPauses();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateAuditableEntities();
        TrackStatusAndPauses();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whatever changed a case's status or paused/resumed its SLA (a status move, Resolve, Escalate, Reopen, the pause limit,
    /// bulk work), the history follows here, in one place: when the case entered its status, and each pause as a row of
    /// <see cref="CaseSlaPause"/> — opened when the clock pauses, closed with its minutes when it resumes.
    /// </summary>
    private void TrackStatusAndPauses()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Case>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
        {
            var c = entry.Entity;
            if (entry.State == EntityState.Added)
            {
                c.StatusEnteredAt ??= c.CreatedAt == default ? now : c.CreatedAt;
                if (c.SlaPausedAt != null) CaseSlaPauses.Add(new CaseSlaPause { CaseId = c.Id, StatusKey = c.EffectiveStatusKey, StartedAt = c.SlaPausedAt.Value });
                continue;
            }

            var statusChanged = !Equals(entry.Property(x => x.Status).OriginalValue, c.Status)
                                || !Equals(entry.Property(x => x.StatusKey).OriginalValue, c.StatusKey);
            if (statusChanged && !entry.Property(x => x.StatusEnteredAt).IsModified) c.StatusEnteredAt = now;

            var pausedBefore = entry.Property(x => x.SlaPausedAt).OriginalValue;
            if (pausedBefore == null && c.SlaPausedAt != null)
            {
                CaseSlaPauses.Add(new CaseSlaPause { CaseId = c.Id, StatusKey = c.EffectiveStatusKey, StartedAt = c.SlaPausedAt.Value });
            }
            else if (pausedBefore != null && c.SlaPausedAt == null)
            {
                var open = CaseSlaPauses.Local.Where(p => p.CaseId == c.Id && p.EndedAt == null).OrderByDescending(p => p.StartedAt).FirstOrDefault()
                           ?? CaseSlaPauses.Where(p => p.CaseId == c.Id && p.EndedAt == null).OrderByDescending(p => p.StartedAt).FirstOrDefault();
                if (open != null)
                {
                    open.EndedAt = now;
                    open.PausedMinutes = Math.Max(0, c.SlaTotalPausedMinutes - entry.Property(x => x.SlaTotalPausedMinutes).OriginalValue);
                    open.EndReason ??= c.Status switch
                    {
                        CaseStatus.Resolved or CaseStatus.Closed or CaseStatus.Cancelled => "Resolved",
                        CaseStatus.Escalated => "Escalated",
                        _ => statusChanged ? "StatusChange" : "Resumed",
                    };
                }
            }
        }
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
