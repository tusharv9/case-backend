using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class CountriesPhoneAndFieldMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- Masking no longer depends on "Sensitive" -----------------------------------------------------
            // Until now a masking rule only took effect when the field was ALSO marked Sensitive. Keep exactly what
            // users see today: a rule on a field that was not Sensitive never masked anything, so it becomes "None"
            // (otherwise removing the flag would suddenly start hiding data). Rules on Sensitive fields are kept.
            migrationBuilder.Sql(@"UPDATE ""FieldConfigurations"" SET ""MaskingRule"" = 'None'
                                    WHERE ""IsSensitive"" = FALSE AND ""MaskingRule"" <> 'None';");

            migrationBuilder.DropColumn(
                name: "IsEditable",
                table: "FieldConfigurations");

            migrationBuilder.DropColumn(
                name: "IsSensitive",
                table: "FieldConfigurations");

            migrationBuilder.AddColumn<bool>(
                name: "AllowAdd",
                table: "LookupTypes",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "MaxValue",
                table: "FieldConfigurations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinValue",
                table: "FieldConfigurations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneCountryIso2",
                table: "Customers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "MY");

            migrationBuilder.CreateTable(
                name: "Countries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Iso2 = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Iso3 = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DialCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MinNationalDigits = table.Column<int>(type: "integer", nullable: false),
                    MaxNationalDigits = table.Column<int>(type: "integer", nullable: false),
                    NationalPattern = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Countries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Countries_Iso2",
                table: "Countries",
                column: "Iso2",
                unique: true);

            // ---- Data ---------------------------------------------------------------------------------------------
            // The customer record has a column for each of three ID types, so the ID type list is a fixed set.
            migrationBuilder.Sql(@"UPDATE ""LookupTypes"" SET ""AllowAdd"" = FALSE WHERE ""Code"" = 'ID_TYPE';");

            // Country metadata (idempotent: never overwrites a country an administrator has since edited).
            migrationBuilder.Sql(CaseManagement.Api.Data.CountryDefaults.InsertSql());

            // The Malaysian phone rule used to be a regex on the phone field, which would now reject every other country.
            // The same rule lives in the Countries table (Malaysia = 10 digits); remove only the untouched default.
            migrationBuilder.Sql(@"UPDATE ""FieldConfigurations"" SET ""ValidationRegex"" = NULL, ""ValidationMessage"" = NULL
                                    WHERE ""ApiField"" = 'phoneNumber' AND ""ValidationRegex"" = '^(\+?60)?([ -]*\d){10}$';");

            // A form needs a deterministic field order, so two fields of one form may not share a display order. The
            // application enforces that on every save. This constraint is the database backstop; it is DEFERRABLE so a
            // save that swaps two orders (3<->4) is checked at commit. It is only added when the existing data already
            // satisfies it: duplicates are NEVER silently renumbered, they are corrected by an administrator (the next
            // time such a field is edited the app asks for a unique order), and the constraint is added by a later release.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""FieldConfigurations"" GROUP BY ""ModuleKey"", ""SectionKey"", ""DisplayOrder"" HAVING COUNT(*) > 1) THEN
        RAISE NOTICE 'FieldConfigurations has duplicate display orders; UQ_FieldConfigurations_DisplayOrder was NOT created. Fix them in Configurable Settings.';
    ELSIF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'UQ_FieldConfigurations_DisplayOrder') THEN
        ALTER TABLE ""FieldConfigurations"" ADD CONSTRAINT ""UQ_FieldConfigurations_DisplayOrder""
            UNIQUE (""ModuleKey"", ""SectionKey"", ""DisplayOrder"") DEFERRABLE INITIALLY DEFERRED;
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""FieldConfigurations"" DROP CONSTRAINT IF EXISTS ""UQ_FieldConfigurations_DisplayOrder"";");

            migrationBuilder.DropTable(
                name: "Countries");

            migrationBuilder.DropColumn(
                name: "AllowAdd",
                table: "LookupTypes");

            migrationBuilder.DropColumn(
                name: "MaxValue",
                table: "FieldConfigurations");

            migrationBuilder.DropColumn(
                name: "MinValue",
                table: "FieldConfigurations");

            migrationBuilder.DropColumn(
                name: "PhoneCountryIso2",
                table: "Customers");

            migrationBuilder.AddColumn<bool>(
                name: "IsEditable",
                table: "FieldConfigurations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSensitive",
                table: "FieldConfigurations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
