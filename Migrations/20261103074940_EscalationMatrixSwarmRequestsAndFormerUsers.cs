using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class EscalationMatrixSwarmRequestsAndFormerUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotificationPriority",
                table: "EscalationLevelConfigs",
                type: "text",
                nullable: false,
                defaultValue: "High");

            migrationBuilder.AddColumn<bool>(
                name: "NotifyCaseOwner",
                table: "EscalationLevelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyRoleHolders",
                table: "EscalationLevelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyTeamLead",
                table: "EscalationLevelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "CaseEvents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "CaseSwarmRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResponseNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseSwarmRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseSwarmRequests_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaseSwarmRequests_Departments_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CaseSwarmRequests_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseSwarmRequests_Users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseSwarmRequests_CaseId_CreatedAt",
                table: "CaseSwarmRequests",
                columns: new[] { "CaseId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_CaseSwarmRequests_RecipientUserId_Status",
                table: "CaseSwarmRequests",
                columns: new[] { "RecipientUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseSwarmRequests_RequestedByUserId",
                table: "CaseSwarmRequests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseSwarmRequests_TeamId",
                table: "CaseSwarmRequests",
                column: "TeamId");

            // Escalation levels now count from 0 ("not escalated"); Level n is the matrix's n-th escalation. Under the old
            // matrix every case started ON Level 1 (the assigned agent), so every case moves down one.
            migrationBuilder.Sql(@"UPDATE ""Cases"" SET ""EscalationLevel"" = GREATEST(""EscalationLevel"" - 1, 0);");

            // The old Level 1 was the reminder to the case's own holder, not an escalation: it goes, and the levels after
            // it move down one (via negative numbers, because LevelNumber is unique) — the same shift the cases just made.
            // Its reminder lives on as a level's "notify the case owner" setting.
            migrationBuilder.Sql(@"
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM ""EscalationLevelConfigs"" WHERE ""LevelNumber"" = 1 AND ""AssignmentType"" = 'Owner') THEN
    DELETE FROM ""EscalationLevelConfigs"" WHERE ""LevelNumber"" = 1 AND ""AssignmentType"" = 'Owner';
    UPDATE ""EscalationLevelConfigs"" SET ""LevelNumber"" = -(""LevelNumber"" - 1);
    UPDATE ""EscalationLevelConfigs"" SET ""LevelNumber"" = -""LevelNumber"", ""DisplayOrder"" = -""LevelNumber"",
      ""Name"" = CASE WHEN ""Name"" LIKE 'Level %' THEN 'Level ' || (-""LevelNumber"")::text ELSE ""Name"" END;
  END IF;
END $$;");

            // Escalation is role-based and notifies only: older levels pointing at a person or the holder become role
            // levels with no role (incomplete until an administrator chooses one), and nothing reassigns automatically.
            migrationBuilder.Sql(@"UPDATE ""EscalationLevelConfigs"" SET ""TargetRole"" = '', ""TargetUserId"" = NULL WHERE ""AssignmentType"" <> 'Role';");
            migrationBuilder.Sql(@"UPDATE ""EscalationLevelConfigs"" SET ""AssignmentType"" = 'Role', ""ReassignOwner"" = FALSE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseSwarmRequests");

            migrationBuilder.DropColumn(
                name: "NotificationPriority",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "NotifyCaseOwner",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "NotifyRoleHolders",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "NotifyTeamLead",
                table: "EscalationLevelConfigs");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "CaseEvents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
