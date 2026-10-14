using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class TypeChangeIdFormatPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FormatMessage",
                table: "LookupValues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormatRegex",
                table: "LookupValues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormatRule",
                table: "LookupValues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsesFormatRules",
                table: "LookupTypes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_UserId_Key",
                table: "UserPreferences",
                columns: new[] { "UserId", "Key" },
                unique: true);

            // ---- Data -------------------------------------------------------------------------------------------------
            // ID types carry their own format rule now. Today's behaviour becomes the explicit default of each type.
            migrationBuilder.Sql(@"UPDATE ""LookupTypes"" SET ""UsesFormatRules"" = TRUE WHERE ""Code"" = 'ID_TYPE';
UPDATE ""LookupValues"" SET ""FormatRule"" = CASE
        WHEN lower(""Value"") LIKE '%passport%' THEN 'PASSPORT'
        WHEN lower(""Value"") LIKE '%account%'  THEN 'ACCOUNT_NUMBER'
        WHEN lower(""Value"") LIKE '%nric%'     THEN 'MY_NRIC'
        ELSE 'ANY' END
 WHERE ""TypeCode"" = 'ID_TYPE' AND ""FormatRule"" IS NULL;");

            // Customer directory search/sort: trigram indexes on the other searched columns (skipped quietly when the
            // extension is unavailable, exactly like the existing ones).
            migrationBuilder.Sql(@"DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
        CREATE INDEX IF NOT EXISTS ""IX_Customers_Email_Trgm"" ON ""Customers"" USING gin (""Email"" gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ""IX_Customers_PhoneNumber_Trgm"" ON ""Customers"" USING gin (""PhoneNumber"" gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ""IX_Customers_Branch_Trgm"" ON ""Customers"" USING gin (""Branch"" gin_trgm_ops);
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ""IX_Customers_FullName_Id"" ON ""Customers"" (""FullName"", ""Id"");
CREATE INDEX IF NOT EXISTS ""IX_Customers_CreatedAt_Id"" ON ""Customers"" (""CreatedAt"", ""Id"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Customers_Email_Trgm"";
DROP INDEX IF EXISTS ""IX_Customers_PhoneNumber_Trgm"";
DROP INDEX IF EXISTS ""IX_Customers_Branch_Trgm"";
DROP INDEX IF EXISTS ""IX_Customers_FullName_Id"";
DROP INDEX IF EXISTS ""IX_Customers_CreatedAt_Id"";");

            migrationBuilder.DropTable(
                name: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "FormatMessage",
                table: "LookupValues");

            migrationBuilder.DropColumn(
                name: "FormatRegex",
                table: "LookupValues");

            migrationBuilder.DropColumn(
                name: "FormatRule",
                table: "LookupValues");

            migrationBuilder.DropColumn(
                name: "UsesFormatRules",
                table: "LookupTypes");
        }
    }
}
