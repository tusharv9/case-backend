using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SlaEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The level-number flags are replaced by the escalation level a case is on; the reminder flag stays (renamed:
            // its threshold now comes from the first escalation level, not a hard-coded 70%).
            migrationBuilder.DropColumn(name: "Sla90Escalated", table: "Cases");
            migrationBuilder.DropColumn(name: "SlaBreachedEscalated", table: "Cases");
            migrationBuilder.DropColumn(name: "Sla12hBreachedEscalated", table: "Cases");
            migrationBuilder.RenameColumn(name: "Sla70ReminderSent", table: "Cases", newName: "SlaReminderSent");

            migrationBuilder.AddColumn<string>(
                name: "EventKey",
                table: "Notifications",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BusinessCalendarSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessCalendarSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Notifications_Recipient_EventKey",
                table: "Notifications",
                columns: new[] { "RecipientUserId", "EventKey" },
                unique: true,
                filter: "\"EventKey\" IS NOT NULL");

            // ---- data: keep today's behaviour, but as explicit, visible configuration ------------------------------

            // The calendar was hard-wired to Malaysia time; that is now a setting, seeded with the same value.
            migrationBuilder.Sql(@"
                INSERT INTO ""BusinessCalendarSettings"" (""Id"", ""TimeZoneId"", ""CreatedAt"")
                SELECT gen_random_uuid(), 'Asia/Kuala_Lumpur', now()
                WHERE NOT EXISTS (SELECT 1 FROM ""BusinessCalendarSettings"");");

            // Level 1 is the case owner, not a role lookup.
            migrationBuilder.Sql(@"
                UPDATE ""EscalationLevelConfigs""
                SET ""AssignmentType"" = 'Owner'
                WHERE ""LevelNumber"" = 1 AND ""AssignmentType"" = 'Role' AND lower(""TargetRole"") = 'assigned agent';");

            // A named person only applies to assignment type 'User'; for 'Role'/'Owner' the seeder had pinned an arbitrary
            // user, which made every escalation go to the same person. The role is resolved when the escalation happens.
            migrationBuilder.Sql(@"
                UPDATE ""EscalationLevelConfigs"" SET ""TargetUserId"" = NULL WHERE ""AssignmentType"" IN ('Role', 'Owner');");

            // Levels added through the old editor had no threshold and silently fired at 70%. Make that explicit.
            migrationBuilder.Sql(@"
                UPDATE ""EscalationLevelConfigs""
                SET ""TriggerValue"" = 70
                WHERE ""TriggerType"" = 'SlaPercentage' AND ""TriggerValue"" IS NULL;");
            migrationBuilder.Sql(@"
                UPDATE ""EscalationLevelConfigs"" SET ""TriggerValue"" = NULL
                WHERE ""TriggerType"" IN ('SlaBreached', 'FirstResponseBreached', 'ManualOnly');");

            // Trigger text is now derived from the structured trigger; regenerate it so the two always agree.
            migrationBuilder.Sql(@"
                UPDATE ""EscalationLevelConfigs"" SET ""TriggerDescription"" = CASE ""TriggerType""
                    WHEN 'SlaPercentage' THEN 'SLA consumption reaches ' || trim(trailing '.' from trim(trailing '0' from ""TriggerValue""::text)) || '%'
                    WHEN 'SlaBreached' THEN 'SLA is breached'
                    WHEN 'SlaPostBreachHours' THEN 'SLA has been breached for ' || trim(trailing '.' from trim(trailing '0' from ""TriggerValue""::text)) || ' hours'
                    WHEN 'FirstResponseBreached' THEN 'First response is overdue'
                    WHEN 'ManualOnly' THEN 'Manual escalation only'
                    ELSE ""TriggerDescription"" END;");

            // Cases made by the linked/reopened-subcase workflows never received their own SLA snapshot (only the model's
            // built-in 120/240 minute defaults). Give them their priority's configured targets; the monitor derives due dates.
            migrationBuilder.Sql(@"
                UPDATE ""Cases"" c
                SET ""FirstResponseTargetMinutes"" = r.""FirstResponseMinutes"",
                    ""InternalResolutionTargetMinutes"" = r.""InternalResolutionMinutes"",
                    ""ExternalResolutionTargetMinutes"" = r.""ExternalResolutionMinutes"",
                    ""SlaTargetHours"" = CEIL(r.""ExternalResolutionMinutes"" / 60.0)::int,
                    ""SlaConfigVersion"" = r.""Version""
                FROM ""PrioritySlaRules"" r
                WHERE c.""InternalResolutionDueAt"" IS NULL AND lower(r.""Priority"") = lower(c.""Severity"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessCalendarSettings");

            migrationBuilder.DropIndex(
                name: "UX_Notifications_Recipient_EventKey",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "EventKey",
                table: "Notifications");

            migrationBuilder.RenameColumn(name: "SlaReminderSent", table: "Cases", newName: "Sla70ReminderSent");
            migrationBuilder.AddColumn<bool>(name: "Sla90Escalated", table: "Cases", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "SlaBreachedEscalated", table: "Cases", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "Sla12hBreachedEscalated", table: "Cases", type: "boolean", nullable: false, defaultValue: false);
        }
    }
}
