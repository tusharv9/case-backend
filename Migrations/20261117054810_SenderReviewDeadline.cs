using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SenderReviewDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every mailbox starts with a one-hour review deadline, a reminder at 30 minutes and escalation to its teams' leads at
            // the deadline; an administrator changes them per mailbox. Emails already waiting get their times from the monitor.
            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewDueAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewEscalateAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewEscalatedAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewReminderAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewReminderSentAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewSlaMinutes",
                table: "InboundEmails",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewSlaOutcome",
                table: "InboundEmails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewEscalateTo",
                table: "EmailMailboxes",
                type: "text",
                nullable: false,
                defaultValue: "TeamLeads");

            migrationBuilder.AddColumn<int>(
                name: "ReviewEscalationMinutes",
                table: "EmailMailboxes",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "ReviewReminderMinutes",
                table: "EmailMailboxes",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "ReviewSlaMinutes",
                table: "EmailMailboxes",
                type: "integer",
                nullable: false,
                defaultValue: 60);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewDueAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewEscalateAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewEscalatedAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewReminderAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewReminderSentAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewSlaMinutes",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewSlaOutcome",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewEscalateTo",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "ReviewEscalationMinutes",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "ReviewReminderMinutes",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "ReviewSlaMinutes",
                table: "EmailMailboxes");
        }
    }
}
