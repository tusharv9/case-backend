namespace CaseManagement.Api.Data;

using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>How much data the seeder is allowed to create.</summary>
public enum SeedMode
{
    /// <summary>Create nothing.</summary>
    None,
    /// <summary>Production bootstrap only: configuration the application cannot work without
    /// (lookups, field layouts, case types, severities/SLA defaults, business hours, escalation
    /// levels). No users, teams, customers or routing rules.</summary>
    Bootstrap,
    /// <summary>Bootstrap plus sample users, teams, customers, sub-categories, routing rules and
    /// skills, so a developer or demo environment is usable immediately.</summary>
    Development
}

/// <summary>
/// Seeds default data as ONE-TIME, recorded steps (see <see cref="SeedHistoryEntry"/>).
///
/// Earlier versions re-ran every "Ensure…" method on every start, which silently re-created data an
/// administrator had deleted. Now each step runs at most once per database; changing defaults means
/// adding a new, versioned step rather than editing an old one.
/// </summary>
public static class DbSeeder
{
    private enum SeedKind
    {
        /// <summary>Required configuration; part of every environment.</summary>
        Bootstrap,
        /// <summary>Sample data; only seeded in <see cref="SeedMode.Development"/>.</summary>
        Development,
        /// <summary>Back-fills data of databases created before the current schema; only runs once,
        /// while such a database is being adopted.</summary>
        LegacyOnly
    }

    private sealed record SeedStep(string Key, SeedKind Kind, Action<AppDbContext> Run);

    // Order matters: Bootstrap steps first, then Development steps that build on them.
    private static readonly SeedStep[] Steps =
    {
        new("bootstrap.configurable-settings.v1", SeedKind.Bootstrap, SeedConfigurableSettings),
        new("bootstrap.case-management-settings.v1", SeedKind.Bootstrap, SeedCaseManagementSettings),
        new("bootstrap.case-channels-and-statuses.v1", SeedKind.Bootstrap, EnsureCaseChannelsAndStatuses),
        new("bootstrap.sla-business-hours-escalation.v1", SeedKind.Bootstrap, EnsureSlaAndEscalationMatrix),
        new("bootstrap.field-metadata.v1", SeedKind.Bootstrap,
            // ExecuteSqlRaw treats { } as format placeholders; the SQL contains a regex quantifier like {10}.
            ctx => ctx.Database.ExecuteSqlRaw(FieldMetadataDefaults.ApplySql.Replace("{", "{{").Replace("}", "}}"))),

        new("bootstrap.assignment-settings.v1", SeedKind.Bootstrap, EnsureGlobalAssignmentSettings),
        new("bootstrap.assignment-and-transfer-reasons.v1", SeedKind.Bootstrap, EnsureAssignmentAndTransferReasons),
        new("bootstrap.escalation-reasons-and-reason-detail.v1", SeedKind.Bootstrap, EnsureEscalationReasonsAndReasonDetail),
        new("bootstrap.case-list-filter-fields.v1", SeedKind.Bootstrap, EnsureCaseListFilterFields),

        new("development.users-and-departments.v1", SeedKind.Development, SeedDevUsers),
        new("development.team-memberships.v1", SeedKind.Development, EnsureDevTeamMemberships),
        new("development.sample-customers.v1", SeedKind.Development, ctx => { SeedPassportCustomer(ctx); SeedAccountNumberCustomer(ctx); }),
        new("development.sample-subcategories-and-priority-mappings.v1", SeedKind.Development, SeedSampleSubCategoriesAndPriorityMappings),
        new("development.teams-and-squads.v1", SeedKind.Development, EnsureTeamsAndSquads),
        new("development.routing-rules-and-skills.v1", SeedKind.Development, EnsureRoutingRulesAndSkills),
        new("development.skill-rules.v1", SeedKind.Development, EnsureDevSkillRules),
        new("development.customer360-standardization.v1", SeedKind.Development, EnsureCustomer360Standardization),

        new("legacy.first-response-backfill.v1", SeedKind.LegacyOnly, EnsureFirstResponseAndEscalationMatrix),
        new("legacy.case-sla-snapshot-backfill.v1", SeedKind.LegacyOnly, BackfillCaseSlaSnapshots),
    };

    /// <summary>
    /// Runs every seed step that is due. Returns a human-readable line per step for logging.
    /// </summary>
    /// <param name="adoptedLegacyDatabase">
    /// True when the database was created by the old per-boot seeder. Its Bootstrap/Development
    /// data already exists (and may have been edited or deleted on purpose), so those steps are
    /// recorded as done WITHOUT running — nothing is re-created.
    /// </param>
    public static IReadOnlyList<string> Run(AppDbContext context, SeedMode mode, bool adoptedLegacyDatabase)
    {
        var report = new List<string>();
        var applied = context.SeedHistory.AsNoTracking().Select(h => h.Key).ToHashSet();

        foreach (var step in Steps)
        {
            if (applied.Contains(step.Key)) continue;

            bool runIt;
            if (adoptedLegacyDatabase)
            {
                runIt = step.Kind == SeedKind.LegacyOnly;
            }
            else
            {
                runIt = step.Kind switch
                {
                    SeedKind.Bootstrap => mode != SeedMode.None,
                    SeedKind.Development => mode == SeedMode.Development,
                    _ => false   // nothing to back-fill in a database that was never legacy
                };
            }

            if (runIt)
            {
                if (step.Kind == SeedKind.LegacyOnly)
                {
                    // Best-effort back-fills of old data must never stop the service from starting.
                    // A failure is reported and the step stays unrecorded, so it is retried next start.
                    try
                    {
                        step.Run(context);
                    }
                    catch (Exception ex)
                    {
                        context.ChangeTracker.Clear();
                        report.Add($"FAILED   {step.Key} (non-fatal, will retry next start): {ex.Message}");
                        continue;
                    }
                }
                else
                {
                    step.Run(context);          // throws on failure => not recorded => retried next start
                }

                context.ChangeTracker.Clear();
                report.Add($"ran      {step.Key}");
            }
            else if (adoptedLegacyDatabase)
            {
                report.Add($"adopted  {step.Key} (already provisioned by previous version)");
            }
            else
            {
                continue;                       // not due in this mode; deliberately NOT recorded
            }

            if (step.Kind == SeedKind.Development)
                LinkSampleUsersToHostIdentity(context);

            context.SeedHistory.Add(new SeedHistoryEntry { Key = step.Key, AppliedAt = DateTime.UtcNow });
            context.SaveChanges();
            context.ChangeTracker.Clear();
        }

        return report;
    }

    /// <summary>
    /// Sample users are created without a Host identity. In standalone mode a user is identified by the
    /// development header, which is matched against <c>ExternalUserId</c>, so each sample user's own id
    /// becomes their external id. Only touches users that have none.
    /// </summary>
    private static void LinkSampleUsersToHostIdentity(AppDbContext context) =>
        context.Database.ExecuteSqlRaw("UPDATE \"Users\" SET \"ExternalUserId\" = \"Id\"::text WHERE \"ExternalUserId\" IS NULL");

    private static void SeedDevUsers(AppDbContext context)
    {
        try
        {
            var ccDept = context.Departments.FirstOrDefault(d => d.Code == "CC") ?? context.Departments.FirstOrDefault(d => d.Name.Contains("Contact"));
            if (ccDept == null)
            {
                ccDept = new Department { Id = Guid.NewGuid(), Name = "Contact Center", Code = "CC", CreatedAt = DateTime.UtcNow };
                context.Departments.Add(ccDept);
                context.SaveChanges();
            }

            var mfDept = context.Departments.FirstOrDefault(d => d.Code == "MF") ?? context.Departments.FirstOrDefault(d => d.Name.Contains("Micro"));
            if (mfDept == null)
            {
                mfDept = new Department { Id = Guid.NewGuid(), Name = "Micro Finance", Code = "MF", CreatedAt = DateTime.UtcNow };
                context.Departments.Add(mfDept);
                context.SaveChanges();
            }

            var fiDept = context.Departments.FirstOrDefault(d => d.Code == "FI") ?? context.Departments.FirstOrDefault(d => d.Name.Contains("Fraud"));
            if (fiDept == null)
            {
                fiDept = new Department { Id = Guid.NewGuid(), Name = "Fraud Operations", Code = "FI", CreatedAt = DateTime.UtcNow };
                context.Departments.Add(fiDept);
                context.SaveChanges();
            }

            // Update existing Siti and Aisha with teams and queues
            var siti = context.Users.FirstOrDefault(u => u.Email == "siti@bank.com");
            if (siti != null)
            {
                if (string.IsNullOrEmpty(siti.Team)) siti.Team = "Contact Centre";
                if (string.IsNullOrEmpty(siti.Queue)) siti.Queue = "General Support";
            }

            var aisha = context.Users.FirstOrDefault(u => u.Email == "aisha@bank.com");
            if (aisha != null)
            {
                if (string.IsNullOrEmpty(aisha.Team)) aisha.Team = "Lending Operations";
                if (string.IsNullOrEmpty(aisha.Queue)) aisha.Queue = "MicroFinance Tier 1";
            }

            // Seed realistic team leads, SMEs, and agents
            var devUsers = new (string Name, string Email, string Role, string Team, string Queue, UserStatus Status, Guid DeptId)[]
            {
                ("Mei Ling Tan", "meiling@bank.com", "CC Team Lead", "Contact Centre", "Escalations", UserStatus.Available, ccDept.Id),
                ("Kavitha Raj", "kavitha@bank.com", "Fraud Investigation Lead", "Fraud Operations", "Disputes Escalation", UserStatus.Available, fiDept.Id),
                ("Hafiz Osman", "hafiz@bank.com", "MicroFinance Team Lead", "Lending Operations", "MicroFinance Tier 2", UserStatus.Available, mfDept.Id),
                ("Farhan Lee", "farhan@bank.com", "Senior Dispute Specialist", "Card Disputes", "Dispute Resolution", UserStatus.Available, ccDept.Id),
                ("Nurul Huda", "nurul@bank.com", "Service Agent", "Digital Banking", "Digital Support", UserStatus.Busy, ccDept.Id),
                ("Zulkhairi Ahmad", "zulkhairi@bank.com", "Senior Fraud SME", "Fraud Risk", "AML & Fraud Risk", UserStatus.Available, fiDept.Id),
                ("Daniel Chong", "daniel@bank.com", "Loan Specialist", "Loan Processing", "Underwriting", UserStatus.Away, mfDept.Id),
                ("Amina Al-Mansoor", "amina@bank.com", "CX Supervisor", "Contact Centre", "Executive Escalations", UserStatus.Available, ccDept.Id),
                ("David Tan Sri", "david.tan@bank.com", "Head of Customer Experience", "Contact Centre", "Executive CX Office", UserStatus.Available, ccDept.Id)
            };

            foreach (var (name, email, role, team, queue, status, deptId) in devUsers)
            {
                var user = context.Users.FirstOrDefault(u => u.Email == email);
                if (user == null)
                {
                    context.Users.Add(new User
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Email = email,
                        Role = role,
                        Team = team,
                        Queue = queue,
                        Status = status,
                        DepartmentId = deptId,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    // Existing users keep whatever name, role and status they have now;
                    // only blanks are filled in.
                    if (string.IsNullOrEmpty(user.Team)) user.Team = team;
                    if (string.IsNullOrEmpty(user.Queue)) user.Queue = queue;
                }
            }

            context.SaveChanges();

            // Ensure department owners are set
            var ccLead = context.Users.FirstOrDefault(u => u.Email == "meiling@bank.com");
            if (ccLead != null && (ccDept.OwnerId == null || ccDept.OwnerId == Guid.Empty))
            {
                ccDept.OwnerId = ccLead.Id;
            }

            var fiLead = context.Users.FirstOrDefault(u => u.Email == "kavitha@bank.com");
            if (fiLead != null && (fiDept.OwnerId == null || fiDept.OwnerId == Guid.Empty))
            {
                fiDept.OwnerId = fiLead.Id;
            }

            var mfLead = context.Users.FirstOrDefault(u => u.Email == "hafiz@bank.com");
            if (mfLead != null && (mfDept.OwnerId == null || mfDept.OwnerId == Guid.Empty))
            {
                mfDept.OwnerId = mfLead.Id;
            }

            context.SaveChanges();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'SeedDevUsers' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Ships a real, working escalation template per department so an administrator can see
    /// exactly how the feature behaves (and how the placeholders are substituted) instead of
    /// starting from an empty screen. Only inserted where the department has none.
    /// </summary>
    private static void SeedPassportCustomer(AppDbContext context)
    {
        try
        {
            if (!context.Customers.Any(c => c.Passport == "A98765432" || c.NRIC == "A98765432" || c.PhoneNumber == "+60 19-876 5432"))
            {
                var passportCustomer = new Customer
                {
                    Id = Guid.NewGuid(),
                    FullName = "Sophia Martinez",
                    IdType = "Passport Number",
                    NRIC = null,
                    Passport = "A98765432",
                    PhoneNumber = "+60 19-876 5432",
                    Email = "sophia.martinez@example.com",
                    Branch = "Kuala Lumpur",
                    PreferredLanguage = "English",
                    DateOfBirth = new DateTime(1992, 8, 15, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = DateTime.UtcNow
                };

                passportCustomer.CustomAttributes.Add(new CustomerCustomAttribute
                {
                    Id = Guid.NewGuid(),
                    CustomerId = passportCustomer.Id,
                    FieldKey = "idType",
                    FieldValue = "Passport Number",
                    CreatedAt = DateTime.UtcNow
                });

                context.Customers.Add(passportCustomer);
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'SeedPassportCustomer' failed: {ex.Message}", ex);
        }
    }

    private static void SeedAccountNumberCustomer(AppDbContext context)
    {
        try
        {
            if (!context.Customers.Any(c => c.AccountNumber == "ACC-88392019" || c.PhoneNumber == "+60 17-654 3210"))
            {
                var accountCustomer = new Customer
                {
                    Id = Guid.NewGuid(),
                    FullName = "Chen Wei Ming",
                    IdType = "Account Number",
                    NRIC = null,
                    Passport = null,
                    AccountNumber = "ACC-88392019",
                    PhoneNumber = "+60 17-654 3210",
                    Email = "chen.weiming@example.com",
                    Branch = "Penang",
                    PreferredLanguage = "English",
                    DateOfBirth = new DateTime(1988, 11, 25, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAt = DateTime.UtcNow
                };

                context.Customers.Add(accountCustomer);
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'SeedAccountNumberCustomer' failed: {ex.Message}", ex);
        }
    }

    private static void SeedConfigurableSettings(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. Seed Lookup Types & Values if empty
            if (!context.LookupTypes.Any())
            {
                var langType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "PREFERRED_LANGUAGE",
                    Name = "Preferred Language",
                    Description = "Available language options for customer profile & filter dropdowns",
                    CreatedAt = now
                };

                var branchType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "HOME_BRANCH",
                    Name = "Home Branch",
                    Description = "Bank branch locations available for customer servicing",
                    CreatedAt = now
                };

                var idType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "ID_TYPE",
                    Name = "ID Type",
                    Description = "Supported customer identification types",
                    AllowAdd = false,   // the customer record has a column for each supported ID type
                    UsesFormatRules = true,   // each ID type says what its ID numbers must look like
                    CreatedAt = now
                };

                context.LookupTypes.AddRange(langType, branchType, idType);

                // Values
                context.LookupValues.AddRange(
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = langType.Id, TypeCode = "PREFERRED_LANGUAGE", Value = "English", Label = "English", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = langType.Id, TypeCode = "PREFERRED_LANGUAGE", Value = "Bahasa Malaysia", Label = "Bahasa Malaysia", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = langType.Id, TypeCode = "PREFERRED_LANGUAGE", Value = "Chinese", Label = "Chinese", DisplayOrder = 3, IsActive = true, CreatedAt = now },

                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = branchType.Id, TypeCode = "HOME_BRANCH", Value = "KL HQ", Label = "KL HQ", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = branchType.Id, TypeCode = "HOME_BRANCH", Value = "Kepong Branch", Label = "Kepong Branch", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = branchType.Id, TypeCode = "HOME_BRANCH", Value = "Kuala Lumpur Main", Label = "Kuala Lumpur Main", DisplayOrder = 3, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = branchType.Id, TypeCode = "HOME_BRANCH", Value = "Penang Branch", Label = "Penang Branch", DisplayOrder = 4, IsActive = true, CreatedAt = now },

                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = idType.Id, TypeCode = "ID_TYPE", Value = "NRIC Number", Label = "NRIC Number", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = idType.Id, TypeCode = "ID_TYPE", Value = "Passport Number", Label = "Passport Number", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = idType.Id, TypeCode = "ID_TYPE", Value = "Account Number", Label = "Account Number", DisplayOrder = 3, IsActive = true, CreatedAt = now }
                );

                context.SaveChanges();
            }

            // 2. Seed Field Configurations if empty
            if (!context.FieldConfigurations.Any())
            {
                var fields = new List<FieldConfiguration>
                {
                    // AddNewCustomer Section
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "fullName", DisplayLabel = "Full Name", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 1, FieldType = "Text", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "idType", DisplayLabel = "Choose an ID", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 2, FieldType = "Dropdown", LookupTypeCode = "ID_TYPE", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "idValue", DisplayLabel = "ID Value", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 3, FieldType = "Text", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "dateOfBirth", DisplayLabel = "Date of Birth", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 4, FieldType = "Date", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "phoneNumber", DisplayLabel = "Phone Number", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 5, FieldType = "Phone", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "email", DisplayLabel = "Email Address", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 6, FieldType = "Email", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "preferredLanguage", DisplayLabel = "Preferred Language", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 7, FieldType = "Dropdown", LookupTypeCode = "PREFERRED_LANGUAGE", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "AddNewCustomer", ApiField = "branch", DisplayLabel = "Home Branch", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 8, FieldType = "Dropdown", LookupTypeCode = "HOME_BRANCH", CreatedAt = now },

                    // ExistingCustomer Section
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "ExistingCustomer", ApiField = "idType", DisplayLabel = "Choose an ID", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 1, FieldType = "Dropdown", LookupTypeCode = "ID_TYPE", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "ExistingCustomer", ApiField = "idValue", DisplayLabel = "ID Value", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 2, FieldType = "Text", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "ExistingCustomer", ApiField = "phoneNumber", DisplayLabel = "Phone Number", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 3, FieldType = "Phone", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "ExistingCustomer", ApiField = "dateOfBirth", DisplayLabel = "Date of Birth", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 4, FieldType = "Date", CreatedAt = now },

                    // Customer 360 Filters Section
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "Filters", ApiField = "preferredLanguage", DisplayLabel = "Preferred Language", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 1, FieldType = "Dropdown", LookupTypeCode = "PREFERRED_LANGUAGE", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "Filters", ApiField = "branch", DisplayLabel = "Home Branch", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 2, FieldType = "Dropdown", LookupTypeCode = "HOME_BRANCH", CreatedAt = now }
                };

                context.FieldConfigurations.AddRange(fields);
                context.SaveChanges();
            }
            else
            {
                // Ensure idType and idValue are present in existing database
                context.Database.ExecuteSqlRaw(@"
                    INSERT INTO ""FieldConfigurations"" (""Id"", ""ModuleKey"", ""SectionKey"", ""ApiField"", ""DisplayLabel"", ""IsVisible"", ""IsRequired"", ""MaskingRule"", ""VisibleChars"", ""DisplayOrder"", ""FieldType"", ""LookupTypeCode"", ""IsCustomField"", ""CreatedAt"")
                    SELECT gen_random_uuid(), 'Customer360', 'AddNewCustomer', 'idType', 'Choose an ID', true, true, 'None', 4, 2, 'Dropdown', 'ID_TYPE', false, NOW()
                    WHERE NOT EXISTS (
                        SELECT 1 FROM ""FieldConfigurations"" WHERE ""ModuleKey"" = 'Customer360' AND ""SectionKey"" = 'AddNewCustomer' AND ""ApiField"" = 'idType'
                    );

                    INSERT INTO ""FieldConfigurations"" (""Id"", ""ModuleKey"", ""SectionKey"", ""ApiField"", ""DisplayLabel"", ""IsVisible"", ""IsRequired"", ""MaskingRule"", ""VisibleChars"", ""DisplayOrder"", ""FieldType"", ""LookupTypeCode"", ""IsCustomField"", ""CreatedAt"")
                    SELECT gen_random_uuid(), 'Customer360', 'AddNewCustomer', 'idValue', 'ID Value', true, true, 'None', 4, 3, 'Text', NULL, false, NOW()
                    WHERE NOT EXISTS (
                        SELECT 1 FROM ""FieldConfigurations"" WHERE ""ModuleKey"" = 'Customer360' AND ""SectionKey"" = 'AddNewCustomer' AND ""ApiField"" = 'idValue'
                    );
                ");
            }

        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'SeedConfigurableSettings' failed: {ex.Message}", ex);
        }
    }

    private static void SeedCaseManagementSettings(AppDbContext context)
    {
        var now = DateTime.UtcNow;
        try
        {
            // 1. Seed COMMUNICATION_CHANNEL lookup type if missing
            var channelType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "COMMUNICATION_CHANNEL");
            if (channelType == null)
            {
                channelType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "COMMUNICATION_CHANNEL",
                    Name = "Communication Channel",
                    Description = "Supported preferred customer communication channels",
                    CreatedAt = now
                };
                context.LookupTypes.Add(channelType);
                context.SaveChanges();

                context.LookupValues.AddRange(
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = channelType.Id, TypeCode = "COMMUNICATION_CHANNEL", Value = "Email", Label = "Email", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = channelType.Id, TypeCode = "COMMUNICATION_CHANNEL", Value = "Phone", Label = "Phone", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = channelType.Id, TypeCode = "COMMUNICATION_CHANNEL", Value = "SMS", Label = "SMS", DisplayOrder = 3, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = channelType.Id, TypeCode = "COMMUNICATION_CHANNEL", Value = "WhatsApp", Label = "WhatsApp", DisplayOrder = 4, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = channelType.Id, TypeCode = "COMMUNICATION_CHANNEL", Value = "In Person", Label = "In Person", DisplayOrder = 5, IsActive = true, CreatedAt = now }
                );
                context.SaveChanges();
            }

            // 2. Seed CaseTypeConfigs if empty
            if (!context.CaseTypeConfigs.Any())
            {
                context.CaseTypeConfigs.AddRange(
                    new CaseTypeConfig { Id = Guid.NewGuid(), Code = "Complaint", Name = "Complaint", Prefix = "C-", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new CaseTypeConfig { Id = Guid.NewGuid(), Code = "Service", Name = "Service", Prefix = "S-", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new CaseTypeConfig { Id = Guid.NewGuid(), Code = "Inquiry", Name = "Inquiry", Prefix = "I-", DisplayOrder = 3, IsActive = true, CreatedAt = now }
                );
                context.SaveChanges();
            }

            // (Priorities and their SLA targets are seeded as PrioritySlaRules by EnsureSlaAndEscalationMatrix.)

            // 5. Seed CaseManagement FieldConfigurations if missing
            var existingCaseFields = context.FieldConfigurations.Where(f => f.ModuleKey == "CaseManagement").ToList();
            if (!existingCaseFields.Any(f => f.SectionKey == "CreateCase"))
            {
                context.FieldConfigurations.AddRange(
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "caseType", DisplayLabel = "Case Type", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 1, FieldType = "Dropdown", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "title", DisplayLabel = "Case Title", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 2, FieldType = "Text", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "description", DisplayLabel = "Description", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 3, FieldType = "Text", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "selectCustomer", DisplayLabel = "Select Customer", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 4, FieldType = "Dropdown", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "departmentId", DisplayLabel = "Department", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 5, FieldType = "Dropdown", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "subCategory", DisplayLabel = "Sub-category", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 6, FieldType = "Dropdown", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "preferredLanguage", DisplayLabel = "Preferred Language", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 7, FieldType = "Dropdown", LookupTypeCode = "PREFERRED_LANGUAGE", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "communicationChannel", DisplayLabel = "Preferred Communication Channel", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 8, FieldType = "Dropdown", LookupTypeCode = "COMMUNICATION_CHANNEL", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "severity", DisplayLabel = "Severity", IsVisible = true, IsRequired = true, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 9, FieldType = "Dropdown", CreatedAt = now }
                );
                context.SaveChanges();
            }

            if (!existingCaseFields.Any(f => f.SectionKey == "Filters"))
            {
                context.FieldConfigurations.AddRange(
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "Filters", ApiField = "departmentId", DisplayLabel = "Handling Department", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 1, FieldType = "Dropdown", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "Filters", ApiField = "status", DisplayLabel = "Case Status", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 2, FieldType = "Dropdown", CreatedAt = now },
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "Filters", ApiField = "caseType", DisplayLabel = "Case Type", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 3, FieldType = "Dropdown", CreatedAt = now }
                );
                context.SaveChanges();
            }

            // 6. Seed Dashboard Quick Actions lookup type if missing
            var quickActionType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "DASHBOARD_QUICK_ACTION");
            if (quickActionType == null)
            {
                quickActionType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "DASHBOARD_QUICK_ACTION",
                    Name = "Dashboard Quick Actions",
                    Description = "Configurable quick action buttons in the Dashboard header banner",
                    CreatedAt = now
                };
                context.LookupTypes.Add(quickActionType);
                context.SaveChanges();

                context.LookupValues.AddRange(
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = quickActionType.Id, TypeCode = "DASHBOARD_QUICK_ACTION", Value = "create_case", Label = "Create Case", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = quickActionType.Id, TypeCode = "DASHBOARD_QUICK_ACTION", Value = "create_customer", Label = "Create Customer", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = quickActionType.Id, TypeCode = "DASHBOARD_QUICK_ACTION", Value = "assign_case", Label = "Assign Case", DisplayOrder = 3, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = quickActionType.Id, TypeCode = "DASHBOARD_QUICK_ACTION", Value = "search_cases", Label = "Search Cases", DisplayOrder = 4, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = quickActionType.Id, TypeCode = "DASHBOARD_QUICK_ACTION", Value = "my_cases_toggle", Label = "My Cases Only", DisplayOrder = 5, IsActive = true, CreatedAt = now }
                );
                context.SaveChanges();
            }

            // 7. Seed Dashboard Date Range Options lookup type if missing
            var dateRangeType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "DASHBOARD_DATE_RANGE");
            if (dateRangeType == null)
            {
                dateRangeType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "DASHBOARD_DATE_RANGE",
                    Name = "Dashboard Date Range Options",
                    Description = "Configurable date range filter choices for the Dashboard header",
                    CreatedAt = now
                };
                context.LookupTypes.Add(dateRangeType);
                context.SaveChanges();

                context.LookupValues.AddRange(
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "today", Label = "Today", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "this_week", Label = "This Week", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "last_week", Label = "Last Week", DisplayOrder = 3, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "this_month", Label = "This Month", DisplayOrder = 4, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "last_month", Label = "Last Month", DisplayOrder = 5, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "this_quarter", Label = "This Quarter", DisplayOrder = 6, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "this_year", Label = "This Year", DisplayOrder = 7, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "all", Label = "All Time", DisplayOrder = 8, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = dateRangeType.Id, TypeCode = "DASHBOARD_DATE_RANGE", Value = "custom", Label = "Custom Range", DisplayOrder = 9, IsActive = true, CreatedAt = now }
                );
                context.SaveChanges();
            }

            // 8. Seed Case Statuses lookup type if missing
            var statusType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "CASE_STATUS");
            if (statusType == null)
            {
                statusType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "CASE_STATUS",
                    Name = "Case Statuses",
                    Description = "Configurable case statuses for filtering and status tracking",
                    CreatedAt = now
                };
                context.LookupTypes.Add(statusType);
                context.SaveChanges();

                context.LookupValues.AddRange(
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = statusType.Id, TypeCode = "CASE_STATUS", Value = "Open", Label = "Open", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = statusType.Id, TypeCode = "CASE_STATUS", Value = "InProgress", Label = "In Progress", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = statusType.Id, TypeCode = "CASE_STATUS", Value = "Escalated", Label = "Escalated", DisplayOrder = 3, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = statusType.Id, TypeCode = "CASE_STATUS", Value = "Resolved", Label = "Resolved", DisplayOrder = 4, IsActive = true, CreatedAt = now }
                );
                context.SaveChanges();
            }

            // 9. Seed SLA Statuses lookup type if missing
            var slaStatusType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "SLA_STATUS");
            if (slaStatusType == null)
            {
                slaStatusType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "SLA_STATUS",
                    Name = "SLA Statuses",
                    Description = "Configurable SLA status choices for Dashboard filter",
                    CreatedAt = now
                };
                context.LookupTypes.Add(slaStatusType);
                context.SaveChanges();

                context.LookupValues.AddRange(
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = slaStatusType.Id, TypeCode = "SLA_STATUS", Value = "healthy", Label = "Within SLA", DisplayOrder = 1, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = slaStatusType.Id, TypeCode = "SLA_STATUS", Value = "approaching", Label = "Approaching SLA", DisplayOrder = 2, IsActive = true, CreatedAt = now },
                    new LookupValue { Id = Guid.NewGuid(), LookupTypeId = slaStatusType.Id, TypeCode = "SLA_STATUS", Value = "breached", Label = "SLA Breached", DisplayOrder = 3, IsActive = true, CreatedAt = now }
                );
                context.SaveChanges();
            }

            // 10. Reconcile Case Statuses & Case Types: purge dummy entries and guarantee standard statuses
            var dummyStatuses = context.LookupValues.Where(lv => lv.TypeCode == "CASE_STATUS" && (lv.Value.ToLower() == "within customer" || lv.Label.ToLower() == "within customer")).ToList();
            if (dummyStatuses.Any())
            {
                context.LookupValues.RemoveRange(dummyStatuses);
                context.SaveChanges();
            }

            var dummyCaseTypes = context.CaseTypeConfigs.Where(ct => ct.Code == "InfoReq" || ct.Name == "Info Request").ToList();
            if (dummyCaseTypes.Any())
            {
                context.CaseTypeConfigs.RemoveRange(dummyCaseTypes);
                context.SaveChanges();
            }

            var caseStatusType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "CASE_STATUS");
            if (caseStatusType != null)
            {
                var hasClosedStatus = context.LookupValues.Any(lv => lv.TypeCode == "CASE_STATUS" && (lv.Value == "Closed" || lv.Label == "Closed"));
                if (!hasClosedStatus)
                {
                    context.LookupValues.Add(new LookupValue
                    {
                        Id = Guid.NewGuid(),
                        LookupTypeId = caseStatusType.Id,
                        TypeCode = "CASE_STATUS",
                        Value = "Closed",
                        Label = "Closed",
                        DisplayOrder = 4,
                        IsActive = true,
                        CreatedAt = now
                    });
                    context.SaveChanges();
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'SeedCaseManagementSettings' failed: {ex.Message}", ex);
        }
    }

    private static void EnsureCaseChannelsAndStatuses(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. Ensure LookupValues for COMMUNICATION_CHANNEL includes Voice, Email, WhatsApp, SMS, Branch, Web Chat, Social
            var channelType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "COMMUNICATION_CHANNEL");
            if (channelType != null)
            {
                var requiredChannels = new[] { "Voice", "Email", "WhatsApp", "SMS", "Branch", "Web Chat", "Social" };
                int order = 1;
                foreach (var ch in requiredChannels)
                {
                    var exists = context.LookupValues.Any(lv => lv.TypeCode == "COMMUNICATION_CHANNEL" && (lv.Value == ch || lv.Label == ch));
                    if (!exists)
                    {
                        context.LookupValues.Add(new LookupValue
                        {
                            Id = Guid.NewGuid(),
                            LookupTypeId = channelType.Id,
                            TypeCode = "COMMUNICATION_CHANNEL",
                            Value = ch,
                            Label = ch,
                            DisplayOrder = order,
                            IsActive = true,
                            CreatedAt = now
                        });
                    }
                    order++;
                }
                context.SaveChanges();
            }

            // 1b. SOURCE_CHANNEL: the channels cases ARRIVE through (distinct from the customer's PREFERRED channel).
            var sourceType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "SOURCE_CHANNEL");
            if (sourceType == null)
            {
                sourceType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "SOURCE_CHANNEL",
                    Name = "Source Channel",
                    Description = "Channels through which cases arrive (Create Case form and filters)",
                    CreatedAt = now
                };
                context.LookupTypes.Add(sourceType);
                context.SaveChanges();

                var sourceChannels = new[] { "Voice", "Email", "WhatsApp", "SMS", "Branch", "Web Chat", "Social" };
                context.LookupValues.AddRange(sourceChannels.Select((ch, i) => new LookupValue
                {
                    Id = Guid.NewGuid(), LookupTypeId = sourceType.Id, TypeCode = "SOURCE_CHANNEL",
                    Value = ch, Label = ch, DisplayOrder = i + 1, IsActive = true, CreatedAt = now
                }));
                context.SaveChanges();
            }

            // 2. Ensure LookupValues for CASE_STATUS has Waiting on Customer
            var statusType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "CASE_STATUS");
            if (statusType != null)
            {
                var exists = context.LookupValues.Any(lv => lv.TypeCode == "CASE_STATUS" && (lv.Value == "WaitingOnCustomer" || lv.Label == "Waiting on Customer"));
                if (!exists)
                {
                    context.LookupValues.Add(new LookupValue
                    {
                        Id = Guid.NewGuid(),
                        LookupTypeId = statusType.Id,
                        TypeCode = "CASE_STATUS",
                        Value = "WaitingOnCustomer",
                        Label = "Waiting on Customer",
                        DisplayOrder = 3,
                        IsActive = true,
                        CreatedAt = now
                    });
                    context.SaveChanges();
                }
            }

            // (Existing cases are never modified here: the seeder only fills in missing master data.)

            // 4. Ensure FieldConfigurations for CreateCase has both SourceChannel and PreferredCommunicationChannel
            var existingCreateCaseFields = context.FieldConfigurations.Where(f => f.ModuleKey == "CaseManagement" && f.SectionKey == "CreateCase").ToList();
            if (existingCreateCaseFields.Count > 0)
            {
                var prefCommField = existingCreateCaseFields.FirstOrDefault(f => f.ApiField == "communicationChannel");
                if (prefCommField != null)
                {
                    prefCommField.ApiField = "preferredCommunicationChannel";
                    prefCommField.DisplayLabel = "Preferred Communication Channel";
                }

                if (!existingCreateCaseFields.Any(f => f.ApiField == "sourceChannel"))
                {
                    context.FieldConfigurations.Add(new FieldConfiguration
                    {
                        Id = Guid.NewGuid(),
                        ModuleKey = "CaseManagement",
                        SectionKey = "CreateCase",
                        ApiField = "sourceChannel",
                        DisplayLabel = "Source Channel",
                        IsVisible = true,
                        IsRequired = true,
                        MaskingRule = "None",
                        VisibleChars = 4,
                        DisplayOrder = 10,   // 9 belongs to severity; two fields never share an order
                        FieldType = "Dropdown",
                        LookupTypeCode = "SOURCE_CHANNEL",
                        CreatedAt = now
                    });
                }
                // Both channel fields are real dropdowns backed by their own configured lists.
                foreach (var f in existingCreateCaseFields)
                {
                    if (f.ApiField == "preferredCommunicationChannel") { f.FieldType = "Dropdown"; f.LookupTypeCode = "COMMUNICATION_CHANNEL"; }
                    if (f.ApiField == "sourceChannel") { f.FieldType = "Dropdown"; f.LookupTypeCode = "SOURCE_CHANNEL"; }
                }
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureCaseChannelsAndStatuses' failed: {ex.Message}", ex);
        }
    }

    private static void EnsureFirstResponseAndEscalationMatrix(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 2. Only cases that never had a first-response due date get one. Existing SLA data
            //    (targets, actual response times, statuses) is never rewritten.
            var casesWithoutDue = context.Cases.Where(c => c.FirstResponseDueAt == null).ToList();
            foreach (var c in casesWithoutDue)
            {
                var frMinutes = c.FirstResponseTargetMinutes > 0 ? c.FirstResponseTargetMinutes : 240;
                c.FirstResponseDueAt = c.SlaStartTime.AddMinutes(frMinutes).AddMinutes(c.SlaTotalPausedMinutes);
            }
            if (casesWithoutDue.Count > 0) context.SaveChanges();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureFirstResponseAndEscalationMatrix' failed: {ex.Message}", ex);
        }
    }

    private static void EnsureSlaAndEscalationMatrix(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. Seed PrioritySlaRules if empty
            if (!context.PrioritySlaRules.Any())
            {
                var critical = new PrioritySlaRule
                {
                    Id = Guid.NewGuid(),
                    Priority = "Critical",
                    DisplayOrder = 1,
                    FirstResponseValue = 30,
                    FirstResponseUnit = "Minutes",
                    FirstResponseMinutes = 30,
                    InternalResolutionValue = 2,
                    InternalResolutionUnit = "Hours",
                    InternalResolutionMinutes = 120,
                    ExternalResolutionValue = 4,
                    ExternalResolutionUnit = "Hours",
                    ExternalResolutionMinutes = 240,
                    Version = 1,
                    CreatedAt = now
                };

                var high = new PrioritySlaRule
                {
                    Id = Guid.NewGuid(),
                    Priority = "High",
                    DisplayOrder = 2,
                    FirstResponseValue = 1,
                    FirstResponseUnit = "Hours",
                    FirstResponseMinutes = 60,
                    InternalResolutionValue = 6,
                    InternalResolutionUnit = "Hours",
                    InternalResolutionMinutes = 360,
                    ExternalResolutionValue = 8,
                    ExternalResolutionUnit = "Hours",
                    ExternalResolutionMinutes = 480,
                    Version = 1,
                    CreatedAt = now
                };

                var medium = new PrioritySlaRule
                {
                    Id = Guid.NewGuid(),
                    Priority = "Medium",
                    DisplayOrder = 3,
                    FirstResponseValue = 4,
                    FirstResponseUnit = "Hours",
                    FirstResponseMinutes = 240,
                    InternalResolutionValue = 10,
                    InternalResolutionUnit = "Hours",
                    InternalResolutionMinutes = 600,
                    ExternalResolutionValue = 12,
                    ExternalResolutionUnit = "Hours",
                    ExternalResolutionMinutes = 720,
                    Version = 1,
                    CreatedAt = now
                };

                var low = new PrioritySlaRule
                {
                    Id = Guid.NewGuid(),
                    Priority = "Low",
                    DisplayOrder = 4,
                    FirstResponseValue = 8,
                    FirstResponseUnit = "Hours",
                    FirstResponseMinutes = 480,
                    InternalResolutionValue = 22,
                    InternalResolutionUnit = "Hours",
                    InternalResolutionMinutes = 1320,
                    ExternalResolutionValue = 24,
                    ExternalResolutionUnit = "Hours",
                    ExternalResolutionMinutes = 1440,
                    Version = 1,
                    CreatedAt = now
                };

                context.PrioritySlaRules.AddRange(critical, high, medium, low);
                context.SaveChanges();
            }

            // 4. Seed BusinessHours (7 days) if empty
            if (!context.BusinessHours.Any())
            {
                var days = new[]
                {
                    (DayOfWeek.Monday, "Monday", true, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                    (DayOfWeek.Tuesday, "Tuesday", true, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                    (DayOfWeek.Wednesday, "Wednesday", true, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                    (DayOfWeek.Thursday, "Thursday", true, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                    (DayOfWeek.Friday, "Friday", true, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                    (DayOfWeek.Saturday, "Saturday", false, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                    (DayOfWeek.Sunday, "Sunday", false, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0)),
                };

                foreach (var (day, name, enabled, start, end) in days)
                {
                    context.BusinessHours.Add(new BusinessHour
                    {
                        Id = Guid.NewGuid(),
                        DayOfWeek = day,
                        DayName = name,
                        IsEnabled = enabled,
                        StartTime = start,
                        EndTime = end,
                        CreatedAt = now
                    });
                }
                context.SaveChanges();
            }

            // 5. Seed EscalationLevelConfigs (default 4 levels) if empty
            if (!context.EscalationLevelConfigs.Any())
            {
                context.EscalationLevelConfigs.AddRange(
                    new EscalationLevelConfig
                    {
                        Id = Guid.NewGuid(),
                        LevelNumber = 1,
                        Name = "Level 1",
                        AssignmentType = "Owner",
                        TargetRole = "Assigned Agent",
                        TriggerType = "SlaPercentage",
                        TriggerValue = 70,
                        TriggerDescription = "SLA consumption reaches 70%",
                        ActionDescription = "Reminder to assigned agent",
                        ReassignOwner = false,
                        DisplayOrder = 1,
                        IsActive = true,
                        CreatedAt = now
                    },
                    new EscalationLevelConfig
                    {
                        Id = Guid.NewGuid(),
                        LevelNumber = 2,
                        Name = "Level 2",
                        AssignmentType = "Role",
                        TargetRole = "Team Lead",
                        TriggerType = "SlaPercentage",
                        TriggerValue = 90,
                        TriggerDescription = "SLA consumption reaches 90%",
                        ActionDescription = "Reassign to team lead",
                        ReassignOwner = true,
                        DisplayOrder = 2,
                        IsActive = true,
                        CreatedAt = now
                    },
                    new EscalationLevelConfig
                    {
                        Id = Guid.NewGuid(),
                        LevelNumber = 3,
                        Name = "Level 3",
                        AssignmentType = "Role",
                        TargetRole = "CX Supervisor",
                        TriggerType = "SlaBreached",
                        TriggerValue = null,
                        TriggerDescription = "SLA is breached",
                        ActionDescription = "Breach review and customer callback",
                        ReassignOwner = true,
                        DisplayOrder = 3,
                        IsActive = true,
                        CreatedAt = now
                    },
                    new EscalationLevelConfig
                    {
                        Id = Guid.NewGuid(),
                        LevelNumber = 4,
                        Name = "Level 4",
                        AssignmentType = "Role",
                        TargetRole = "Head of Customer Experience",
                        TriggerType = "SlaPostBreachHours",
                        TriggerValue = 12,
                        TriggerDescription = "SLA has been breached for 12 hours",
                        ActionDescription = "Executive escalation and RCA required",
                        ReassignOwner = true,
                        DisplayOrder = 4,
                        IsActive = true,
                        CreatedAt = now
                    }
                );
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureSlaAndEscalationMatrix' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// DEVELOPMENT DATA: sample sub-categories per department and their priority mappings. These
    /// reference departments, which only exist in development seeds, so they are not part of the
    /// production bootstrap.
    /// </summary>
    private static void SeedSampleSubCategoriesAndPriorityMappings(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;
            var rules = context.PrioritySlaRules.ToList();
            PrioritySlaRule? RuleFor(string priority) => rules.FirstOrDefault(r => r.Priority == priority);
            var critical = RuleFor("Critical");
            var high = RuleFor("High");
            var medium = RuleFor("Medium");
            var low = RuleFor("Low");

            // 2. Ensure initial Department Subcategories exist for category mapping
            var allDepts = context.Departments.ToList();
            var fiDept = allDepts.FirstOrDefault(d => d.Code == "FI") ?? allDepts.FirstOrDefault(d => d.Name.Contains("Fraud", StringComparison.OrdinalIgnoreCase));
            var ccDept = allDepts.FirstOrDefault(d => d.Code == "CC") ?? allDepts.FirstOrDefault(d => d.Name.Contains("Contact", StringComparison.OrdinalIgnoreCase));
            var mfDept = allDepts.FirstOrDefault(d => d.Code == "MF") ?? allDepts.FirstOrDefault(d => d.Name.Contains("Micro", StringComparison.OrdinalIgnoreCase));

            var initialCategories = new List<(string Name, string Code, Guid? DeptId)>
            {
                ("Fraud", "FRD", fiDept?.Id),
                ("Security Incident", "SEC", fiDept?.Id),
                ("Payment Issue", "PAY", ccDept?.Id),
                ("Account Access", "ACC", ccDept?.Id),
                ("Card Dispute", "DIS", ccDept?.Id),
                ("Loan Inquiry", "LON", mfDept?.Id),
                ("General Inquiry", "GEN", ccDept?.Id)
            };

            foreach (var (name, code, deptId) in initialCategories)
            {
                if (deptId.HasValue && !context.DepartmentSubCategories.Any(s => s.Name == name))
                {
                    context.DepartmentSubCategories.Add(new DepartmentSubCategory
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Code = code,
                        DepartmentId = deptId.Value,
                        DisplayOrder = 1,
                        IsActive = true,
                        CreatedAt = now
                    });
                }
            }
            context.SaveChanges();

            // 3. Seed PriorityCategoryMappings
            var categoryMappings = new (string Category, PrioritySlaRule? Rule)[]
            {
                ("Fraud", critical),
                ("Security Incident", critical),
                ("Payment Issue", high),
                ("Account Access", high),
                ("Card Dispute", medium),
                ("Loan Inquiry", low)
            };

            foreach (var (cat, r) in categoryMappings)
            {
                if (r == null) continue;
                var subCategory = context.DepartmentSubCategories.FirstOrDefault(s => s.Name == cat);
                if (subCategory == null || context.PriorityCategoryMappings.Any(m => m.DepartmentSubCategoryId == subCategory.Id)) continue;

                context.PriorityCategoryMappings.Add(new PriorityCategoryMapping
                {
                    Id = Guid.NewGuid(),
                    PrioritySlaRuleId = r.Id,
                    DepartmentSubCategoryId = subCategory.Id,
                    CreatedAt = now
                });
            }
            context.SaveChanges();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'SeedSampleSubCategoriesAndPriorityMappings' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// LEGACY ONLY: gives cases created before SLA snapshots existed their snapshot fields.
    /// Runs once, while adopting a pre-migration database; a fresh database has no such cases.
    /// </summary>
    private static void BackfillCaseSlaSnapshots(AppDbContext context)
    {
        try
        {
            // 6. Backfill existing cases with snapshot fields without altering historical behavior
            var casesWithoutSnapshots = context.Cases.Where(c => c.ExternalResolutionDueAt == null).ToList();
            foreach (var c in casesWithoutSnapshots)
            {
                int extHours = c.SlaTargetHours > 0 ? c.SlaTargetHours : 24;
                c.ExternalResolutionTargetMinutes = extHours * 60;
                c.InternalResolutionTargetMinutes = Math.Max(60, (extHours - 2) * 60);
                c.ExternalResolutionDueAt = c.SlaStartTime.AddHours(extHours).AddMinutes(c.SlaTotalPausedMinutes);
                c.InternalResolutionDueAt = c.SlaStartTime.AddHours(Math.Max(1, extHours - 2)).AddMinutes(c.SlaTotalPausedMinutes);
                c.SlaConfigVersion = 1;
            }
            if (casesWithoutSnapshots.Count > 0) context.SaveChanges();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'BackfillCaseSlaSnapshots' failed: {ex.Message}", ex);
        }
    }

    /// <summary>The global default for how cases are given to agents. Every team follows it unless it has its own settings.</summary>
    private static void EnsureGlobalAssignmentSettings(AppDbContext context)
    {
        if (context.AssignmentConfigurations.Any(c => c.DepartmentId == null)) return;
        context.AssignmentConfigurations.Add(new AssignmentConfiguration
        {
            Id = Guid.NewGuid(), DepartmentId = null, Algorithm = "RoundRobin", MaxConcurrentCapacity = 5, IsActive = true, CreatedAt = DateTime.UtcNow
        });
        context.SaveChanges();
    }

    /// <summary>
    /// BOOTSTRAP (runs once per database): the case list's filters now come from the "Case Filters" configuration. The list used to
    /// show Priority and Channel filters that were not configured; they are added here (visible, after the existing ones) so the list
    /// keeps them, and an administrator can hide, rename or reorder them like any other filter. Existing rows are never changed.
    /// </summary>
    private static void EnsureCaseListFilterFields(AppDbContext context)
    {
        const string module = "CaseManagement", section = "Filters";
        var existing = context.FieldConfigurations.Where(f => f.ModuleKey == module && f.SectionKey == section).ToList();
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(f => f.DisplayOrder) + 1;
        foreach (var (apiField, label) in new[] { ("severity", "Priority"), ("sourceChannel", "Channel") })
        {
            if (existing.Any(f => f.ApiField == apiField)) continue;
            context.FieldConfigurations.Add(new FieldConfiguration
            {
                Id = Guid.NewGuid(), ModuleKey = module, SectionKey = section, ApiField = apiField, DisplayLabel = label,
                FieldType = "Dropdown", IsVisible = true, IsRequired = false, MaskingRule = "None", DisplayOrder = nextOrder++,
                CreatedAt = DateTime.UtcNow
            });
        }
        context.SaveChanges();
    }

    /// <summary>
    /// BOOTSTRAP (runs once per database): makes the reason lists the whole story for CM-06.
    /// 1. ESCALATION_REASON — the escalation reasons the Escalate dialog used to hard-code, now an editable list.
    /// 2. "Other" / "Others" options of the three reason lists need a typed explanation (RequiresDetail).
    /// 3. "Escalation" is deactivated (never deleted) in ASSIGNMENT_REASON: escalating is its own action with its own
    ///    reasons, not a reason for reassigning. Past events keep their text; an administrator can re-activate it.
    /// Existing values an administrator already changed are not overwritten (only missing ones are added).
    /// </summary>
    private static void EnsureEscalationReasonsAndReasonDetail(AppDbContext context)
    {
        var now = DateTime.UtcNow;

        var type = context.LookupTypes.FirstOrDefault(t => t.Code == "ESCALATION_REASON");
        if (type == null)
        {
            type = new LookupType
            {
                Id = Guid.NewGuid(),
                Code = "ESCALATION_REASON",
                Name = "Escalation Reason",
                Description = "Why a case is escalated to the next level of the escalation matrix",
                AllowAdd = true,
                CreatedAt = now
            };
            context.LookupTypes.Add(type);
            context.SaveChanges();
        }

        var escalationReasons = new[]
        {
            "SLA Breach / Risk", "Complex Technical Issue", "Customer Complaint Repeat", "Regulatory / Compliance",
            "Management Escalation", "Fraud Risk", "Other"
        };
        var order = 1;
        foreach (var reason in escalationReasons)
        {
            if (!context.LookupValues.Any(v => v.TypeCode == "ESCALATION_REASON" && (v.Value == reason || v.Label == reason)))
            {
                context.LookupValues.Add(new LookupValue
                {
                    Id = Guid.NewGuid(), LookupTypeId = type.Id, TypeCode = "ESCALATION_REASON", Value = reason, Label = reason,
                    DisplayOrder = order, IsActive = true, CreatedAt = now
                });
            }
            order++;
        }
        context.SaveChanges();

        var reasonLists = new[] { "ASSIGNMENT_REASON", "TRANSFER_REASON", "ESCALATION_REASON" };
        foreach (var other in context.LookupValues.Where(v => reasonLists.Contains(v.TypeCode) && (v.Value == "Other" || v.Value == "Others")))
        {
            other.RequiresDetail = true;
            other.UpdatedAt = now;
        }

        foreach (var escalation in context.LookupValues.Where(v => v.TypeCode == "ASSIGNMENT_REASON" && v.Value == "Escalation" && v.IsActive))
        {
            escalation.IsActive = false;
            escalation.UpdatedAt = now;
        }
        context.SaveChanges();
    }

    /// <summary>BOOTSTRAP: Seeds ASSIGNMENT_REASON and TRANSFER_REASON lookup types and standard values.</summary>
    private static void EnsureAssignmentAndTransferReasons(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. ASSIGNMENT_REASON
            var assignType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "ASSIGNMENT_REASON");
            if (assignType == null)
            {
                assignType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "ASSIGNMENT_REASON",
                    Name = "Assignment Reason",
                    Description = "Reasons for manual case assignment to agents or leads",
                    AllowAdd = true,
                    CreatedAt = now
                };
                context.LookupTypes.Add(assignType);
                context.SaveChanges();
            }

            var defaultAssignmentReasons = new[]
            {
                "Escalation",
                "Workload rebalancing",
                "Specialist required",
                "Shift handover",
                "Leave / absence coverage",
                "Customer request",
                "Others"
            };

            int assignOrder = 1;
            foreach (var reason in defaultAssignmentReasons)
            {
                var exists = context.LookupValues.Any(lv => lv.TypeCode == "ASSIGNMENT_REASON" && (lv.Value == reason || lv.Label == reason));
                if (!exists)
                {
                    context.LookupValues.Add(new LookupValue
                    {
                        Id = Guid.NewGuid(),
                        LookupTypeId = assignType.Id,
                        TypeCode = "ASSIGNMENT_REASON",
                        Value = reason,
                        Label = reason,
                        DisplayOrder = assignOrder,
                        IsActive = true,
                        CreatedAt = now
                    });
                }
                assignOrder++;
            }

            // 2. TRANSFER_REASON
            var transferType = context.LookupTypes.FirstOrDefault(lt => lt.Code == "TRANSFER_REASON");
            if (transferType == null)
            {
                transferType = new LookupType
                {
                    Id = Guid.NewGuid(),
                    Code = "TRANSFER_REASON",
                    Name = "Transfer Reason",
                    Description = "Reasons for transferring cases between teams or departments",
                    AllowAdd = true,
                    CreatedAt = now
                };
                context.LookupTypes.Add(transferType);
                context.SaveChanges();
            }

            var defaultTransferReasons = new[]
            {
                "Escalation to specialist team",
                "Workload distribution",
                "Incorrect routing",
                "Multi-department collaboration",
                "Shift handover",
                "Customer request",
                "Others"
            };

            int transferOrder = 1;
            foreach (var reason in defaultTransferReasons)
            {
                var exists = context.LookupValues.Any(lv => lv.TypeCode == "TRANSFER_REASON" && (lv.Value == reason || lv.Label == reason));
                if (!exists)
                {
                    context.LookupValues.Add(new LookupValue
                    {
                        Id = Guid.NewGuid(),
                        LookupTypeId = transferType.Id,
                        TypeCode = "TRANSFER_REASON",
                        Value = reason,
                        Label = reason,
                        DisplayOrder = transferOrder,
                        IsActive = true,
                        CreatedAt = now
                    });
                }
                transferOrder++;
            }

            context.SaveChanges();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureAssignmentAndTransferReasons' failed: {ex.Message}", ex);
        }
    }

    /// <summary>DEVELOPMENT DATA: the sample users belong to their team, because membership is what makes someone eligible for its cases.</summary>
    private static void EnsureDevTeamMemberships(AppDbContext context)
    {
        var now = DateTime.UtcNow;
        var users = context.Users.Where(u => u.DepartmentId != null).ToList();
        var existing = context.TeamMembers.Select(m => new { m.DepartmentId, m.UserId }).ToList().Select(m => (m.DepartmentId, m.UserId)).ToHashSet();
        foreach (var u in users)
        {
            if (existing.Contains((u.DepartmentId!.Value, u.Id))) continue;
            context.TeamMembers.Add(new TeamMember
            {
                Id = Guid.NewGuid(), DepartmentId = u.DepartmentId.Value, UserId = u.Id,
                MemberRole = string.IsNullOrWhiteSpace(u.Role) ? "Service Agent" : u.Role, IsAssignable = true, IsActive = true, JoinedAt = now, CreatedAt = now
            });
        }
        context.SaveChanges();
    }

    /// <summary>DEVELOPMENT DATA: sample (banking) skill rules. A new installation starts with none and defines its own.</summary>
    private static void EnsureDevSkillRules(AppDbContext context)
    {
        if (context.SkillRules.Any()) return;
        var now = DateTime.UtcNow;
        (string Skill, string Field, string Value)[] rules =
        {
            ("Fraud", "Title", "fraud"), ("Fraud", "Title", "unauthorised"), ("Fraud", "Title", "stolen"),
            ("Loans", "Title", "loan"), ("Loans", "Title", "financing"),
            ("Cards", "Title", "card"), ("Cards", "Title", "atm"),
            ("Social", "Channel", "Social"), ("Voice", "Channel", "Voice"), ("Email", "Channel", "Email"),
        };
        context.SkillRules.AddRange(rules.Select(r => new SkillRule
        {
            Id = Guid.NewGuid(), SkillName = r.Skill, MatchField = r.Field, MatchType = r.Field == "Channel" ? "Equals" : "Contains", MatchValue = r.Value, IsActive = true, CreatedAt = now
        }));
        context.SaveChanges();
    }

    private static void EnsureTeamsAndSquads(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. Ensure squads users exist
            var squadUsers = new (string Name, string Email, string Role, UserStatus Status)[]
            {
                ("Nurul Aisyah", "nurul.aisyah@bank.com", "Team Lead", UserStatus.Available),
                ("Grace Wong", "grace.wong@bank.com", "Team Lead", UserStatus.Available),
                ("Amirul Hakim", "amirul.hakim@bank.com", "Team Lead", UserStatus.Available),
                ("Farid Rahman", "farid.rahman@bank.com", "Service Agent — Voice/Chat", UserStatus.Available),
                ("Mei Ling Tan", "meiling@bank.com", "Service Agent — Digital", UserStatus.Busy),
                ("Siti Hajar", "siti.hajar@bank.com", "Service Agent — Email", UserStatus.Away),
                ("Priya Nair", "priya.nair@bank.com", "Service Agent — Social", UserStatus.Available),
                ("Rajesh Kumar", "rajesh.kumar@bank.com", "Sales Agent — Leads", UserStatus.Available),
                ("Hafiz Osman", "hafiz@bank.com", "Collections Agent", UserStatus.Away)
            };

            var defaultDept = context.Departments.OrderBy(d => d.Id).FirstOrDefault();
            var defaultDeptId = defaultDept?.Id ?? Guid.NewGuid();

            var userMap = new Dictionary<string, User>();
            foreach (var (name, email, role, status) in squadUsers)
            {
                var user = context.Users.FirstOrDefault(u => u.Email == email);
                if (user == null)
                {
                    user = new User
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Email = email,
                        Role = role,
                        Status = status,
                        DepartmentId = defaultDeptId,
                        CreatedAt = now
                    };
                    context.Users.Add(user);
                }
                else
                {
                    user.Status = status;
                    if (!string.IsNullOrWhiteSpace(role)) user.Role = role;
                }
                userMap[name] = user;
            }
            context.SaveChanges();

            // 2. Ensure Teams (Departments)
            var squads = new[]
            {
                new {
                    Name = "Service Desk — Retail",
                    Code = "SDR",
                    Function = "Case handling (retail banking)",
                    LeadName = "Nurul Aisyah",
                    MemberNames = new[] { "Farid Rahman", "Mei Ling Tan", "Siti Hajar", "Priya Nair" }
                },
                new {
                    Name = "Sales Pursuit",
                    Code = "SP",
                    Function = "Lead qualification & conversion",
                    LeadName = "Grace Wong",
                    MemberNames = new[] { "Rajesh Kumar", "Hafiz Osman" }
                }
            };

            foreach (var sq in squads)
            {
                var dept = context.Departments.FirstOrDefault(d => d.Name == sq.Name || d.Code == sq.Code);
                var lead = userMap.ContainsKey(sq.LeadName) ? userMap[sq.LeadName] : null;

                if (dept == null)
                {
                    dept = new Department
                    {
                        Id = Guid.NewGuid(),
                        Name = sq.Name,
                        Code = sq.Code,
                        Function = sq.Function,
                        OwnerId = lead?.Id,
                        IsActive = true,
                        CreatedAt = now
                    };
                    context.Departments.Add(dept);
                    context.SaveChanges();
                }
                else
                {
                    dept.Function = sq.Function;
                    if (lead != null) dept.OwnerId = lead.Id;
                    context.SaveChanges();
                }

                // Ensure TeamMembers
                foreach (var memberName in sq.MemberNames)
                {
                    if (userMap.TryGetValue(memberName, out var memberUser))
                    {
                        var tm = context.TeamMembers.FirstOrDefault(m => m.DepartmentId == dept.Id && m.UserId == memberUser.Id);
                        if (tm == null)
                        {
                            context.TeamMembers.Add(new TeamMember
                            {
                                Id = Guid.NewGuid(),
                                DepartmentId = dept.Id,
                                UserId = memberUser.Id,
                                MemberRole = memberUser.Role,
                                IsActive = true,
                                JoinedAt = now
                            });
                        }
                    }
                }
            }
            context.SaveChanges();

            // 3. Ensure a couple of at-risk and open cases for the monitor
            var sdrDept = context.Departments.FirstOrDefault(d => d.Code == "SDR");
            var cust = context.Customers.OrderBy(c => c.Id).FirstOrDefault();
            var farid = userMap.ContainsKey("Farid Rahman") ? userMap["Farid Rahman"] : null;
            var meiLing = userMap.ContainsKey("Mei Ling Tan") ? userMap["Mei Ling Tan"] : null;

            // The sample cases carry the SLA snapshot of the configured "High" priority, like any case created through the app.
            var highRule = context.PrioritySlaRules.FirstOrDefault(r => r.Priority == "High");

            if (sdrDept != null && cust != null && farid != null && meiLing != null && highRule != null)
            {
                if (!context.Cases.Any(c => c.CaseNumber == "C-01041"))
                {
                    context.Cases.Add(new Case
                    {
                        Id = Guid.NewGuid(),
                        CaseNumber = "C-01041",
                        CaseType = "Complaint",
                        Title = "Unauthorised card transaction RM 2,500 via ATM",
                        Description = "Customer disputes unknown cash withdrawal from card ending in 8821.",
                        Status = CaseStatus.InProgress,
                        Severity = "High",
                        SourceChannel = "Voice",
                        CommunicationChannel = "Voice",
                        DepartmentId = sdrDept.Id,
                        CustomerId = cust.Id,
                        OwnerId = meiLing.Id,
                        SlaStartTime = now.AddHours(-3.3),
                        CreatedAt = now.AddHours(-3.3)
                    }.WithSlaSnapshot(highRule));
                }

                if (!context.Cases.Any(c => c.CaseNumber == "S-01042"))
                {
                    context.Cases.Add(new Case
                    {
                        Id = Guid.NewGuid(),
                        CaseNumber = "S-01042",
                        CaseType = "Service",
                        Title = "ASB financing payment failed twice after scheduled debit",
                        Description = "Monthly repayment not reflected in financing balance despite auto-debit deduction.",
                        Status = CaseStatus.Open,
                        Severity = "High",
                        SourceChannel = "Email",
                        CommunicationChannel = "Email",
                        DepartmentId = sdrDept.Id,
                        CustomerId = cust.Id,
                        OwnerId = farid.Id,
                        SlaStartTime = now.AddHours(-3.6),
                        CreatedAt = now.AddHours(-3.6)
                    }.WithSlaSnapshot(highRule));
                }
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureTeamsAndSquads' failed: {ex.Message}", ex);
        }
    }

    private static void EnsureRoutingRulesAndSkills(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. Ensure required departments exist
            var rfdDept = context.Departments.FirstOrDefault(d => d.Code == "RFD" || d.Name.Contains("Fraud"));
            if (rfdDept == null)
            {
                rfdDept = new Department
                {
                    Id = Guid.NewGuid(),
                    Name = "Risk & Fraud Dept",
                    Code = "RFD",
                    Function = "Fraud detection and dispute management",
                    IsActive = true,
                    CreatedAt = now
                };
                context.Departments.Add(rfdDept);
                context.SaveChanges();
            }

            var pbsDept = context.Departments.FirstOrDefault(d => d.Code == "PBS" || d.Name.Contains("Premier"));
            if (pbsDept == null)
            {
                pbsDept = new Department
                {
                    Id = Guid.NewGuid(),
                    Name = "Premier Banking Squad",
                    Code = "PBS",
                    Function = "High net worth & priority customer desk",
                    IsActive = true,
                    CreatedAt = now
                };
                context.Departments.Add(pbsDept);
                context.SaveChanges();
            }

            var sdrDept = context.Departments.FirstOrDefault(d => d.Code == "SDR" || d.Name.Contains("Service Desk"));
            var csDept = context.Departments.FirstOrDefault(d => d.Code == "CS" || d.Name.Contains("Campaign"));

            // 2. Ensure default Assignment Configuration
            if (!context.AssignmentConfigurations.Any())
            {
                context.AssignmentConfigurations.Add(new AssignmentConfiguration
                {
                    Id = Guid.NewGuid(),
                    DepartmentId = null,
                    Algorithm = "RoundRobin",
                    MaxConcurrentCapacity = 5,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                context.SaveChanges();
            }

            // 3. Ensure Routing Rules
            if (!context.RoutingRules.Any())
            {
                var r1Target = rfdDept?.Id ?? (sdrDept?.Id ?? Guid.NewGuid());
                var r2Target = pbsDept?.Id ?? (sdrDept?.Id ?? Guid.NewGuid());
                var r3Target = csDept?.Id ?? (sdrDept?.Id ?? Guid.NewGuid());
                var r4Target = sdrDept?.Id ?? Guid.NewGuid();

                var defaultRules = new[]
                {
                    new RoutingRule
                    {
                        Id = Guid.NewGuid(),
                        Name = "Fraud keywords → Critical queue",
                        Description = "Sub-category = Fraud",
                        EvaluationOrder = 1,
                        IsActive = true,
                        ConditionsJson = "{\"MatchType\":\"ALL\",\"Category\":\"Fraud\"}",
                        TargetDepartmentId = r1Target,
                        ActionDescription = "Route to Risk & Fraud Dept (High Priority)",
                        CreatedAt = now
                    },
                    new RoutingRule
                    {
                        Id = Guid.NewGuid(),
                        Name = "Priority segment fast-lane",
                        Description = "Priority = High/Critical • Segment = Priority / Premier • Match: any priority case from premier...",
                        EvaluationOrder = 2,
                        IsActive = true,
                        ConditionsJson = "{\"MatchType\":\"ALL\",\"Priority\":\"Critical\",\"CustomerSegment\":\"Priority\"}",
                        TargetDepartmentId = r2Target,
                        ActionDescription = "Route to Premier Banking Squad",
                        CreatedAt = now
                    },
                    new RoutingRule
                    {
                        Id = Guid.NewGuid(),
                        Name = "Social channel → Digital team",
                        Description = "Channel = Social • Type = Any • Match: all incoming social media cases (Twitter, Facebook, IG)...",
                        EvaluationOrder = 3,
                        IsActive = true,
                        ConditionsJson = "{\"MatchType\":\"ALL\",\"Channel\":\"Social\"}",
                        TargetDepartmentId = r3Target,
                        ActionDescription = "Route to Campaign Studio",
                        CreatedAt = now
                    },
                    new RoutingRule
                    {
                        Id = Guid.NewGuid(),
                        Name = "SME complaints → RM notify",
                        Description = "Segment = SME • Type = Complaint • Match: all SME complaints; auto-assign to SME desk and alert RM...",
                        EvaluationOrder = 4,
                        IsActive = false,
                        ConditionsJson = "{\"MatchType\":\"ALL\",\"CaseType\":\"Complaint\",\"CustomerSegment\":\"SME\"}",
                        TargetDepartmentId = r4Target,
                        ActionDescription = "Route to Service Desk — Retail",
                        CreatedAt = now
                    }
                };

                context.RoutingRules.AddRange(defaultRules);
                context.SaveChanges();
            }

            // 4. Ensure Agent Skills
            if (!context.AgentSkills.Any())
            {
                var skillsToSeed = new (string UserEmail, string Skill, int Level)[]
                {
                    ("priya.nair@bank.com", "Social", 5),
                    ("priya.nair@bank.com", "Digital", 4),
                    ("priya.nair@bank.com", "Complaints", 4),
                    ("meiling@bank.com", "Digital", 5),
                    ("meiling@bank.com", "Chat", 4),
                    ("meiling@bank.com", "Fraud", 3),
                    ("farid.rahman@bank.com", "Voice", 5),
                    ("farid.rahman@bank.com", "Retail", 4),
                    ("farid.rahman@bank.com", "General", 4),
                    ("rajesh.kumar@bank.com", "Sales", 5),
                    ("rajesh.kumar@bank.com", "Leads", 4),
                    ("rajesh.kumar@bank.com", "Cards", 4),
                    ("siti.hajar@bank.com", "Email", 5),
                    ("siti.hajar@bank.com", "SME", 4),
                    ("siti.hajar@bank.com", "Billing", 3)
                };

                foreach (var item in skillsToSeed)
                {
                    var user = context.Users.FirstOrDefault(u => u.Email == item.UserEmail);
                    if (user != null)
                    {
                        context.AgentSkills.Add(new AgentSkill
                        {
                            Id = Guid.NewGuid(),
                            UserId = user.Id,
                            SkillName = item.Skill,
                            ProficiencyLevel = item.Level,
                            CreatedAt = now
                        });
                    }
                }
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureRoutingRulesAndSkills' failed: {ex.Message}", ex);
        }
    }

    private static void EnsureCustomer360Standardization(AppDbContext context)
    {
        try
        {
            var now = DateTime.UtcNow;

            // 1. Remove legacy ID_TYPE options (IC Number, ID Number)
            var legacyTypes = context.LookupValues
                .Where(lv => lv.TypeCode == "ID_TYPE" && (lv.Value == "IC Number" || lv.Value == "ID Number"))
                .ToList();
            if (legacyTypes.Any())
            {
                context.LookupValues.RemoveRange(legacyTypes);
                context.SaveChanges();
            }

            // 2. Standardize Passport to Passport Number
            var passportType = context.LookupValues
                .FirstOrDefault(lv => lv.TypeCode == "ID_TYPE" && lv.Value == "Passport");
            if (passportType != null)
            {
                passportType.Value = "Passport Number";
                passportType.Label = "Passport Number";
                passportType.DisplayOrder = 2;
                context.SaveChanges();
            }

            // Ensure the 3 supported ID types exist and have correct display orders
            var idTypeHeader = context.LookupTypes.FirstOrDefault(lt => lt.Code == "ID_TYPE");
            if (idTypeHeader != null)
            {
                var supported = new (string Val, int Order)[]
                {
                    ("NRIC Number", 1),
                    ("Passport Number", 2),
                    ("Account Number", 3)
                };
                foreach (var (val, order) in supported)
                {
                    var existing = context.LookupValues.FirstOrDefault(lv => lv.TypeCode == "ID_TYPE" && lv.Value == val);
                    if (existing == null)
                    {
                        context.LookupValues.Add(new LookupValue
                        {
                            Id = Guid.NewGuid(),
                            LookupTypeId = idTypeHeader.Id,
                            TypeCode = "ID_TYPE",
                            Value = val,
                            Label = val,
                            DisplayOrder = order,
                            IsActive = true,
                            CreatedAt = now
                        });
                    }
                    else
                    {
                        existing.DisplayOrder = order;
                        existing.IsActive = true;
                    }
                }
                context.SaveChanges();
            }

            // 3. Ensure AddNewCustomer FieldConfigurations are all required
            var addCustomerFields = context.FieldConfigurations
                .Where(f => f.ModuleKey == "Customer360" && f.SectionKey == "AddNewCustomer" &&
                            (f.ApiField == "dateOfBirth" || f.ApiField == "email" || f.ApiField == "branch"))
                .ToList();
            foreach (var f in addCustomerFields)
            {
                f.IsRequired = true;
            }
            if (addCustomerFields.Any())
            {
                context.SaveChanges();
            }

            // 4. Remove obsolete Tenure filter configuration if present
            var tenureFields = context.FieldConfigurations
                .Where(f => f.ModuleKey == "Customer360" && f.ApiField == "tenure")
                .ToList();
            if (tenureFields.Any())
            {
                context.FieldConfigurations.RemoveRange(tenureFields);
                context.SaveChanges();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureCustomer360Standardization' failed: {ex.Message}", ex);
        }
    }
}

internal static class SeedCaseExtensions
{
    /// <summary>Gives a seeded case the same SLA snapshot a case created through the app gets (round-the-clock due dates are fine for demo data).</summary>
    public static Case WithSlaSnapshot(this Case c, PrioritySlaRule rule)
    {
        c.FirstResponseTargetMinutes = rule.FirstResponseMinutes;
        c.InternalResolutionTargetMinutes = rule.InternalResolutionMinutes;
        c.ExternalResolutionTargetMinutes = rule.ExternalResolutionMinutes;
        c.SlaTargetHours = (int)Math.Ceiling(rule.ExternalResolutionMinutes / 60.0);
        c.SlaConfigVersion = rule.Version;
        c.FirstResponseDueAt = c.SlaStartTime.AddMinutes(rule.FirstResponseMinutes);
        c.InternalResolutionDueAt = c.SlaStartTime.AddMinutes(rule.InternalResolutionMinutes);
        c.ExternalResolutionDueAt = c.SlaStartTime.AddMinutes(rule.ExternalResolutionMinutes);
        return c;
    }
}
