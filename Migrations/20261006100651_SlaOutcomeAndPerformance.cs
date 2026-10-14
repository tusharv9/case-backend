using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SlaOutcomeAndPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SlaOutcome",
                table: "Cases",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cases_SlaOutcome_Breached",
                table: "Cases",
                column: "SlaOutcome",
                filter: "\"SlaOutcome\" = 'Breached'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cases_SlaOutcome_Breached",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "SlaOutcome",
                table: "Cases");
        }
    }
}
