using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <summary>
    /// Teams and departments become two independent tables joined only by a mapping, replacing the one-department-one-team
    /// layout in which the TEAM table was called "Departments" (every team once WAS its department) and the departments were
    /// "OrgDepartments":
    ///   "Departments" → "Teams";  "OrgDepartments" → "Departments";  every team foreign key "…DepartmentId" → "…TeamId";
    ///   a case's / sub-category's / mapping's "OrgDepartmentId" → "DepartmentId".
    /// The mapping "TeamDepartments" becomes one team → many departments, one department → one team (unique index), and the
    /// "default team" flag goes: there is no default or fallback team any more. Retired with it: Users."DepartmentId" (a
    /// legacy home team nothing read; teams come from membership) and DepartmentSubCategories' legacy team column.
    ///
    /// Data: only renames and the clean-up below. A department that several teams served keeps the team that was its default
    /// (else the earliest linked); every link removed that way is written to the audit trail. Cases, members, history and audit
    /// rows are untouched. Constraint and index names differ between databases created by migrations and those adopted from
    /// the pre-migration schema, so foreign keys are found by what they reference, not by name, and recreated with the model's
    /// names.
    /// </summary>
    public partial class IndependentTeamsAndDepartments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Data, while the old names still hold.

            // 1a. One team per department: keep the default link (else the earliest); audit every link removed.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT td."TeamId", td."OrgDepartmentId",
                           row_number() OVER (PARTITION BY td."OrgDepartmentId" ORDER BY td."IsDefault" DESC, td."LinkedAt", td."TeamId") AS rn
                    FROM "TeamDepartments" td
                ), removed AS (
                    DELETE FROM "TeamDepartments" td USING ranked r
                    WHERE r.rn > 1 AND td."TeamId" = r."TeamId" AND td."OrgDepartmentId" = r."OrgDepartmentId"
                    RETURNING td."TeamId", td."OrgDepartmentId"
                )
                INSERT INTO "CaseEvents" ("Id", "EventType", "Message", "CreatedAt", "Module", "EntityName", "ActionType", "OldValue", "NewValue", "IsInternal")
                SELECT gen_random_uuid(), 'Other',
                       format('Department ''%s'' can belong to only one team: removed it from ''%s'' (it stays with ''%s'')', od."Name", t."Name", kept."Name"),
                       now(), 'Departments', od."Name", 'UPDATE', t."Name", kept."Name", false
                FROM removed x
                JOIN "OrgDepartments" od ON od."Id" = x."OrgDepartmentId"
                JOIN "Departments" t ON t."Id" = x."TeamId"
                JOIN ranked k ON k."OrgDepartmentId" = x."OrgDepartmentId" AND k.rn = 1
                JOIN "Departments" kept ON kept."Id" = k."TeamId";
                """);

            // 1b. A sub-category still filed only under its old team takes that team's department, so dropping the legacy team
            //     column loses nothing.
            migrationBuilder.Sql("""
                UPDATE "DepartmentSubCategories" s SET "OrgDepartmentId" = td."OrgDepartmentId"
                FROM "TeamDepartments" td
                WHERE s."OrgDepartmentId" IS NULL AND s."DepartmentId" = td."TeamId";
                """);

            // ---- 2. Foreign keys that point at either table, found by target (names vary by database), and the indexes on the
            //         columns that are renamed or dropped. All are recreated below with the model's names.
            migrationBuilder.Sql("""
                DO $$
                DECLARE r record;
                BEGIN
                  FOR r IN SELECT conrelid::regclass AS tbl, conname FROM pg_constraint
                           WHERE contype = 'f' AND (confrelid IN ('"Departments"'::regclass, '"OrgDepartments"'::regclass)
                                                 OR conrelid IN ('"Departments"'::regclass, '"OrgDepartments"'::regclass)) LOOP
                    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.tbl, r.conname);
                  END LOOP;
                END $$;
                DROP INDEX IF EXISTS "IX_AssignmentConfigurations_DepartmentId";
                DROP INDEX IF EXISTS "IX_Cases_DepartmentId_CreatedAt";
                DROP INDEX IF EXISTS "IX_Cases_Open_Department";
                DROP INDEX IF EXISTS "IX_Cases_OrgDepartmentId";
                DROP INDEX IF EXISTS "IX_DepartmentSubCategories_DepartmentId";
                DROP INDEX IF EXISTS "IX_DepartmentSubCategories_OrgDepartmentId";
                DROP INDEX IF EXISTS "IX_Departments_OwnerId";
                DROP INDEX IF EXISTS "IX_Departments_Code";
                DROP INDEX IF EXISTS "IX_Departments_Name";
                DROP INDEX IF EXISTS "IX_OrgDepartments_Code";
                DROP INDEX IF EXISTS "IX_OrgDepartments_Name";
                DROP INDEX IF EXISTS "IX_ManualAssignmentCrossTeams_DepartmentId";
                DROP INDEX IF EXISTS "IX_ManualAssignmentCrossTeams_SettingsId_DepartmentId";
                DROP INDEX IF EXISTS "IX_RoutingRules_TargetDepartmentId";
                DROP INDEX IF EXISTS "IX_TeamDepartments_OneDefaultPerDepartment";
                DROP INDEX IF EXISTS "IX_TeamDepartments_OrgDepartmentId";
                DROP INDEX IF EXISTS "IX_TeamMembers_DepartmentId_UserId";
                DROP INDEX IF EXISTS "IX_Users_DepartmentId";
                """);

            // ---- 3. Retired columns.
            migrationBuilder.Sql("""
                ALTER TABLE "TeamDepartments" DROP COLUMN "IsDefault";
                ALTER TABLE "DepartmentSubCategories" DROP COLUMN "DepartmentId";
                ALTER TABLE "Users" DROP COLUMN IF EXISTS "DepartmentId";
                """);

            // ---- 4. Renames: tables first, then columns (a case's team column before its department column, which takes its name).
            migrationBuilder.Sql("""
                ALTER TABLE "Departments" RENAME TO "Teams";
                ALTER TABLE "OrgDepartments" RENAME TO "Departments";
                DO $$
                BEGIN
                  IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '"Teams"'::regclass AND conname = 'PK_Departments') THEN
                    ALTER TABLE "Teams" RENAME CONSTRAINT "PK_Departments" TO "PK_Teams";
                  END IF;
                  IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = '"Departments"'::regclass AND conname = 'PK_OrgDepartments') THEN
                    ALTER TABLE "Departments" RENAME CONSTRAINT "PK_OrgDepartments" TO "PK_Departments";
                  END IF;
                END $$;

                ALTER TABLE "Cases" RENAME COLUMN "DepartmentId" TO "TeamId";
                ALTER TABLE "Cases" RENAME COLUMN "OrgDepartmentId" TO "DepartmentId";
                ALTER TABLE "DepartmentSubCategories" RENAME COLUMN "OrgDepartmentId" TO "DepartmentId";
                ALTER TABLE "TeamDepartments" RENAME COLUMN "OrgDepartmentId" TO "DepartmentId";
                ALTER TABLE "TeamMembers" RENAME COLUMN "DepartmentId" TO "TeamId";
                ALTER TABLE "TeamAssignmentPointers" RENAME COLUMN "DepartmentId" TO "TeamId";
                ALTER TABLE "RoutingRules" RENAME COLUMN "TargetDepartmentId" TO "TargetTeamId";
                ALTER TABLE "ManualAssignmentCrossTeams" RENAME COLUMN "DepartmentId" TO "TeamId";
                ALTER TABLE "AssignmentConfigurations" RENAME COLUMN "DepartmentId" TO "TeamId";
                """);

            // ---- 5. Indexes, with the model's names. A department belongs to at most one team (UX_TeamDepartments_Department).
            migrationBuilder.CreateIndex(name: "UX_TeamDepartments_Department", table: "TeamDepartments", column: "DepartmentId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Teams_Code", table: "Teams", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Teams_Name", table: "Teams", column: "Name", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Teams_OwnerId", table: "Teams", column: "OwnerId");
            migrationBuilder.CreateIndex(name: "IX_Departments_Code", table: "Departments", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Departments_Name", table: "Departments", column: "Name", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Cases_DepartmentId", table: "Cases", column: "DepartmentId");
            migrationBuilder.CreateIndex(name: "IX_Cases_Open_Team", table: "Cases", columns: new[] { "TeamId", "Status" },
                filter: "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')");
            migrationBuilder.CreateIndex(name: "IX_Cases_TeamId_CreatedAt", table: "Cases", columns: new[] { "TeamId", "CreatedAt" },
                descending: new[] { false, true });
            migrationBuilder.CreateIndex(name: "IX_DepartmentSubCategories_DepartmentId", table: "DepartmentSubCategories", column: "DepartmentId");
            migrationBuilder.CreateIndex(name: "IX_AssignmentConfigurations_TeamId", table: "AssignmentConfigurations", column: "TeamId",
                unique: true, filter: "\"TeamId\" IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_ManualAssignmentCrossTeams_TeamId", table: "ManualAssignmentCrossTeams", column: "TeamId");
            migrationBuilder.CreateIndex(name: "IX_ManualAssignmentCrossTeams_SettingsId_TeamId", table: "ManualAssignmentCrossTeams",
                columns: new[] { "SettingsId", "TeamId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_RoutingRules_TargetTeamId", table: "RoutingRules", column: "TargetTeamId");
            migrationBuilder.CreateIndex(name: "IX_TeamMembers_TeamId_UserId", table: "TeamMembers", columns: new[] { "TeamId", "UserId" }, unique: true);

            // ---- 6a. The case-list filter that was configured as "departmentId" ("Handling Department") filters by TEAM: it is
            //          renamed to what it is. A label an administrator changed is kept.
            migrationBuilder.Sql("""
                UPDATE "FieldConfigurations"
                SET "ApiField" = 'teamId',
                    "DisplayLabel" = CASE WHEN "DisplayLabel" = 'Handling Department' THEN 'Team' ELSE "DisplayLabel" END,
                    "UpdatedAt" = now()
                WHERE "ModuleKey" = 'CaseManagement' AND "SectionKey" = 'Filters' AND "ApiField" = 'departmentId';
                """);

            // ---- 6. Foreign keys, with the model's names and delete rules.
            migrationBuilder.AddForeignKey(name: "FK_Teams_Users_OwnerId", table: "Teams", column: "OwnerId",
                principalTable: "Users", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_AssignmentConfigurations_Teams_TeamId", table: "AssignmentConfigurations", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_Cases_Departments_DepartmentId", table: "Cases", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_Cases_Teams_TeamId", table: "Cases", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_CaseSwarmRequests_Teams_TeamId", table: "CaseSwarmRequests", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(name: "FK_DepartmentSubCategories_Departments_DepartmentId", table: "DepartmentSubCategories", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_ManualAssignmentCrossTeams_Teams_TeamId", table: "ManualAssignmentCrossTeams", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_RoutingRules_Teams_TargetTeamId", table: "RoutingRules", column: "TargetTeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_TeamAssignmentPointers_Teams_TeamId", table: "TeamAssignmentPointers", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_TeamCustomAttributes_Teams_TeamId", table: "TeamCustomAttributes", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_TeamDepartments_Departments_DepartmentId", table: "TeamDepartments", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_TeamDepartments_Teams_TeamId", table: "TeamDepartments", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_TeamMembers_Teams_TeamId", table: "TeamMembers", column: "TeamId",
                principalTable: "Teams", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The reverse renames. The retired columns come back empty except the default flag (every remaining link is its
            // department's only team, so it is its default) and a sub-category's old team (its department's team).
            migrationBuilder.Sql("""
                UPDATE "FieldConfigurations"
                SET "ApiField" = 'departmentId',
                    "DisplayLabel" = CASE WHEN "DisplayLabel" = 'Team' THEN 'Handling Department' ELSE "DisplayLabel" END
                WHERE "ModuleKey" = 'CaseManagement' AND "SectionKey" = 'Filters' AND "ApiField" = 'teamId';
                """);
            migrationBuilder.Sql("""
                DO $$
                DECLARE r record;
                BEGIN
                  FOR r IN SELECT conrelid::regclass AS tbl, conname FROM pg_constraint
                           WHERE contype = 'f' AND (confrelid IN ('"Teams"'::regclass, '"Departments"'::regclass)
                                                 OR conrelid IN ('"Teams"'::regclass, '"Departments"'::regclass)) LOOP
                    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.tbl, r.conname);
                  END LOOP;
                END $$;
                DROP INDEX IF EXISTS "UX_TeamDepartments_Department";
                DROP INDEX IF EXISTS "IX_Teams_Code";
                DROP INDEX IF EXISTS "IX_Teams_Name";
                DROP INDEX IF EXISTS "IX_Teams_OwnerId";
                DROP INDEX IF EXISTS "IX_Departments_Code";
                DROP INDEX IF EXISTS "IX_Departments_Name";
                DROP INDEX IF EXISTS "IX_Cases_DepartmentId";
                DROP INDEX IF EXISTS "IX_Cases_Open_Team";
                DROP INDEX IF EXISTS "IX_Cases_TeamId_CreatedAt";
                DROP INDEX IF EXISTS "IX_DepartmentSubCategories_DepartmentId";
                DROP INDEX IF EXISTS "IX_AssignmentConfigurations_TeamId";
                DROP INDEX IF EXISTS "IX_ManualAssignmentCrossTeams_TeamId";
                DROP INDEX IF EXISTS "IX_ManualAssignmentCrossTeams_SettingsId_TeamId";
                DROP INDEX IF EXISTS "IX_RoutingRules_TargetTeamId";
                DROP INDEX IF EXISTS "IX_TeamMembers_TeamId_UserId";

                ALTER TABLE "AssignmentConfigurations" RENAME COLUMN "TeamId" TO "DepartmentId";
                ALTER TABLE "ManualAssignmentCrossTeams" RENAME COLUMN "TeamId" TO "DepartmentId";
                ALTER TABLE "RoutingRules" RENAME COLUMN "TargetTeamId" TO "TargetDepartmentId";
                ALTER TABLE "TeamAssignmentPointers" RENAME COLUMN "TeamId" TO "DepartmentId";
                ALTER TABLE "TeamMembers" RENAME COLUMN "TeamId" TO "DepartmentId";
                ALTER TABLE "TeamDepartments" RENAME COLUMN "DepartmentId" TO "OrgDepartmentId";
                ALTER TABLE "DepartmentSubCategories" RENAME COLUMN "DepartmentId" TO "OrgDepartmentId";
                ALTER TABLE "Cases" RENAME COLUMN "DepartmentId" TO "OrgDepartmentId";
                ALTER TABLE "Cases" RENAME COLUMN "TeamId" TO "DepartmentId";

                ALTER TABLE "Departments" RENAME CONSTRAINT "PK_Departments" TO "PK_OrgDepartments";
                ALTER TABLE "Teams" RENAME CONSTRAINT "PK_Teams" TO "PK_Departments";
                ALTER TABLE "Departments" RENAME TO "OrgDepartments";
                ALTER TABLE "Teams" RENAME TO "Departments";

                ALTER TABLE "TeamDepartments" ADD COLUMN "IsDefault" boolean NOT NULL DEFAULT false;
                UPDATE "TeamDepartments" SET "IsDefault" = true;
                ALTER TABLE "DepartmentSubCategories" ADD COLUMN "DepartmentId" uuid NULL;
                UPDATE "DepartmentSubCategories" s SET "DepartmentId" = td."TeamId" FROM "TeamDepartments" td WHERE td."OrgDepartmentId" = s."OrgDepartmentId";
                ALTER TABLE "Users" ADD COLUMN "DepartmentId" uuid NULL;
                """);

            migrationBuilder.CreateIndex(name: "IX_TeamDepartments_OrgDepartmentId", table: "TeamDepartments", column: "OrgDepartmentId");
            migrationBuilder.CreateIndex(name: "IX_TeamDepartments_OneDefaultPerDepartment", table: "TeamDepartments", column: "OrgDepartmentId",
                unique: true, filter: "\"IsDefault\"");
            migrationBuilder.CreateIndex(name: "IX_Departments_Code", table: "Departments", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Departments_Name", table: "Departments", column: "Name", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Departments_OwnerId", table: "Departments", column: "OwnerId");
            migrationBuilder.CreateIndex(name: "IX_OrgDepartments_Code", table: "OrgDepartments", column: "Code", unique: true);
            migrationBuilder.CreateIndex(name: "IX_OrgDepartments_Name", table: "OrgDepartments", column: "Name", unique: true);
            migrationBuilder.CreateIndex(name: "IX_Cases_OrgDepartmentId", table: "Cases", column: "OrgDepartmentId");
            migrationBuilder.CreateIndex(name: "IX_Cases_Open_Department", table: "Cases", columns: new[] { "DepartmentId", "Status" },
                filter: "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')");
            migrationBuilder.CreateIndex(name: "IX_Cases_DepartmentId_CreatedAt", table: "Cases", columns: new[] { "DepartmentId", "CreatedAt" },
                descending: new[] { false, true });
            migrationBuilder.CreateIndex(name: "IX_DepartmentSubCategories_DepartmentId", table: "DepartmentSubCategories", column: "DepartmentId");
            migrationBuilder.CreateIndex(name: "IX_DepartmentSubCategories_OrgDepartmentId", table: "DepartmentSubCategories", column: "OrgDepartmentId");
            migrationBuilder.CreateIndex(name: "IX_AssignmentConfigurations_DepartmentId", table: "AssignmentConfigurations", column: "DepartmentId",
                unique: true, filter: "\"DepartmentId\" IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_ManualAssignmentCrossTeams_DepartmentId", table: "ManualAssignmentCrossTeams", column: "DepartmentId");
            migrationBuilder.CreateIndex(name: "IX_ManualAssignmentCrossTeams_SettingsId_DepartmentId", table: "ManualAssignmentCrossTeams",
                columns: new[] { "SettingsId", "DepartmentId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_RoutingRules_TargetDepartmentId", table: "RoutingRules", column: "TargetDepartmentId");
            migrationBuilder.CreateIndex(name: "IX_TeamMembers_DepartmentId_UserId", table: "TeamMembers", columns: new[] { "DepartmentId", "UserId" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_Users_DepartmentId", table: "Users", column: "DepartmentId");

            migrationBuilder.AddForeignKey(name: "FK_Departments_Users_OwnerId", table: "Departments", column: "OwnerId",
                principalTable: "Users", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_AssignmentConfigurations_Departments_DepartmentId", table: "AssignmentConfigurations", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_Cases_Departments_DepartmentId", table: "Cases", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_Cases_OrgDepartments_OrgDepartmentId", table: "Cases", column: "OrgDepartmentId",
                principalTable: "OrgDepartments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_CaseSwarmRequests_Departments_TeamId", table: "CaseSwarmRequests", column: "TeamId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(name: "FK_DepartmentSubCategories_Departments_DepartmentId", table: "DepartmentSubCategories", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
            migrationBuilder.AddForeignKey(name: "FK_DepartmentSubCategories_OrgDepartments_OrgDepartmentId", table: "DepartmentSubCategories", column: "OrgDepartmentId",
                principalTable: "OrgDepartments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_ManualAssignmentCrossTeams_Departments_DepartmentId", table: "ManualAssignmentCrossTeams", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_RoutingRules_Departments_TargetDepartmentId", table: "RoutingRules", column: "TargetDepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_TeamAssignmentPointers_Departments_DepartmentId", table: "TeamAssignmentPointers", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_TeamCustomAttributes_Departments_TeamId", table: "TeamCustomAttributes", column: "TeamId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_TeamDepartments_Departments_TeamId", table: "TeamDepartments", column: "TeamId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_TeamDepartments_OrgDepartments_OrgDepartmentId", table: "TeamDepartments", column: "OrgDepartmentId",
                principalTable: "OrgDepartments", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_TeamMembers_Departments_DepartmentId", table: "TeamMembers", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(name: "FK_Users_Departments_DepartmentId", table: "Users", column: "DepartmentId",
                principalTable: "Departments", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
        }
    }
}
