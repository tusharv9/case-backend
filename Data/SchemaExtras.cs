namespace CaseManagement.Api.Data;

/// <summary>
/// Database objects that EF Core's model cannot express (a sequence, a PostgreSQL extension,
/// trigram/expression indexes). They used to be created by ad-hoc DDL on every application start;
/// they now live in exactly one place and are applied by the Baseline migration (fresh databases)
/// and by <see cref="LegacySchemaAdopter"/> (databases created by the old startup bootstrap).
///
/// Every statement is idempotent, so applying it to a database that already has the objects is safe.
/// </summary>
public static class SchemaExtras
{
    public const string UpSql = $@"
-- Atomic allocation of the numeric part of Cases.CaseNumber.
CREATE SEQUENCE IF NOT EXISTS {SchemaConstants.CaseNumberSequence};

-- Notification polling / de-duplication indexes (beyond the ones declared on the model).
CREATE INDEX IF NOT EXISTS ""IX_Notifications_CreatedAt"" ON ""Notifications"" (""CreatedAt"");
CREATE INDEX IF NOT EXISTS ""IX_Notifications_IsRead"" ON ""Notifications"" (""IsRead"");
CREATE INDEX IF NOT EXISTS ""IX_Notifications_RecipientUserId"" ON ""Notifications"" (""RecipientUserId"");
CREATE INDEX IF NOT EXISTS ""IX_Notifications_Dedup""
    ON ""Notifications"" (""RecipientUserId"", ""Type"", ""CaseId"", ""CreatedAt"" DESC);

-- Customer lookup normalises the NRIC before comparing (REPLACE(""NRIC"", '-', '')); a matching
-- expression index turns that branch into an index lookup. EF cannot model expression indexes.
CREATE INDEX IF NOT EXISTS ""IX_Customers_NRIC_Normalized"" ON ""Customers"" (replace(""NRIC"", '-', ''));

-- Trigram indexes make ILIKE '%term%' searches index scans. Isolated so a database role without
-- permission to create the extension still gets a working (just unindexed-search) schema.
DO $$
BEGIN
    BEGIN
        CREATE EXTENSION IF NOT EXISTS pg_trgm;
    EXCEPTION WHEN OTHERS THEN
        RAISE NOTICE 'pg_trgm could not be created (%): free-text search will not use trigram indexes.', SQLERRM;
    END;

    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
        CREATE INDEX IF NOT EXISTS ""IX_Cases_CaseNumber_Trgm"" ON ""Cases"" USING gin (""CaseNumber"" gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ""IX_Cases_Title_Trgm"" ON ""Cases"" USING gin (""Title"" gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ""IX_Customers_FullName_Trgm"" ON ""Customers"" USING gin (""FullName"" gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ""IX_Users_Name_Trgm"" ON ""Users"" USING gin (""Name"" gin_trgm_ops);
    END IF;
END $$;
";

    /// <summary>
    /// Moves the case-number sequence forward to the highest number already present in the data,
    /// never backwards. Used when adopting an existing database so the next case number cannot
    /// collide with an existing one.
    /// </summary>
    public const string SyncCaseNumberSequenceSql = $@"
DO $$
DECLARE
    data_max    bigint;
    current_val bigint;
    target      bigint;
BEGIN
    SELECT COALESCE(MAX(split_part(""CaseNumber"", '-', 2)::bigint), 0)
      INTO data_max
      FROM ""Cases""
     WHERE split_part(""CaseNumber"", '-', 2) ~ '^[0-9]+$';

    SELECT CASE WHEN is_called THEN last_value ELSE 0 END
      INTO current_val
      FROM {SchemaConstants.CaseNumberSequence};

    target := GREATEST(data_max, current_val);
    PERFORM setval('{SchemaConstants.CaseNumberSequence}', GREATEST(target, 1), target > 0);
END $$;
";

    public const string DownSql = $@"
DROP INDEX IF EXISTS ""IX_Users_Name_Trgm"";
DROP INDEX IF EXISTS ""IX_Customers_FullName_Trgm"";
DROP INDEX IF EXISTS ""IX_Cases_Title_Trgm"";
DROP INDEX IF EXISTS ""IX_Cases_CaseNumber_Trgm"";
DROP INDEX IF EXISTS ""IX_Customers_NRIC_Normalized"";
DROP INDEX IF EXISTS ""IX_Notifications_Dedup"";
DROP INDEX IF EXISTS ""IX_Notifications_RecipientUserId"";
DROP INDEX IF EXISTS ""IX_Notifications_IsRead"";
DROP INDEX IF EXISTS ""IX_Notifications_CreatedAt"";
DROP SEQUENCE IF EXISTS {SchemaConstants.CaseNumberSequence};
";
}
