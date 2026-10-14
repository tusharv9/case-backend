using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class OpenCaseIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Partial indexes over just the OPEN cases: an agent's load, a team's queue, the monitor and the worker read only these,
            // and the table they are carved from grows forever.
            migrationBuilder.CreateIndex(
                name: "IX_Cases_Open_Department",
                table: "Cases",
                columns: new[] { "DepartmentId", "Status" },
                filter: "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_Open_Owner",
                table: "Cases",
                columns: new[] { "OwnerId", "Status" },
                filter: "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_Open_SlaStart",
                table: "Cases",
                column: "SlaStartTime",
                filter: "\"Status\" NOT IN ('Resolved', 'Closed', 'Cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_Cases_Open_Department", table: "Cases");
            migrationBuilder.DropIndex(name: "IX_Cases_Open_Owner", table: "Cases");
            migrationBuilder.DropIndex(name: "IX_Cases_Open_SlaStart", table: "Cases");
        }
    }
}
