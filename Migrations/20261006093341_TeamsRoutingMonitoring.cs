using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class TeamsRoutingMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssignmentConfigurations_DepartmentId",
                table: "AssignmentConfigurations");

            migrationBuilder.DropColumn(
                name: "PrimaryChannel",
                table: "TeamMembers");

            migrationBuilder.DropColumn(
                name: "TargetQueueName",
                table: "RoutingRules");

            migrationBuilder.DropColumn(
                name: "Channels",
                table: "Departments");

            migrationBuilder.AddColumn<bool>(
                name: "IsAssignable",
                table: "TeamMembers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "SkillRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillName = table.Column<string>(type: "text", nullable: false),
                    MatchField = table.Column<string>(type: "text", nullable: false),
                    MatchType = table.Column<string>(type: "text", nullable: false),
                    MatchValue = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillRules", x => x.Id);
                });

            // One settings row per team: keep the newest if a team somehow has several.
            migrationBuilder.Sql(@"
                DELETE FROM ""AssignmentConfigurations"" a
                USING ""AssignmentConfigurations"" b
                WHERE a.""DepartmentId"" IS NOT NULL AND a.""DepartmentId"" = b.""DepartmentId""
                  AND (a.""CreatedAt"", a.""Id"") < (b.""CreatedAt"", b.""Id"");");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentConfigurations_DepartmentId",
                table: "AssignmentConfigurations",
                column: "DepartmentId",
                unique: true,
                filter: "\"DepartmentId\" IS NOT NULL");

            // ---- data ------------------------------------------------------------------------------------------------

            // Routing used to treat a user's "home team" as membership too. Membership is now the only source, so give every
            // active user who was eligible that way a real membership row — nobody silently drops out of rotation.
            migrationBuilder.Sql(@"
                INSERT INTO ""TeamMembers"" (""Id"", ""DepartmentId"", ""UserId"", ""MemberRole"", ""IsActive"", ""IsAssignable"", ""JoinedAt"", ""CreatedAt"")
                SELECT gen_random_uuid(), u.""DepartmentId"", u.""Id"",
                       CASE WHEN coalesce(u.""Role"", '') = '' THEN 'Service Agent' ELSE u.""Role"" END, true, true, now(), now()
                FROM ""Users"" u
                WHERE u.""DepartmentId"" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM ""TeamMembers"" m WHERE m.""DepartmentId"" = u.""DepartmentId"" AND m.""UserId"" = u.""Id"");");

            // A team lead is a member of their team, but (as before) only receives cases automatically if their own home team made them eligible above.
            migrationBuilder.Sql(@"
                INSERT INTO ""TeamMembers"" (""Id"", ""DepartmentId"", ""UserId"", ""MemberRole"", ""IsActive"", ""IsAssignable"", ""JoinedAt"", ""CreatedAt"")
                SELECT gen_random_uuid(), d.""Id"", d.""OwnerId"", 'Team Lead', true, false, now(), now()
                FROM ""Departments"" d
                WHERE d.""OwnerId"" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM ""TeamMembers"" m WHERE m.""DepartmentId"" = d.""Id"" AND m.""UserId"" = d.""OwnerId"");");

            // The global default used to be a constant in the code when no row existed; make it a visible setting.
            migrationBuilder.Sql(@"
                INSERT INTO ""AssignmentConfigurations"" (""Id"", ""DepartmentId"", ""Algorithm"", ""MaxConcurrentCapacity"", ""IsActive"", ""CreatedAt"")
                SELECT gen_random_uuid(), NULL, 'RoundRobin', 5, true, now()
                WHERE NOT EXISTS (SELECT 1 FROM ""AssignmentConfigurations"" WHERE ""DepartmentId"" IS NULL);");
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SkillRules");

            migrationBuilder.DropIndex(
                name: "IX_AssignmentConfigurations_DepartmentId",
                table: "AssignmentConfigurations");

            migrationBuilder.DropColumn(
                name: "IsAssignable",
                table: "TeamMembers");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryChannel",
                table: "TeamMembers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TargetQueueName",
                table: "RoutingRules",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Channels",
                table: "Departments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentConfigurations_DepartmentId",
                table: "AssignmentConfigurations",
                column: "DepartmentId");
        }
    }
}
