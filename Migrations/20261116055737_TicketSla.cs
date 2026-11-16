using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class TicketSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AssignDueAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AssignTargetMinutes",
                table: "Tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AssignedWithinMinutes",
                table: "Tickets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SlaBreachNotified",
                table: "Tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SlaBreachedAt",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlaOutcome",
                table: "Tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Existing priorities and the calendar row start with the documented 30-minute assign-within time. It is a database
            // default only: the model has none, so EF always writes the value it is given (an explicit 0, "no target", included).
            migrationBuilder.AddColumn<int>(
                name: "TicketAssignMinutes",
                table: "PrioritySlaRules",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "TicketAssignUnit",
                table: "PrioritySlaRules",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Minutes");

            migrationBuilder.AddColumn<int>(
                name: "TicketAssignValue",
                table: "PrioritySlaRules",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<DateTime>(
                name: "RequestReceivedAt",
                table: "Cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceTicketId",
                table: "Cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceTicketNumber",
                table: "Cases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TicketDefaultAssignMinutes",
                table: "BusinessCalendarSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "TicketDefaultAssignUnit",
                table: "BusinessCalendarSettings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Minutes");

            migrationBuilder.AddColumn<int>(
                name: "TicketDefaultAssignValue",
                table: "BusinessCalendarSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Status_AssignDueAt",
                table: "Tickets",
                columns: new[] { "Status", "AssignDueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_Status_AssignDueAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssignDueAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssignTargetMinutes",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssignedWithinMinutes",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SlaBreachNotified",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SlaBreachedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SlaOutcome",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TicketAssignMinutes",
                table: "PrioritySlaRules");

            migrationBuilder.DropColumn(
                name: "TicketAssignUnit",
                table: "PrioritySlaRules");

            migrationBuilder.DropColumn(
                name: "TicketAssignValue",
                table: "PrioritySlaRules");

            migrationBuilder.DropColumn(
                name: "RequestReceivedAt",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "SourceTicketId",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "SourceTicketNumber",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "TicketDefaultAssignMinutes",
                table: "BusinessCalendarSettings");

            migrationBuilder.DropColumn(
                name: "TicketDefaultAssignUnit",
                table: "BusinessCalendarSettings");

            migrationBuilder.DropColumn(
                name: "TicketDefaultAssignValue",
                table: "BusinessCalendarSettings");
        }
    }
}
