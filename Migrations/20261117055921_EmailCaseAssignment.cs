using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class EmailCaseAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaseSource",
                table: "AssignmentConfigurations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Default");

            migrationBuilder.CreateIndex(
                name: "IX_AssignmentConfigurations_Global_CaseSource",
                table: "AssignmentConfigurations",
                column: "CaseSource",
                unique: true,
                filter: "\"TeamId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssignmentConfigurations_Global_CaseSource",
                table: "AssignmentConfigurations");

            migrationBuilder.DropColumn(
                name: "CaseSource",
                table: "AssignmentConfigurations");
        }
    }
}
