using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class EscalationHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AssignOnAuto",
                table: "EscalationLevelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "FallbackPolicy",
                table: "EscalationLevelConfigs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NextLevel");

            migrationBuilder.AddColumn<bool>(
                name: "IsInitial",
                table: "EscalationLevelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ManualAllowed",
                table: "EscalationLevelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "RoutingStrategy",
                table: "EscalationLevelConfigs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Team");

            migrationBuilder.AddColumn<string>(
                name: "TriggerPriorities",
                table: "EscalationLevelConfigs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EscalationCycle",
                table: "Cases",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CaseEscalations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cycle = table.Column<int>(type: "integer", nullable: false),
                    FromLevel = table.Column<int>(type: "integer", nullable: false),
                    ToLevel = table.Column<int>(type: "integer", nullable: false),
                    ToLevelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SlaConsumedPercent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    ReasonText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FromOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecipientIds = table.Column<string>(type: "text", nullable: false),
                    RecipientNames = table.Column<string>(type: "text", nullable: false),
                    FallbackNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseEscalations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseEscalations_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CaseNotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseNotificationOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EscalationLevelRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LevelId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RoleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscalationLevelRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EscalationLevelRoles_EscalationLevelConfigs_LevelId",
                        column: x => x.LevelId,
                        principalTable: "EscalationLevelConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEscalations_CaseId_Cycle_ToLevel",
                table: "CaseEscalations",
                columns: new[] { "CaseId", "Cycle", "ToLevel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseEscalations_CreatedAt",
                table: "CaseEscalations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CaseNotificationOutbox_CompletedAt_NextAttemptAt",
                table: "CaseNotificationOutbox",
                columns: new[] { "CompletedAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EscalationLevelRoles_LevelId",
                table: "EscalationLevelRoles",
                column: "LevelId");

            migrationBuilder.CreateIndex(
                name: "IX_EscalationLevelRoles_RoleKey",
                table: "EscalationLevelRoles",
                column: "RoleKey",
                unique: true);

            // The matrix as it stood: each level keeps its one role (by name, until the next save stores the Host's role id), keeps
            // reaching its own team first and going up a level when nobody there can take it, and the highest level goes to the
            // administrators rather than to nobody. No level is made the initial handlers' level here: that is the administrator's call.
            migrationBuilder.Sql("""
                INSERT INTO "EscalationLevelRoles" ("Id", "LevelId", "RoleKey", "RoleName")
                SELECT gen_random_uuid(), l."Id", trim(l."TargetRole"), trim(l."TargetRole")
                FROM "EscalationLevelConfigs" l
                WHERE trim(coalesce(l."TargetRole", '')) <> ''
                  AND NOT EXISTS (SELECT 1 FROM "EscalationLevelConfigs" o WHERE lower(trim(o."TargetRole")) = lower(trim(l."TargetRole")) AND o."LevelNumber" < l."LevelNumber");

                UPDATE "EscalationLevelConfigs" SET "FallbackPolicy" = 'Administrators'
                WHERE "LevelNumber" = (SELECT max("LevelNumber") FROM "EscalationLevelConfigs");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseEscalations");

            migrationBuilder.DropTable(
                name: "CaseNotificationOutbox");

            migrationBuilder.DropTable(
                name: "EscalationLevelRoles");

            migrationBuilder.DropColumn(
                name: "AssignOnAuto",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "FallbackPolicy",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "IsInitial",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "ManualAllowed",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "RoutingStrategy",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "TriggerPriorities",
                table: "EscalationLevelConfigs");

            migrationBuilder.DropColumn(
                name: "EscalationCycle",
                table: "Cases");
        }
    }
}
