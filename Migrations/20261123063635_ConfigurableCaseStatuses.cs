using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableCaseStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StatusKey",
                table: "Cases",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseStatusDefinitions",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    SlaBehaviour = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MaxPauseMinutes = table.Column<int>(type: "integer", nullable: true),
                    OnPauseExpiry = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PauseExpiryStatusKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseStatusDefinitions", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "CaseStatusTransitions",
                columns: table => new
                {
                    FromKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ToKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseStatusTransitions", x => new { x.FromKey, x.ToKey });
                    table.ForeignKey(
                        name: "FK_CaseStatusTransitions_CaseStatusDefinitions_FromKey",
                        column: x => x.FromKey,
                        principalTable: "CaseStatusDefinitions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaseStatusTransitions_CaseStatusDefinitions_ToKey",
                        column: x => x.ToKey,
                        principalTable: "CaseStatusDefinitions",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_StatusKey",
                table: "Cases",
                column: "StatusKey",
                filter: "\"StatusKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseStatusTransitions_ToKey",
                table: "CaseStatusTransitions",
                column: "ToKey");
            // The statuses the workflow has always had, as system statuses; a label renamed under the old Status master list
            // (CASE_STATUS) carries over, so nobody's wording changes. Then the manual moves the workflow allowed until now.
            migrationBuilder.Sql("""
                INSERT INTO "CaseStatusDefinitions" ("Key", "Label", "Category", "Level", "SlaBehaviour", "Color", "IsSystem", "IsActive", "CreatedAt")
                SELECT d.key,
                       COALESCE((SELECT lv."Label" FROM "LookupValues" lv
                                  WHERE lv."TypeCode" = 'CASE_STATUS' AND lv."Value" = d.key AND lv."IsActive" AND btrim(lv."Label") <> ''
                                  ORDER BY lv."DisplayOrder" LIMIT 1), d.label),
                       d.category, d.level, d.sla, d.color, TRUE, d.active, now() AT TIME ZONE 'utc'
                FROM (VALUES
                    ('Open', 'Open', 'Open', 1, 'Running', 'blue', TRUE),
                    ('InProgress', 'In Progress', 'Active', 2, 'Running', 'indigo', TRUE),
                    ('WaitingOnCustomer', 'Waiting on Customer', 'Waiting', 3, 'Paused', 'amber', TRUE),
                    ('Escalated', 'Escalated', 'Escalated', 4, 'Running', 'red', TRUE),
                    ('Resolved', 'Resolved', 'Closed', 5, 'Stopped', 'green', TRUE),
                    ('Closed', 'Closed', 'Closed', 90, 'Stopped', 'slate', FALSE),
                    ('Cancelled', 'Cancelled', 'Closed', 91, 'Stopped', 'slate', FALSE)
                ) AS d(key, label, category, level, sla, color, active)
                ON CONFLICT ("Key") DO NOTHING;

                INSERT INTO "CaseStatusTransitions" ("FromKey", "ToKey") VALUES
                    ('InProgress', 'WaitingOnCustomer'),
                    ('WaitingOnCustomer', 'InProgress'),
                    ('Escalated', 'InProgress'),
                    ('Escalated', 'WaitingOnCustomer')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseStatusTransitions");

            migrationBuilder.DropTable(
                name: "CaseStatusDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_Cases_StatusKey",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "StatusKey",
                table: "Cases");
        }
    }
}
