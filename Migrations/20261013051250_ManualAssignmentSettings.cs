using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class ManualAssignmentSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanBeManuallyAssigned",
                table: "TeamMembers",
                type: "boolean",
                nullable: false,
                defaultValue: true);   // every existing member stays choosable, exactly as before this setting existed

            migrationBuilder.CreateTable(
                name: "ManualAssignmentSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncludeTeamLeads = table.Column<bool>(type: "boolean", nullable: false),
                    RequireAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    EnforceCapacity = table.Column<bool>(type: "boolean", nullable: false),
                    CrossTeamMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualAssignmentSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManualAssignmentCrossTeams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingsId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualAssignmentCrossTeams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualAssignmentCrossTeams_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManualAssignmentCrossTeams_ManualAssignmentSettings_Setting~",
                        column: x => x.SettingsId,
                        principalTable: "ManualAssignmentSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualAssignmentCrossTeams_DepartmentId",
                table: "ManualAssignmentCrossTeams",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualAssignmentCrossTeams_SettingsId_DepartmentId",
                table: "ManualAssignmentCrossTeams",
                columns: new[] { "SettingsId", "DepartmentId" },
                unique: true);

            // The policy is a single row, and the mode is one of the values the engine understands.
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ""UX_ManualAssignmentSettings_Single"" ON ""ManualAssignmentSettings"" ((true));");
            migrationBuilder.Sql(@"ALTER TABLE ""ManualAssignmentSettings"" ADD CONSTRAINT ""CK_ManualAssignmentSettings_CrossTeamMode""
                CHECK (""CrossTeamMode"" IN ('Disabled', 'SelectedTeams', 'AllTeams'));");

            // The row starts as the behaviour in force before it existed (team only, leads included, Available/Busy, below capacity),
            // so applying this migration changes nothing until an administrator edits the settings.
            migrationBuilder.Sql(@"
                INSERT INTO ""ManualAssignmentSettings"" (""Id"", ""IncludeTeamLeads"", ""RequireAvailable"", ""EnforceCapacity"", ""CrossTeamMode"", ""CreatedAt"")
                SELECT gen_random_uuid(), true, true, true, 'Disabled', now()
                WHERE NOT EXISTS (SELECT 1 FROM ""ManualAssignmentSettings"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualAssignmentCrossTeams");

            migrationBuilder.DropTable(
                name: "ManualAssignmentSettings");

            migrationBuilder.DropColumn(
                name: "CanBeManuallyAssigned",
                table: "TeamMembers");
        }
    }
}
