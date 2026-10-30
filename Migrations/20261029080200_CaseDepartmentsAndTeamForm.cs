using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class CaseDepartmentsAndTeamForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DepartmentSubCategories_Departments_DepartmentId",
                table: "DepartmentSubCategories");

            migrationBuilder.DropIndex(
                name: "IX_CaseEvents_UserId",
                table: "CaseEvents");

            migrationBuilder.AddColumn<bool>(
                name: "AllowDeactivateWithOpenCases",
                table: "TeamSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowLeadInOtherTeams",
                table: "TeamSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowTeamsWithoutMembers",
                table: "TeamSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireTeamLead",
                table: "TeamSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "TeamDepartments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "DepartmentId",
                table: "DepartmentSubCategories",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "OrgDepartmentId",
                table: "DepartmentSubCategories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrgDepartmentId",
                table: "Cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TeamCustomAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FieldValue = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamCustomAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamCustomAttributes_Departments_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeamDepartments_OneDefaultPerDepartment",
                table: "TeamDepartments",
                column: "OrgDepartmentId",
                unique: true,
                filter: "\"IsDefault\"");

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentSubCategories_OrgDepartmentId",
                table: "DepartmentSubCategories",
                column: "OrgDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_OrgDepartmentId",
                table: "Cases",
                column: "OrgDepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_UserId_CreatedAt",
                table: "CaseEvents",
                columns: new[] { "UserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_TeamCustomAttributes_TeamId_FieldKey",
                table: "TeamCustomAttributes",
                columns: new[] { "TeamId", "FieldKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Cases_OrgDepartments_OrgDepartmentId",
                table: "Cases",
                column: "OrgDepartmentId",
                principalTable: "OrgDepartments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DepartmentSubCategories_Departments_DepartmentId",
                table: "DepartmentSubCategories",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_DepartmentSubCategories_OrgDepartments_OrgDepartmentId",
                table: "DepartmentSubCategories",
                column: "OrgDepartmentId",
                principalTable: "OrgDepartments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ---- Data. Additive only: nothing is deleted, and the old team links stay where they were. ----

            // 1. Each department's earliest-linked team is its default team (where its cases go when no routing rule matches).
            //    The seed linked every team to one department of the same name, so this keeps today's routing.
            migrationBuilder.Sql("""
                UPDATE "TeamDepartments" td SET "IsDefault" = true
                FROM (SELECT DISTINCT ON ("OrgDepartmentId") "OrgDepartmentId", "TeamId"
                      FROM "TeamDepartments" ORDER BY "OrgDepartmentId", "LinkedAt", "TeamId") first_link
                WHERE td."OrgDepartmentId" = first_link."OrgDepartmentId" AND td."TeamId" = first_link."TeamId";
                """);

            // 2. A case's department and a sub-category's department: the department its team serves (default link first).
            migrationBuilder.Sql("""
                UPDATE "Cases" c SET "OrgDepartmentId" = (
                    SELECT td."OrgDepartmentId" FROM "TeamDepartments" td WHERE td."TeamId" = c."DepartmentId"
                    ORDER BY td."IsDefault" DESC, td."LinkedAt", td."OrgDepartmentId" LIMIT 1)
                WHERE c."OrgDepartmentId" IS NULL;
                UPDATE "DepartmentSubCategories" s SET "OrgDepartmentId" = (
                    SELECT td."OrgDepartmentId" FROM "TeamDepartments" td WHERE td."TeamId" = s."DepartmentId"
                    ORDER BY td."IsDefault" DESC, td."LinkedAt", td."OrgDepartmentId" LIMIT 1)
                WHERE s."OrgDepartmentId" IS NULL;
                """);

            // 3. A routing rule's "Department" condition named the TEAM a case was opened for; it now names the case's business
            //    department. Where the value is a team's name or code, it becomes that team's department name (same text for the
            //    seeded one-to-one links). Unreadable rule JSON is left exactly as it is.
            migrationBuilder.Sql("""
                DO $$
                DECLARE r record; conds jsonb; k text; v text; dept text;
                BEGIN
                  FOR r IN SELECT "Id", "ConditionsJson" FROM "RoutingRules" LOOP
                    BEGIN
                      conds := r."ConditionsJson"::jsonb;
                    EXCEPTION WHEN others THEN CONTINUE;
                    END;
                    FOREACH k IN ARRAY ARRAY['Department', 'department'] LOOP
                      v := conds ->> k;
                      CONTINUE WHEN v IS NULL OR btrim(v) = '';
                      SELECT od."Name" INTO dept
                      FROM "Departments" t
                      JOIN "TeamDepartments" td ON td."TeamId" = t."Id"
                      JOIN "OrgDepartments" od ON od."Id" = td."OrgDepartmentId"
                      WHERE lower(t."Name") = lower(btrim(v)) OR lower(t."Code") = lower(btrim(v))
                      ORDER BY td."IsDefault" DESC, td."LinkedAt" LIMIT 1;
                      IF dept IS NOT NULL AND dept <> v THEN
                        UPDATE "RoutingRules" SET "ConditionsJson" = jsonb_set(conds, ARRAY[k], to_jsonb(dept))::text, "UpdatedAt" = now()
                        WHERE "Id" = r."Id";
                      END IF;
                    END LOOP;
                  END LOOP;
                END $$;
                """);

            // 4. The Create/Edit Team form becomes FieldConfiguration rows (module CaseManagement, section CreateTeam), carrying
            //    over what the first version stored in TeamSettings.FormFields. Team name is system-required.
            migrationBuilder.Sql("""
                WITH rules AS (
                    SELECT r ->> 'Key' AS key, COALESCE((r ->> 'IsVisible')::boolean, true) AS visible, COALESCE((r ->> 'IsRequired')::boolean, false) AS required
                    FROM "TeamSettings" ts, jsonb_array_elements(COALESCE(ts."FormFields", '[]'::jsonb)) r
                ), fields(api, label, type, ord, sysreq) AS (VALUES
                    ('name', 'Team name', 'Text', 1, true),
                    ('code', 'Team code', 'Text', 2, false),
                    ('description', 'Description', 'Text', 3, false),
                    ('departments', 'Departments', 'MultiSelect', 4, false),
                    ('teamLead', 'Team lead', 'Dropdown', 5, false),
                    ('members', 'Members', 'MultiSelect', 6, false))
                INSERT INTO "FieldConfigurations" ("Id", "ModuleKey", "SectionKey", "ApiField", "DisplayLabel", "IsVisible", "IsRequired",
                    "IsSystemRequired", "MaskingRule", "VisibleChars", "DisplayOrder", "FieldType", "IsCustomField", "CreatedAt")
                SELECT gen_random_uuid(), 'CaseManagement', 'CreateTeam', f.api, f.label,
                    f.sysreq OR COALESCE(r.visible, true),
                    f.sysreq OR (COALESCE(r.visible, true) AND COALESCE(r.required, false)),
                    f.sysreq, 'None', 4, f.ord, f.type, false, now()
                FROM fields f LEFT JOIN rules r ON r.key = f.api
                WHERE NOT EXISTS (SELECT 1 FROM "FieldConfigurations" fc
                                  WHERE fc."ModuleKey" = 'CaseManagement' AND fc."SectionKey" = 'CreateTeam' AND fc."ApiField" = f.api);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The Team form rows are removed; everything else is dropped by the schema steps below (data in kept tables is untouched).
            migrationBuilder.Sql("""DELETE FROM "FieldConfigurations" WHERE "ModuleKey" = 'CaseManagement' AND "SectionKey" = 'CreateTeam';""");
            // Sub-categories created after this migration have no team; the old schema needs one, so they take their
            // department's default team before the column becomes required again.
            migrationBuilder.Sql("""
                UPDATE "DepartmentSubCategories" s SET "DepartmentId" = (
                    SELECT td."TeamId" FROM "TeamDepartments" td WHERE td."OrgDepartmentId" = s."OrgDepartmentId"
                    ORDER BY td."IsDefault" DESC, td."LinkedAt" LIMIT 1)
                WHERE s."DepartmentId" IS NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Cases_OrgDepartments_OrgDepartmentId",
                table: "Cases");

            migrationBuilder.DropForeignKey(
                name: "FK_DepartmentSubCategories_Departments_DepartmentId",
                table: "DepartmentSubCategories");

            migrationBuilder.DropForeignKey(
                name: "FK_DepartmentSubCategories_OrgDepartments_OrgDepartmentId",
                table: "DepartmentSubCategories");

            migrationBuilder.DropTable(
                name: "TeamCustomAttributes");

            migrationBuilder.DropIndex(
                name: "IX_TeamDepartments_OneDefaultPerDepartment",
                table: "TeamDepartments");

            migrationBuilder.DropIndex(
                name: "IX_DepartmentSubCategories_OrgDepartmentId",
                table: "DepartmentSubCategories");

            migrationBuilder.DropIndex(
                name: "IX_Cases_OrgDepartmentId",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_CaseEvents_UserId_CreatedAt",
                table: "CaseEvents");

            migrationBuilder.DropColumn(
                name: "AllowDeactivateWithOpenCases",
                table: "TeamSettings");

            migrationBuilder.DropColumn(
                name: "AllowLeadInOtherTeams",
                table: "TeamSettings");

            migrationBuilder.DropColumn(
                name: "AllowTeamsWithoutMembers",
                table: "TeamSettings");

            migrationBuilder.DropColumn(
                name: "RequireTeamLead",
                table: "TeamSettings");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "TeamDepartments");

            migrationBuilder.DropColumn(
                name: "OrgDepartmentId",
                table: "DepartmentSubCategories");

            migrationBuilder.DropColumn(
                name: "OrgDepartmentId",
                table: "Cases");

            migrationBuilder.AlterColumn<Guid>(
                name: "DepartmentId",
                table: "DepartmentSubCategories",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_UserId",
                table: "CaseEvents",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DepartmentSubCategories_Departments_DepartmentId",
                table: "DepartmentSubCategories",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
