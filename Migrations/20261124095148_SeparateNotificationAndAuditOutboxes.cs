using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeparateNotificationAndAuditOutboxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_OutboxMessages",
                table: "OutboxMessages");

            migrationBuilder.RenameTable(
                name: "OutboxMessages",
                newName: "AuditOutboxMessages");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_EventId",
                table: "AuditOutboxMessages",
                newName: "IX_AuditOutboxMessages_EventId");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_Due",
                table: "AuditOutboxMessages",
                newName: "IX_AuditOutboxMessages_Due");

            migrationBuilder.RenameIndex(
                name: "IX_OutboxMessages_CreatedAt",
                table: "AuditOutboxMessages",
                newName: "IX_AuditOutboxMessages_CreatedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AuditOutboxMessages",
                table: "AuditOutboxMessages",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "NotificationOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxMessages_CreatedAt",
                table: "NotificationOutboxMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxMessages_Due",
                table: "NotificationOutboxMessages",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "\"Status\" IN ('pending', 'sending')");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutboxMessages_EventId",
                table: "NotificationOutboxMessages",
                column: "EventId");

            // Notifications already in the single outbox (written before the split) move to their own table, undelivered ones
            // included, so nothing waiting is lost; only the audit rows stay in AuditOutboxMessages.
            migrationBuilder.Sql("""
                INSERT INTO "NotificationOutboxMessages" ("Id", "EventId", "Topic", "Payload", "Status", "Attempts", "NextAttemptAt", "LeaseUntil", "LastError", "CreatedAt", "SentAt")
                SELECT "Id", "EventId", "Topic", "Payload", "Status", "Attempts", "NextAttemptAt", "LeaseUntil", "LastError", "CreatedAt", "SentAt"
                FROM "AuditOutboxMessages" WHERE "Topic" = 'notification';
                DELETE FROM "AuditOutboxMessages" WHERE "Topic" = 'notification';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "AuditOutboxMessages" ("Id", "EventId", "Topic", "Payload", "Status", "Attempts", "NextAttemptAt", "LeaseUntil", "LastError", "CreatedAt", "SentAt")
                SELECT "Id", "EventId", "Topic", "Payload", "Status", "Attempts", "NextAttemptAt", "LeaseUntil", "LastError", "CreatedAt", "SentAt"
                FROM "NotificationOutboxMessages";
                """);

            migrationBuilder.DropTable(
                name: "NotificationOutboxMessages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AuditOutboxMessages",
                table: "AuditOutboxMessages");

            migrationBuilder.RenameTable(
                name: "AuditOutboxMessages",
                newName: "OutboxMessages");

            migrationBuilder.RenameIndex(
                name: "IX_AuditOutboxMessages_EventId",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_EventId");

            migrationBuilder.RenameIndex(
                name: "IX_AuditOutboxMessages_Due",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_Due");

            migrationBuilder.RenameIndex(
                name: "IX_AuditOutboxMessages_CreatedAt",
                table: "OutboxMessages",
                newName: "IX_OutboxMessages_CreatedAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OutboxMessages",
                table: "OutboxMessages",
                column: "Id");
        }
    }
}
