using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SubCategoryDescriptionAndUniqueCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "DepartmentSubCategories",
                type: "text",
                nullable: true);

            // Older databases may hold the same (generated) code twice: the oldest keeps it, later ones get their id appended,
            // so the unique index can be created without losing any row.
            migrationBuilder.Sql("""
                UPDATE "DepartmentSubCategories" s SET "Code" = s."Code" || '-' || left(s."Id"::text, 8)
                FROM (SELECT "Id", row_number() OVER (PARTITION BY "Code" ORDER BY "CreatedAt", "Id") AS n
                      FROM "DepartmentSubCategories" WHERE "Code" <> '') d
                WHERE d."Id" = s."Id" AND d.n > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DepartmentSubCategories_Code",
                table: "DepartmentSubCategories",
                column: "Code",
                unique: true,
                filter: "\"Code\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DepartmentSubCategories_Code",
                table: "DepartmentSubCategories");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "DepartmentSubCategories");
        }
    }
}
