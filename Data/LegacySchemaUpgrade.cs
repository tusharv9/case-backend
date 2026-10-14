namespace CaseManagement.Api.Data;

/// <summary>
/// ONE-TIME upgrade for databases that were created by the application's old startup bootstrap
/// (EnsureCreated + ad-hoc ALTER/CREATE statements), before EF Core migrations were adopted.
/// It brings such a database up to the shape the Baseline migration describes, after which the
/// Baseline migration is recorded as applied (see <see cref="LegacySchemaAdopter"/>).
///
/// Every statement is idempotent and ADDITIVE. The old bootstrap also dropped obsolete tables and
/// columns and nulled customer segments on every start; those destructive statements were
/// deliberately NOT carried over — leftover obsolete objects are harmless, lost data is not.
/// This list is frozen: new schema changes must be EF migrations, never edits here.
/// </summary>
public static class LegacySchemaUpgrade
{
    public static readonly string[] Statements =
    {
        @"ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""PreferredLanguage"" text DEFAULT 'Bahasa Malaysia';",

        @"ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""DateOfBirth"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""Passport"" text NULL;",

        @"ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""AccountNumber"" text NULL;",

        @"ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""IdType"" text DEFAULT 'NRIC Number';",

        @"ALTER TABLE ""Customers"" ALTER COLUMN ""NRIC"" DROP NOT NULL;",

        @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_PhoneNumber_Unique"" ON ""Customers"" (""PhoneNumber"") WHERE ""PhoneNumber"" IS NOT NULL AND ""PhoneNumber"" <> '';",

        @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_NRIC_Unique"" ON ""Customers"" (""NRIC"") WHERE ""NRIC"" IS NOT NULL AND ""NRIC"" <> '';",

        @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_Passport_Unique"" ON ""Customers"" (""Passport"") WHERE ""Passport"" IS NOT NULL AND ""Passport"" <> '';",

        @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_AccountNumber_Unique"" ON ""Customers"" (""AccountNumber"") WHERE ""AccountNumber"" IS NOT NULL AND ""AccountNumber"" <> '';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""CaseType"" text DEFAULT 'Complaint';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""ParentCaseId"" uuid NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""LinkedSourceCaseId"" uuid NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SubcaseType"" text DEFAULT 'Original';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""CommunicationChannel"" text DEFAULT 'Voice';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SourceChannel"" text DEFAULT 'Voice';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""PreferredCommunicationChannel"" text DEFAULT 'Phone';",

        @"UPDATE ""Cases"" SET ""SourceChannel"" = ""CommunicationChannel"" WHERE ""SourceChannel"" IS NULL OR ""SourceChannel"" = '';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""Subcategory"" text DEFAULT 'General Inquiry';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SlaPausedAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SlaTotalPausedMinutes"" integer DEFAULT 0;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""FirstResponseTargetMinutes"" integer DEFAULT 240;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""FirstResponseDueAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""FirstResponseActualAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""FirstResponseStatus"" text DEFAULT 'Pending';",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""EscalationLevel"" integer DEFAULT 1;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""Sla70ReminderSent"" boolean DEFAULT false;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""Sla90Escalated"" boolean DEFAULT false;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SlaBreachedEscalated"" boolean DEFAULT false;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""Sla12hBreachedEscalated"" boolean DEFAULT false;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SlaBreachedAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""SlaConfigurations"" ADD COLUMN IF NOT EXISTS ""FirstResponseMinutes"" integer DEFAULT 240;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""InternalResolutionTargetMinutes"" integer DEFAULT 120;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""ExternalResolutionTargetMinutes"" integer DEFAULT 240;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""InternalResolutionDueAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""ExternalResolutionDueAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Cases"" ADD COLUMN IF NOT EXISTS ""SlaConfigVersion"" integer DEFAULT 1;",

        @"CREATE TABLE IF NOT EXISTS ""PrioritySlaRules"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_PrioritySlaRules"" PRIMARY KEY,
                ""Priority"" text NOT NULL,
                ""FirstResponseValue"" integer NOT NULL DEFAULT 30,
                ""FirstResponseUnit"" text NOT NULL DEFAULT 'Minutes',
                ""FirstResponseMinutes"" integer NOT NULL DEFAULT 30,
                ""InternalResolutionValue"" integer NOT NULL DEFAULT 2,
                ""InternalResolutionUnit"" text NOT NULL DEFAULT 'Hours',
                ""InternalResolutionMinutes"" integer NOT NULL DEFAULT 120,
                ""ExternalResolutionValue"" integer NOT NULL DEFAULT 4,
                ""ExternalResolutionUnit"" text NOT NULL DEFAULT 'Hours',
                ""ExternalResolutionMinutes"" integer NOT NULL DEFAULT 240,
                ""Version"" integer NOT NULL DEFAULT 1,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PrioritySlaRules_Priority"" ON ""PrioritySlaRules"" (""Priority"");

            CREATE TABLE IF NOT EXISTS ""PriorityCategoryMappings"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_PriorityCategoryMappings"" PRIMARY KEY,
                ""PrioritySlaRuleId"" uuid NOT NULL CONSTRAINT ""FK_PriorityCategoryMappings_PrioritySlaRules"" REFERENCES ""PrioritySlaRules"" (""Id"") ON DELETE CASCADE,
                ""Priority"" text NOT NULL DEFAULT 'Medium',
                ""CategoryName"" text NOT NULL,
                ""DepartmentSubCategoryId"" uuid NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PriorityCategoryMappings_CategoryName"" ON ""PriorityCategoryMappings"" (""CategoryName"");
            CREATE INDEX IF NOT EXISTS ""IX_PriorityCategoryMappings_PrioritySlaRuleId"" ON ""PriorityCategoryMappings"" (""PrioritySlaRuleId"");

            CREATE TABLE IF NOT EXISTS ""BusinessHours"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_BusinessHours"" PRIMARY KEY,
                ""DayOfWeek"" integer NOT NULL,
                ""DayName"" text NOT NULL,
                ""IsEnabled"" boolean NOT NULL DEFAULT true,
                ""StartTime"" interval NOT NULL DEFAULT '09:00:00',
                ""EndTime"" interval NOT NULL DEFAULT '17:00:00',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_BusinessHours_DayOfWeek"" ON ""BusinessHours"" (""DayOfWeek"");

            CREATE TABLE IF NOT EXISTS ""PublicHolidays"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_PublicHolidays"" PRIMARY KEY,
                ""HolidayDate"" timestamp with time zone NOT NULL,
                ""Name"" text NOT NULL,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PublicHolidays_HolidayDate"" ON ""PublicHolidays"" (""HolidayDate"");

            CREATE TABLE IF NOT EXISTS ""EscalationLevelConfigs"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_EscalationLevelConfigs"" PRIMARY KEY,
                ""LevelNumber"" integer NOT NULL,
                ""Name"" text NOT NULL,
                ""AssignmentType"" text NOT NULL DEFAULT 'Role',
                ""TargetRole"" text NOT NULL DEFAULT 'Team Lead',
                ""TargetUserId"" uuid NULL CONSTRAINT ""FK_EscalationLevelConfigs_Users"" REFERENCES ""Users"" (""Id"") ON DELETE SET NULL,
                ""TriggerType"" text NOT NULL DEFAULT 'SlaPercentage',
                ""TriggerValue"" numeric NULL,
                ""TriggerDescription"" text NOT NULL DEFAULT '',
                ""ActionDescription"" text NOT NULL DEFAULT '',
                ""ReassignOwner"" boolean NOT NULL DEFAULT false,
                ""DisplayOrder"" integer NOT NULL DEFAULT 1,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_EscalationLevelConfigs_LevelNumber"" ON ""EscalationLevelConfigs"" (""LevelNumber"");",

        @"CREATE TABLE IF NOT EXISTS ""CaseChildRelations"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_CaseChildRelations"" PRIMARY KEY,
                ""ChildId"" text NOT NULL,
                ""ParentCaseId"" uuid NOT NULL CONSTRAINT ""FK_CaseChildRelations_Cases_ParentCaseId"" REFERENCES ""Cases"" (""Id"") ON DELETE CASCADE,
                ""RelationType"" text NOT NULL,
                ""LinkedCaseId"" uuid NULL CONSTRAINT ""FK_CaseChildRelations_Cases_LinkedCaseId"" REFERENCES ""Cases"" (""Id"") ON DELETE RESTRICT,
                ""Reason"" text NOT NULL DEFAULT '',
                ""CreatedByUserId"" uuid NOT NULL CONSTRAINT ""FK_CaseChildRelations_Users_CreatedByUserId"" REFERENCES ""Users"" (""Id"") ON DELETE RESTRICT,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CaseChildRelations_ChildId"" ON ""CaseChildRelations"" (""ChildId"");
            CREATE INDEX IF NOT EXISTS ""IX_CaseChildRelations_ParentCaseId"" ON ""CaseChildRelations"" (""ParentCaseId"");
            CREATE INDEX IF NOT EXISTS ""IX_CaseChildRelations_LinkedCaseId"" ON ""CaseChildRelations"" (""LinkedCaseId"");
            CREATE INDEX IF NOT EXISTS ""IX_CaseChildRelations_CreatedByUserId"" ON ""CaseChildRelations"" (""CreatedByUserId"");",

        @"CREATE TABLE IF NOT EXISTS ""Notifications"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_Notifications"" PRIMARY KEY
            );",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""RecipientUserId"" uuid;",

        @"DO $$ BEGIN ALTER TABLE ""Notifications"" ALTER COLUMN ""UserId"" DROP NOT NULL; EXCEPTION WHEN OTHERS THEN NULL; END $$;",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""Type"" text DEFAULT '';",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""Title"" text DEFAULT '';",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""Message"" text DEFAULT '';",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""CaseId"" uuid NULL;",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""CaseNumber"" text NULL;",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""IsRead"" boolean DEFAULT false;",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""CreatedAt"" timestamp with time zone DEFAULT NOW();",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""ReadAt"" timestamp with time zone NULL;",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""Priority"" text DEFAULT 'High';",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""ReminderCount"" integer DEFAULT 0;",

        @"ALTER TABLE ""Notifications"" ADD COLUMN IF NOT EXISTS ""LastReminderAt"" timestamp with time zone NULL;",

        @"DO $$ BEGIN ALTER TABLE ""CaseEvents"" ALTER COLUMN ""CaseId"" DROP NOT NULL; EXCEPTION WHEN OTHERS THEN NULL; END $$;",

        @"ALTER TABLE ""CaseEvents"" ADD COLUMN IF NOT EXISTS ""Module"" text NULL;",

        @"ALTER TABLE ""CaseEvents"" ADD COLUMN IF NOT EXISTS ""EntityName"" text NULL;",

        @"ALTER TABLE ""CaseEvents"" ADD COLUMN IF NOT EXISTS ""OldValue"" text NULL;",

        @"ALTER TABLE ""CaseEvents"" ADD COLUMN IF NOT EXISTS ""IsInternal"" boolean DEFAULT true;",

        @"ALTER TABLE ""CaseEvents"" ADD COLUMN IF NOT EXISTS ""Channel"" text NULL;",

        @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""Team"" text NULL;",

        @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""Queue"" text NULL;",

        @"ALTER TABLE ""Departments"" ADD COLUMN IF NOT EXISTS ""Function"" text DEFAULT '';",

        @"ALTER TABLE ""Departments"" ADD COLUMN IF NOT EXISTS ""Channels"" text DEFAULT 'Voice,Chat,Email';",

        @"CREATE TABLE IF NOT EXISTS ""TeamMembers"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_TeamMembers"" PRIMARY KEY,
                ""DepartmentId"" uuid NOT NULL CONSTRAINT ""FK_TeamMembers_Departments"" REFERENCES ""Departments"" (""Id"") ON DELETE CASCADE,
                ""UserId"" uuid NOT NULL CONSTRAINT ""FK_TeamMembers_Users"" REFERENCES ""Users"" (""Id"") ON DELETE CASCADE,
                ""MemberRole"" text NOT NULL DEFAULT 'Service Agent',
                ""PrimaryChannel"" text NOT NULL DEFAULT 'Voice',
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""JoinedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_TeamMembers_DepartmentId_UserId"" ON ""TeamMembers"" (""DepartmentId"", ""UserId"");",

        @"CREATE TABLE IF NOT EXISTS ""RoutingRules"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_RoutingRules"" PRIMARY KEY,
                ""Name"" text NOT NULL,
                ""Description"" text NOT NULL DEFAULT '',
                ""EvaluationOrder"" integer NOT NULL DEFAULT 1,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""ConditionsJson"" text NOT NULL DEFAULT '{{}}',
                ""TargetDepartmentId"" uuid NOT NULL CONSTRAINT ""FK_RoutingRules_Departments"" REFERENCES ""Departments"" (""Id"") ON DELETE RESTRICT,
                ""TargetQueueName"" text NULL,
                ""ActionDescription"" text NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_RoutingRules_EvaluationOrder"" ON ""RoutingRules"" (""EvaluationOrder"");

            CREATE TABLE IF NOT EXISTS ""AssignmentConfigurations"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_AssignmentConfigurations"" PRIMARY KEY,
                ""DepartmentId"" uuid NULL CONSTRAINT ""FK_AssignmentConfigurations_Departments"" REFERENCES ""Departments"" (""Id"") ON DELETE CASCADE,
                ""Algorithm"" text NOT NULL DEFAULT 'RoundRobin',
                ""MaxConcurrentCapacity"" integer NOT NULL DEFAULT 5,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );

            CREATE TABLE IF NOT EXISTS ""AgentSkills"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_AgentSkills"" PRIMARY KEY,
                ""UserId"" uuid NOT NULL CONSTRAINT ""FK_AgentSkills_Users"" REFERENCES ""Users"" (""Id"") ON DELETE CASCADE,
                ""SkillName"" text NOT NULL,
                ""ProficiencyLevel"" integer NOT NULL DEFAULT 1,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AgentSkills_UserId_SkillName"" ON ""AgentSkills"" (""UserId"", ""SkillName"");

            CREATE TABLE IF NOT EXISTS ""TeamAssignmentPointers"" (
                ""DepartmentId"" uuid NOT NULL CONSTRAINT ""PK_TeamAssignmentPointers"" PRIMARY KEY REFERENCES ""Departments"" (""Id"") ON DELETE CASCADE,
                ""LastAssignedUserId"" uuid NOT NULL CONSTRAINT ""FK_TeamAssignmentPointers_Users"" REFERENCES ""Users"" (""Id"") ON DELETE RESTRICT,
                ""LastAssignedAt"" timestamp with time zone NOT NULL DEFAULT NOW()
            );",

        @"CREATE TABLE IF NOT EXISTS ""CaseAttachments"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_CaseAttachments"" PRIMARY KEY,
                ""CaseId"" uuid NOT NULL CONSTRAINT ""FK_CaseAttachments_Cases_CaseId"" REFERENCES ""Cases"" (""Id"") ON DELETE CASCADE,
                ""FileName"" text NOT NULL,
                ""FileType"" text NOT NULL,
                ""FileSizeBytes"" bigint NOT NULL DEFAULT 0,
                ""StoragePath"" text NOT NULL,
                ""Note"" text NULL,
                ""UploadedByUserId"" uuid NOT NULL CONSTRAINT ""FK_CaseAttachments_Users_UploadedByUserId"" REFERENCES ""Users"" (""Id"") ON DELETE RESTRICT,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                ""UpdatedAt"" timestamp with time zone NULL
            );
            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""FileType"" text DEFAULT '';
            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""ContentType"" text DEFAULT 'application/octet-stream';
            DO $$ BEGIN ALTER TABLE ""CaseAttachments"" ALTER COLUMN ""ContentType"" DROP NOT NULL; EXCEPTION WHEN OTHERS THEN NULL; END $$;
            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""FileSizeBytes"" bigint DEFAULT 0;
            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""FileSize"" bigint DEFAULT 0;
            DO $$ BEGIN ALTER TABLE ""CaseAttachments"" ALTER COLUMN ""FileSize"" DROP NOT NULL; EXCEPTION WHEN OTHERS THEN NULL; END $$;
            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""StoragePath"" text DEFAULT '';

            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""Note"" text NULL;
            ALTER TABLE ""CaseAttachments"" ADD COLUMN IF NOT EXISTS ""UploadedByUserId"" uuid;
            CREATE INDEX IF NOT EXISTS ""IX_CaseAttachments_CaseId"" ON ""CaseAttachments"" (""CaseId"");
            CREATE INDEX IF NOT EXISTS ""IX_CaseAttachments_UploadedByUserId"" ON ""CaseAttachments"" (""UploadedByUserId"");",

        @"CREATE INDEX IF NOT EXISTS ""IX_Notifications_RecipientUserId"" ON ""Notifications"" (""RecipientUserId"");
            CREATE INDEX IF NOT EXISTS ""IX_Notifications_IsRead"" ON ""Notifications"" (""IsRead"");
            CREATE INDEX IF NOT EXISTS ""IX_Notifications_CreatedAt"" ON ""Notifications"" (""CreatedAt"");",

        @"CREATE TABLE IF NOT EXISTS ""FieldConfigurations"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_FieldConfigurations"" PRIMARY KEY,
                ""ModuleKey"" text NOT NULL,
                ""SectionKey"" text NOT NULL,
                ""ApiField"" text NOT NULL,
                ""DisplayLabel"" text NOT NULL,
                ""IsVisible"" boolean NOT NULL DEFAULT true,
                ""IsRequired"" boolean NOT NULL DEFAULT false,
                ""IsEditable"" boolean NOT NULL DEFAULT true,
                ""IsSensitive"" boolean NOT NULL DEFAULT false,
                ""MaskingRule"" text NOT NULL DEFAULT 'None',
                ""VisibleChars"" integer NOT NULL DEFAULT 4,
                ""DisplayOrder"" integer NOT NULL DEFAULT 0,
                ""FieldType"" text NOT NULL DEFAULT 'Text',
                ""ValidationRegex"" text NULL,
                ""MinLength"" integer NULL,
                ""MaxLength"" integer NULL,
                ""LookupTypeCode"" text NULL,
                ""IsCustomField"" boolean NOT NULL DEFAULT false,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_FieldConfigurations_ModuleKey_SectionKey_ApiField""
                ON ""FieldConfigurations"" (""ModuleKey"", ""SectionKey"", ""ApiField"");

            CREATE TABLE IF NOT EXISTS ""LookupTypes"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_LookupTypes"" PRIMARY KEY,
                ""Code"" text NOT NULL,
                ""Name"" text NOT NULL,
                ""Description"" text NOT NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_LookupTypes_Code"" ON ""LookupTypes"" (""Code"");

            CREATE TABLE IF NOT EXISTS ""LookupValues"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_LookupValues"" PRIMARY KEY,
                ""LookupTypeId"" uuid NOT NULL CONSTRAINT ""FK_LookupValues_LookupTypes_LookupTypeId"" REFERENCES ""LookupTypes"" (""Id"") ON DELETE CASCADE,
                ""TypeCode"" text NOT NULL,
                ""Value"" text NOT NULL,
                ""Label"" text NOT NULL,
                ""DisplayOrder"" integer NOT NULL DEFAULT 0,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_LookupValues_LookupTypeId_Value"" ON ""LookupValues"" (""LookupTypeId"", ""Value"");
            CREATE INDEX IF NOT EXISTS ""IX_LookupValues_TypeCode"" ON ""LookupValues"" (""TypeCode"");

            CREATE TABLE IF NOT EXISTS ""CustomerCustomAttributes"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_CustomerCustomAttributes"" PRIMARY KEY,
                ""CustomerId"" uuid NOT NULL CONSTRAINT ""FK_CustomerCustomAttributes_Customers_CustomerId"" REFERENCES ""Customers"" (""Id"") ON DELETE CASCADE,
                ""FieldKey"" text NOT NULL,
                ""FieldValue"" text NOT NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_CustomerCustomAttributes_CustomerId"" ON ""CustomerCustomAttributes"" (""CustomerId"");

            CREATE TABLE IF NOT EXISTS ""CaseTypeConfigs"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_CaseTypeConfigs"" PRIMARY KEY,
                ""Code"" text NOT NULL,
                ""Name"" text NOT NULL,
                ""Prefix"" text NOT NULL,
                ""DisplayOrder"" integer NOT NULL DEFAULT 0,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CaseTypeConfigs_Code"" ON ""CaseTypeConfigs"" (""Code"");

            CREATE TABLE IF NOT EXISTS ""DepartmentSubCategories"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_DepartmentSubCategories"" PRIMARY KEY,
                ""DepartmentId"" uuid NOT NULL CONSTRAINT ""FK_DepartmentSubCategories_Departments_DepartmentId"" REFERENCES ""Departments"" (""Id"") ON DELETE CASCADE,
                ""Name"" text NOT NULL,
                ""Code"" text NOT NULL,
                ""DisplayOrder"" integer NOT NULL DEFAULT 0,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE INDEX IF NOT EXISTS ""IX_DepartmentSubCategories_DepartmentId"" ON ""DepartmentSubCategories"" (""DepartmentId"");

            CREATE TABLE IF NOT EXISTS ""SlaConfigurations"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_SlaConfigurations"" PRIMARY KEY,
                ""Severity"" text NOT NULL,
                ""InternalHours"" integer NOT NULL DEFAULT 0,
                ""ExternalHours"" integer NOT NULL DEFAULT 0,
                ""IsActive"" boolean NOT NULL DEFAULT true,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""UpdatedAt"" timestamp with time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SlaConfigurations_Severity"" ON ""SlaConfigurations"" (""Severity"");",

        @"-- Board listing: ORDER BY ""CreatedAt"" DESC, optionally filtered by department.
            CREATE INDEX IF NOT EXISTS ""IX_Cases_CreatedAt"" ON ""Cases"" (""CreatedAt"" DESC);
            CREATE INDEX IF NOT EXISTS ""IX_Cases_DepartmentId_CreatedAt"" ON ""Cases"" (""DepartmentId"", ""CreatedAt"" DESC);

            -- Sub-case hierarchy: columns were added by ALTER above, so EF never indexed them.
            CREATE INDEX IF NOT EXISTS ""IX_Cases_ParentCaseId"" ON ""Cases"" (""ParentCaseId"");
            CREATE INDEX IF NOT EXISTS ""IX_Cases_LinkedSourceCaseId"" ON ""Cases"" (""LinkedSourceCaseId"");

            -- Audit trail: global ORDER BY ""CreatedAt"" DESC with keyset/offset paging,
            -- and per-case timelines on the case detail screen.
            CREATE INDEX IF NOT EXISTS ""IX_CaseEvents_CreatedAt"" ON ""CaseEvents"" (""CreatedAt"" DESC);
            CREATE INDEX IF NOT EXISTS ""IX_CaseEvents_CaseId_CreatedAt"" ON ""CaseEvents"" (""CaseId"", ""CreatedAt"" DESC);

            -- Notification list is always scoped to one recipient, newest first.
            CREATE INDEX IF NOT EXISTS ""IX_Notifications_RecipientUserId_CreatedAt"" ON ""Notifications"" (""RecipientUserId"", ""CreatedAt"" DESC);
            -- Unread badge polls this constantly; a partial index keeps it tiny.
            CREATE INDEX IF NOT EXISTS ""IX_Notifications_Unread"" ON ""Notifications"" (""RecipientUserId"") WHERE ""IsRead"" = false;

            -- Customer lookup normalises the NRIC before comparing (REPLACE(""NRIC"", '-', '')),
            -- which the plain unique index on ""NRIC"" cannot serve. A matching expression index
            -- makes that branch an index lookup instead of a sequential scan. Expressed as raw
            -- SQL because EF Core cannot model an expression index.
            CREATE INDEX IF NOT EXISTS ""IX_Customers_NRIC_Normalized""
                ON ""Customers"" (replace(""NRIC"", '-', ''));

            -- Case List View paging and Board columns: WHERE Status = ? ORDER BY CreatedAt DESC.
            CREATE INDEX IF NOT EXISTS ""IX_Cases_Status_CreatedAt"" ON ""Cases"" (""Status"", ""CreatedAt"" DESC);
            -- Priority filter and severity usage counts.
            CREATE INDEX IF NOT EXISTS ""IX_Cases_Severity"" ON ""Cases"" (""Severity"");
            -- Duplicate-notification check performed before every notification insert.
            CREATE INDEX IF NOT EXISTS ""IX_Notifications_Dedup""
                ON ""Notifications"" (""RecipientUserId"", ""Type"", ""CaseId"", ""CreatedAt"" DESC);",

        @"CREATE TABLE IF NOT EXISTS ""CaseCollaborationActivities"" (
                ""Id"" uuid NOT NULL CONSTRAINT ""PK_CaseCollaborationActivities"" PRIMARY KEY,
                ""CaseId"" uuid NOT NULL CONSTRAINT ""FK_CaseCollaborationActivities_Cases_CaseId"" REFERENCES ""Cases"" (""Id"") ON DELETE CASCADE,
                ""ActivityType"" text NOT NULL,
                ""ActorUserId"" uuid NOT NULL CONSTRAINT ""FK_CaseCollaborationActivities_Users_ActorUserId"" REFERENCES ""Users"" (""Id"") ON DELETE RESTRICT,
                ""TargetUserId"" uuid NULL CONSTRAINT ""FK_CaseCollaborationActivities_Users_TargetUserId"" REFERENCES ""Users"" (""Id"") ON DELETE RESTRICT,
                ""Content"" text NULL,
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS ""IX_CaseCollaborationActivities_CaseId_CreatedAt""
                ON ""CaseCollaborationActivities"" (""CaseId"", ""CreatedAt"" DESC);
            CREATE INDEX IF NOT EXISTS ""IX_CaseCollaborationActivities_ActorUserId"" ON ""CaseCollaborationActivities"" (""ActorUserId"");
            CREATE INDEX IF NOT EXISTS ""IX_CaseCollaborationActivities_TargetUserId"" ON ""CaseCollaborationActivities"" (""TargetUserId"");",

        // Table introduced by the Baseline migration (seed bookkeeping); legacy databases lack it.
        @"CREATE TABLE IF NOT EXISTS ""SeedHistory"" (
            ""Key"" character varying(200) NOT NULL CONSTRAINT ""PK_SeedHistory"" PRIMARY KEY,
            ""AppliedAt"" timestamp with time zone NOT NULL
        )",

        // Rows written with an uninitialised (DateTime.MinValue) CreatedAt by very old seeders.
        @"UPDATE ""Users"" SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" < TIMESTAMPTZ '0002-01-01'",
        @"UPDATE ""Departments"" SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" < TIMESTAMPTZ '0002-01-01'",
        @"UPDATE ""Customers"" SET ""CreatedAt"" = NOW() WHERE ""CreatedAt"" < TIMESTAMPTZ '0002-01-01'"
    };

    /// <summary>
    /// Brings structures that the old bootstrap created WITHOUT everything the EF model declares
    /// (foreign-key indexes and one foreign key) to the Baseline shape. Idempotent and additive.
    /// Run after <see cref="Statements"/>. Column nullability is converged separately, from the EF
    /// model, by <see cref="LegacySchemaAdopter"/>.
    /// </summary>
    public static readonly string[] ConvergenceStatements =
    {
        @"CREATE INDEX IF NOT EXISTS ""IX_AssignmentConfigurations_DepartmentId"" ON ""AssignmentConfigurations"" (""DepartmentId"")",
        @"CREATE INDEX IF NOT EXISTS ""IX_EscalationLevelConfigs_TargetUserId"" ON ""EscalationLevelConfigs"" (""TargetUserId"")",
        @"CREATE INDEX IF NOT EXISTS ""IX_PriorityCategoryMappings_DepartmentSubCategoryId"" ON ""PriorityCategoryMappings"" (""DepartmentSubCategoryId"")",
        @"CREATE INDEX IF NOT EXISTS ""IX_RoutingRules_TargetDepartmentId"" ON ""RoutingRules"" (""TargetDepartmentId"")",
        @"CREATE INDEX IF NOT EXISTS ""IX_TeamAssignmentPointers_LastAssignedUserId"" ON ""TeamAssignmentPointers"" (""LastAssignedUserId"")",
        @"CREATE INDEX IF NOT EXISTS ""IX_TeamMembers_UserId"" ON ""TeamMembers"" (""UserId"")",

        // Existing orphan ids would make the FK impossible to add, so clear them first (the column
        // is nullable and was never populated by application code).
        @"UPDATE ""PriorityCategoryMappings"" SET ""DepartmentSubCategoryId"" = NULL
            WHERE ""DepartmentSubCategoryId"" IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM ""DepartmentSubCategories"" s WHERE s.""Id"" = ""PriorityCategoryMappings"".""DepartmentSubCategoryId"")",
        @"DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PriorityCategoryMappings_DepartmentSubCategories_Department~') THEN
                ALTER TABLE ""PriorityCategoryMappings""
                    ADD CONSTRAINT ""FK_PriorityCategoryMappings_DepartmentSubCategories_Department~""
                    FOREIGN KEY (""DepartmentSubCategoryId"") REFERENCES ""DepartmentSubCategories"" (""Id"") ON DELETE SET NULL;
            END IF;
        END $$",

        // Notifications.RecipientUserId FK; skipped (not forced) if orphan rows exist, so no
        // notification is ever deleted to make the constraint fit.
        @"DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Notifications_Users_RecipientUserId')
               AND NOT EXISTS (SELECT 1 FROM ""Notifications"" n WHERE n.""RecipientUserId"" IS NOT NULL
                               AND NOT EXISTS (SELECT 1 FROM ""Users"" u WHERE u.""Id"" = n.""RecipientUserId"")) THEN
                ALTER TABLE ""Notifications""
                    ADD CONSTRAINT ""FK_Notifications_Users_RecipientUserId""
                    FOREIGN KEY (""RecipientUserId"") REFERENCES ""Users"" (""Id"") ON DELETE CASCADE;
            END IF;
        END $$"
    };
}
