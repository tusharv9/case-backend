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
    /// <summary>Bootstrap plus sample customers. Never teams, departments or their mappings: those are
    /// always created by an administrator.</summary>
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

        // No step creates teams, departments, team members, sub-categories or routing rules: every team and every
        // team–department mapping is created by an administrator. (The former sample-team steps were removed; a database
        // that ran them keeps what they created, as ordinary, editable records.)
        new("development.sample-customers.v1", SeedKind.Development, ctx => { SeedPassportCustomer(ctx); SeedAccountNumberCustomer(ctx); }),
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
                    new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "Filters", ApiField = "teamId", DisplayLabel = "Team", IsVisible = true, IsRequired = false, MaskingRule = "None", VisibleChars = 4, DisplayOrder = 1, FieldType = "Dropdown", CreatedAt = now },
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

            // 5. Escalation levels are not seeded: each one names a Host role granted Case Management access, which only an
            //    administrator can choose (SLA & Routing → Escalation Matrix).
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Seed step 'EnsureSlaAndEscalationMatrix' failed: {ex.Message}", ex);
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
        if (context.AssignmentConfigurations.Any(c => c.TeamId == null)) return;
        context.AssignmentConfigurations.Add(new AssignmentConfiguration
        {
            Id = Guid.NewGuid(), TeamId = null, Algorithm = "RoundRobin", MaxConcurrentCapacity = 5, IsActive = true, CreatedAt = DateTime.UtcNow
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
