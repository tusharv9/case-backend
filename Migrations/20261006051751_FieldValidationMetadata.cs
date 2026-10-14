using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class FieldValidationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystemRequired",
                table: "FieldConfigurations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ValidationMessage",
                table: "FieldConfigurations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseCustomAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldKey = table.Column<string>(type: "text", nullable: false),
                    FieldValue = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseCustomAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseCustomAttributes_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseCustomAttributes_CaseId_FieldKey",
                table: "CaseCustomAttributes",
                columns: new[] { "CaseId", "FieldKey" },
                unique: true);

            // ---- Data: bring EXISTING configuration in line with the new metadata --------------------------

            // Lock the fields the system needs; carry the previously hard-coded phone rule into configuration.
            migrationBuilder.Sql(CaseManagement.Api.Data.FieldMetadataDefaults.ApplySql);

            // "Source channel" (how a case arrived) used to be a hard-coded list of three values in code. It becomes a
            // real, editable list, seeded from the matching values of the existing channel list so nothing the
            // administrator configured is lost. COMMUNICATION_CHANNEL stays the list of PREFERRED channels, as the
            // Settings screen already described it.
            migrationBuilder.Sql(@"
                INSERT INTO ""LookupTypes"" (""Id"", ""Code"", ""Name"", ""Description"", ""CreatedAt"")
                SELECT gen_random_uuid(), 'SOURCE_CHANNEL', 'Source Channel', 'Channels through which cases arrive (Create Case form and filters)', NOW()
                WHERE NOT EXISTS (SELECT 1 FROM ""LookupTypes"" WHERE ""Code"" = 'SOURCE_CHANNEL')
                  -- Only for databases that already hold the old channel list. A brand-new database must stay EMPTY here:
                  -- the seeder decides what to create from whether any lookup types exist.
                  AND EXISTS (SELECT 1 FROM ""LookupTypes"" WHERE ""Code"" = 'COMMUNICATION_CHANNEL');

                INSERT INTO ""LookupValues"" (""Id"", ""LookupTypeId"", ""TypeCode"", ""Value"", ""Label"", ""DisplayOrder"", ""IsActive"", ""CreatedAt"")
                SELECT gen_random_uuid(), (SELECT ""Id"" FROM ""LookupTypes"" WHERE ""Code"" = 'SOURCE_CHANNEL'), 'SOURCE_CHANNEL',
                       v.""Value"", v.""Label"", v.""DisplayOrder"", v.""IsActive"", NOW()
                FROM ""LookupValues"" v
                WHERE v.""TypeCode"" = 'COMMUNICATION_CHANNEL'
                  AND v.""Value"" IN ('Voice', 'Email', 'WhatsApp', 'SMS', 'Branch', 'Web Chat', 'Social')
                  AND NOT EXISTS (SELECT 1 FROM ""LookupValues"" x WHERE x.""TypeCode"" = 'SOURCE_CHANNEL' AND x.""Value"" = v.""Value"");

                UPDATE ""FieldConfigurations"" SET ""FieldType"" = 'Dropdown', ""LookupTypeCode"" = 'SOURCE_CHANNEL'
                 WHERE ""ModuleKey"" = 'CaseManagement' AND ""SectionKey"" = 'CreateCase' AND ""ApiField"" = 'sourceChannel';
                UPDATE ""FieldConfigurations"" SET ""FieldType"" = 'Dropdown', ""LookupTypeCode"" = 'COMMUNICATION_CHANNEL'
                 WHERE ""ModuleKey"" = 'CaseManagement' AND ""SectionKey"" = 'CreateCase'
                   AND ""ApiField"" IN ('preferredCommunicationChannel', 'communicationChannel');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseCustomAttributes");

            migrationBuilder.DropColumn(
                name: "IsSystemRequired",
                table: "FieldConfigurations");

            migrationBuilder.DropColumn(
                name: "ValidationMessage",
                table: "FieldConfigurations");
        }
    }
}
