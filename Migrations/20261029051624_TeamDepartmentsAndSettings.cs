using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class TeamDepartmentsAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StatusUpdatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrgDepartments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgDepartments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowMultipleTeamMembership = table.Column<bool>(type: "boolean", nullable: false),
                    AllowMultipleTeamLeads = table.Column<bool>(type: "boolean", nullable: false),
                    AllowMultipleDepartments = table.Column<bool>(type: "boolean", nullable: false),
                    FormFields = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TeamDepartments",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrgDepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamDepartments", x => new { x.TeamId, x.OrgDepartmentId });
                    table.ForeignKey(
                        name: "FK_TeamDepartments_Departments_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamDepartments_OrgDepartments_OrgDepartmentId",
                        column: x => x.OrgDepartmentId,
                        principalTable: "OrgDepartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrgDepartments_Code",
                table: "OrgDepartments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrgDepartments_Name",
                table: "OrgDepartments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamDepartments_OrgDepartmentId",
                table: "TeamDepartments",
                column: "OrgDepartmentId");

            // The Teams settings are a single row, starting as the behaviour in force before they existed: several teams per
            // person and per lead allowed, several departments per team allowed, every form field shown and optional. Applying
            // this migration therefore changes nothing until an administrator edits the settings.
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ""UX_TeamSettings_Single"" ON ""TeamSettings"" ((true));");
            migrationBuilder.Sql(@"
                INSERT INTO ""TeamSettings"" (""Id"", ""AllowMultipleTeamMembership"", ""AllowMultipleTeamLeads"", ""AllowMultipleDepartments"", ""FormFields"", ""CreatedAt"")
                SELECT gen_random_uuid(), true, true, true,
                       '[{""Key"":""description"",""IsVisible"":true,""IsRequired"":false},{""Key"":""departments"",""IsVisible"":true,""IsRequired"":false},{""Key"":""teamLead"",""IsVisible"":true,""IsRequired"":false},{""Key"":""members"",""IsVisible"":true,""IsRequired"":false}]'::jsonb,
                       now()
                WHERE NOT EXISTS (SELECT 1 FROM ""TeamSettings"");");

            // Until now every team WAS its department. Each existing team gets a department of the same name and code (active
            // when the team is) and is linked to it, so filtering and reporting by department start out exactly as by team.
            // Nothing about the teams themselves — members, leads, routing, SLA, cases — is touched.
            migrationBuilder.Sql(@"
                INSERT INTO ""OrgDepartments"" (""Id"", ""Name"", ""Code"", ""Description"", ""IsActive"", ""DisplayOrder"", ""CreatedAt"")
                SELECT gen_random_uuid(), d.""Name"", d.""Code"", NULL, d.""IsActive"",
                       ROW_NUMBER() OVER (ORDER BY d.""Name""), now()
                FROM ""Departments"" d
                WHERE NOT EXISTS (SELECT 1 FROM ""OrgDepartments"" o WHERE lower(o.""Name"") = lower(d.""Name"") OR upper(o.""Code"") = upper(d.""Code""));");
            migrationBuilder.Sql(@"
                INSERT INTO ""TeamDepartments"" (""TeamId"", ""OrgDepartmentId"", ""LinkedAt"")
                SELECT d.""Id"", o.""Id"", now()
                FROM ""Departments"" d
                JOIN ""OrgDepartments"" o ON lower(o.""Name"") = lower(d.""Name"")
                ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamDepartments");

            migrationBuilder.DropTable(
                name: "TeamSettings");

            migrationBuilder.DropTable(
                name: "OrgDepartments");

            migrationBuilder.DropColumn(
                name: "StatusUpdatedAt",
                table: "Users");
        }
    }
}
