using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <summary>
    /// Skill-based assignment is removed; Round-robin and Least occupancy remain. Any assignment setting (the default or a
    /// team's own) that was on Skill-based is moved to Round-robin first, so no team is left on an algorithm that no longer
    /// exists. Then the skill rules and agents' skills are dropped. Down recreates the empty tables only; the rows are gone.
    /// </summary>
    public partial class RemoveSkillBasedAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"UPDATE ""AssignmentConfigurations"" SET ""Algorithm"" = 'RoundRobin', ""UpdatedAt"" = NOW() WHERE ""Algorithm"" = 'SkillBased';");

            migrationBuilder.DropTable(
                name: "AgentSkills");

            migrationBuilder.DropTable(
                name: "SkillRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSkills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProficiencyLevel = table.Column<int>(type: "integer", nullable: false),
                    SkillName = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentSkills_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SkillRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    MatchField = table.Column<string>(type: "text", nullable: false),
                    MatchType = table.Column<string>(type: "text", nullable: false),
                    MatchValue = table.Column<string>(type: "text", nullable: false),
                    SkillName = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkillRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSkills_UserId_SkillName",
                table: "AgentSkills",
                columns: new[] { "UserId", "SkillName" },
                unique: true);
        }
    }
}
