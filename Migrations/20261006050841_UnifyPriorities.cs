using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class UnifyPriorities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- Priorities become the single master (PrioritySlaRules) -----------------------------------
            // The legacy "severity" master (CASE_SEVERITY lookup + SlaConfigurations) is folded INTO the rules
            // table before it is retired, so no configured priority or SLA number is lost.

            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                table: "PrioritySlaRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing rows stay ACTIVE (EF's generated default would deactivate every priority).
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "PrioritySlaRules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Severities that exist only in the legacy master (e.g. an administrator-added 'Urgent') become rules,
            // carrying their hours/first-response over. Skipped entirely if the legacy table is absent.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF to_regclass('public.""SlaConfigurations""') IS NOT NULL THEN
                        INSERT INTO ""PrioritySlaRules""
                            (""Id"", ""Priority"", ""DisplayOrder"", ""IsActive"",
                             ""FirstResponseValue"", ""FirstResponseUnit"", ""FirstResponseMinutes"",
                             ""InternalResolutionValue"", ""InternalResolutionUnit"", ""InternalResolutionMinutes"",
                             ""ExternalResolutionValue"", ""ExternalResolutionUnit"", ""ExternalResolutionMinutes"",
                             ""Version"", ""CreatedAt"")
                        SELECT gen_random_uuid(), sc.""Severity"",
                               (SELECT COALESCE(MAX(""DisplayOrder""), 0) FROM ""PrioritySlaRules"") + ROW_NUMBER() OVER (ORDER BY sc.""Severity""),
                               sc.""IsActive"",
                               GREATEST(sc.""FirstResponseMinutes"", 1), 'Minutes', GREATEST(sc.""FirstResponseMinutes"", 1),
                               GREATEST(sc.""InternalHours"", 1), 'Hours', GREATEST(sc.""InternalHours"", 1) * 60,
                               GREATEST(sc.""ExternalHours"", 1), 'Hours', GREATEST(sc.""ExternalHours"", 1) * 60,
                               1, NOW()
                        FROM ""SlaConfigurations"" sc
                        WHERE NOT EXISTS (SELECT 1 FROM ""PrioritySlaRules"" r WHERE lower(r.""Priority"") = lower(sc.""Severity""));
                    END IF;
                END $$;
            ");

            // Display order for ALL rules (existing and just-copied): the conventional urgency order for the four
            // well-known names, then anything else alphabetically after them.
            migrationBuilder.Sql(@"
                UPDATE ""PrioritySlaRules"" r SET ""DisplayOrder"" = o.rn
                FROM (
                    SELECT ""Id"", ROW_NUMBER() OVER (
                        ORDER BY CASE lower(""Priority"") WHEN 'critical' THEN 1 WHEN 'high' THEN 2 WHEN 'medium' THEN 3 WHEN 'low' THEN 4 ELSE 5 END,
                                 ""Priority"") AS rn
                    FROM ""PrioritySlaRules""
                ) o
                WHERE r.""Id"" = o.""Id"";
            ");

            // ---- Priority mappings: keyed by sub-category ID instead of by name ---------------------------
            migrationBuilder.Sql(@"ALTER TABLE ""PriorityCategoryMappings"" DROP CONSTRAINT IF EXISTS ""FK_PriorityCategoryMappings_DepartmentSubCategories_Department~"";");

            // The name is about to be copied onto several rows, so its (old) unique index must go first.
            migrationBuilder.DropIndex(
                name: "IX_PriorityCategoryMappings_CategoryName",
                table: "PriorityCategoryMappings");

            // A mapping that only knew a NAME applied to every sub-category with that name; keep exactly that
            // meaning by expanding it to one row per matching sub-category.
            migrationBuilder.Sql(@"
                INSERT INTO ""PriorityCategoryMappings"" (""Id"", ""PrioritySlaRuleId"", ""Priority"", ""CategoryName"", ""DepartmentSubCategoryId"", ""CreatedAt"")
                SELECT DISTINCT ON (s.""Id"") gen_random_uuid(), m.""PrioritySlaRuleId"", m.""Priority"", m.""CategoryName"", s.""Id"", NOW()
                FROM ""PriorityCategoryMappings"" m
                JOIN ""DepartmentSubCategories"" s ON lower(s.""Name"") = lower(m.""CategoryName"")
                WHERE m.""DepartmentSubCategoryId"" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM ""PriorityCategoryMappings"" x WHERE x.""DepartmentSubCategoryId"" = s.""Id"")
                ORDER BY s.""Id"", m.""CreatedAt"";
            ");
            // Mappings pointing at nothing can no longer be applied to any case; one priority per sub-category.
            migrationBuilder.Sql(@"DELETE FROM ""PriorityCategoryMappings"" WHERE ""DepartmentSubCategoryId"" IS NULL;");
            migrationBuilder.Sql(@"
                DELETE FROM ""PriorityCategoryMappings"" a USING ""PriorityCategoryMappings"" b
                WHERE a.""DepartmentSubCategoryId"" = b.""DepartmentSubCategoryId"" AND a.""Id"" > b.""Id"";
            ");

            migrationBuilder.DropIndex(
                name: "IX_PriorityCategoryMappings_DepartmentSubCategoryId",
                table: "PriorityCategoryMappings");

            migrationBuilder.DropColumn(
                name: "CategoryName",
                table: "PriorityCategoryMappings");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "PriorityCategoryMappings");

            migrationBuilder.AlterColumn<Guid>(
                name: "DepartmentSubCategoryId",
                table: "PriorityCategoryMappings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriorityCategoryMappings_DepartmentSubCategoryId",
                table: "PriorityCategoryMappings",
                column: "DepartmentSubCategoryId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PriorityCategoryMappings_DepartmentSubCategories_Department~",
                table: "PriorityCategoryMappings",
                column: "DepartmentSubCategoryId",
                principalTable: "DepartmentSubCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // ---- Retire the legacy severity master (its content now lives in PrioritySlaRules) -------------
            migrationBuilder.DropTable(
                name: "SlaConfigurations");

            migrationBuilder.Sql(@"DELETE FROM ""LookupValues"" WHERE ""TypeCode"" = 'CASE_SEVERITY';");
            migrationBuilder.Sql(@"DELETE FROM ""LookupTypes"" WHERE ""Code"" = 'CASE_SEVERITY';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Folding the legacy severity master into PrioritySlaRules and re-keying priority mappings by
            // sub-category id rewrites and drops data, so it cannot be reversed faithfully. To go back,
            // restore a backup taken before this migration.
            throw new NotSupportedException(
                "The UnifyPriorities migration is forward-only. Restore a database backup taken before it to roll back.");
        }
    }
}
