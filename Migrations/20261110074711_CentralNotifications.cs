using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class CentralNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Notifications now live in OmniConnect's central store. Before the local table goes, keep the one thing the SLA
            // monitor still needs from it: which open cases' current breach was already reported, and how many reminders
            // followed — otherwise every breached case would be reported again on the first cycle after this migration.
            migrationBuilder.CreateTable(
                name: "CaseSlaAlerts",
                columns: table => new
                {
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reminders = table.Column<int>(type: "integer", nullable: false),
                    LastReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseSlaAlerts", x => x.CaseId);
                    table.ForeignKey(
                        name: "FK_CaseSlaAlerts_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(@"
                INSERT INTO ""CaseSlaAlerts"" (""CaseId"", ""CycleStart"", ""NotifiedAt"", ""Reminders"", ""LastReminderAt"")
                SELECT c.""Id"", c.""SlaStartTime"", MIN(n.""CreatedAt""), COALESCE(MAX(n.""ReminderCount""), 0), MAX(n.""LastReminderAt"")
                FROM ""Notifications"" n
                JOIN ""Cases"" c ON c.""Id"" = n.""CaseId""
                WHERE n.""Type"" IN ('SLA_BREACHED', 'SLA_BREACH_REMINDER') AND n.""CreatedAt"" >= c.""SlaStartTime"" - INTERVAL '1 minute'
                GROUP BY c.""Id"", c.""SlaStartTime"";");

            // Then the old notifications themselves are removed, as decided: nothing is copied to the central store.
            migrationBuilder.DropTable(
                name: "Notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseSlaAlerts");

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseNumber = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventKey = table.Column<string>(type: "text", nullable: true),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    LastReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<string>(type: "text", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReminderCount = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_Users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Unread",
                table: "Notifications",
                column: "RecipientUserId",
                filter: "\"IsRead\" = false");

            migrationBuilder.CreateIndex(
                name: "UX_Notifications_Recipient_EventKey",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "EventKey" },
                unique: true,
                filter: "\"EventKey\" IS NOT NULL");
        }
    }
}
