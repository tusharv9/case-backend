using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <summary>
    /// Verified domains get an organisation record (several domains, one organisation), a verification policy (entire domain or
    /// listed senders) with its sender list, and a "require a signature" switch; emails record their organisation, the listed
    /// sender they matched and whether they proved their domain (DKIM/DMARC). Existing domains stay "entire domain", so nothing
    /// behaves differently until an administrator changes a domain. Down removes the additions; OrganisationName is still there.
    /// </summary>
    public partial class EmailSendersAndOrganisations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthenticationDetail",
                table: "InboundEmails",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Authenticity",
                table: "InboundEmails",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<Guid>(
                name: "OrganisationId",
                table: "InboundEmails",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SenderAlias",
                table: "InboundEmails",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VerifiedSenderId",
                table: "InboundEmails",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "EmailVerifiedDomains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganisationId",
                table: "EmailVerifiedDomains",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "RequireAuthentication",
                table: "EmailVerifiedDomains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByUserId",
                table: "EmailVerifiedDomains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationPolicy",
                table: "EmailVerifiedDomains",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "EntireDomain");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "EmailMailboxes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByUserId",
                table: "EmailMailboxes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmailOrganisations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOrganisations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailVerifiedSenders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VerifiedDomainId = table.Column<Guid>(type: "uuid", nullable: false),
                    Address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerifiedSenders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailVerifiedSenders_EmailVerifiedDomains_VerifiedDomainId",
                        column: x => x.VerifiedDomainId,
                        principalTable: "EmailVerifiedDomains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_OrganisationId",
                table: "InboundEmails",
                column: "OrganisationId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_VerifiedSenderId",
                table: "InboundEmails",
                column: "VerifiedSenderId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedDomains_OrganisationId",
                table: "EmailVerifiedDomains",
                column: "OrganisationId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOrganisations_NameKey",
                table: "EmailOrganisations",
                column: "NameKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedSenders_Address",
                table: "EmailVerifiedSenders",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedSenders_VerifiedDomainId",
                table: "EmailVerifiedSenders",
                column: "VerifiedDomainId");

            // Existing data: one organisation per distinct organisation name (ignoring case and spacing), every domain linked to it,
            // and every email already filed under a domain filed under its organisation. Every existing domain keeps "entire domain".
            migrationBuilder.Sql(@"
INSERT INTO ""EmailOrganisations"" (""Id"", ""Name"", ""NameKey"", ""IsActive"", ""CreatedAt"")
SELECT gen_random_uuid(), min(btrim(""OrganisationName"")), lower(regexp_replace(btrim(""OrganisationName""), '\s+', ' ', 'g')), true, NOW()
FROM ""EmailVerifiedDomains""
GROUP BY lower(regexp_replace(btrim(""OrganisationName""), '\s+', ' ', 'g'));

UPDATE ""EmailVerifiedDomains"" d SET ""OrganisationId"" = o.""Id""
FROM ""EmailOrganisations"" o
WHERE o.""NameKey"" = lower(regexp_replace(btrim(d.""OrganisationName""), '\s+', ' ', 'g'));

UPDATE ""InboundEmails"" e SET ""OrganisationId"" = d.""OrganisationId""
FROM ""EmailVerifiedDomains"" d
WHERE e.""VerifiedDomainId"" = d.""Id"";
");

            migrationBuilder.AddForeignKey(
                name: "FK_EmailVerifiedDomains_EmailOrganisations_OrganisationId",
                table: "EmailVerifiedDomains",
                column: "OrganisationId",
                principalTable: "EmailOrganisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InboundEmails_EmailOrganisations_OrganisationId",
                table: "InboundEmails",
                column: "OrganisationId",
                principalTable: "EmailOrganisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_InboundEmails_EmailVerifiedSenders_VerifiedSenderId",
                table: "InboundEmails",
                column: "VerifiedSenderId",
                principalTable: "EmailVerifiedSenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmailVerifiedDomains_EmailOrganisations_OrganisationId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropForeignKey(
                name: "FK_InboundEmails_EmailOrganisations_OrganisationId",
                table: "InboundEmails");

            migrationBuilder.DropForeignKey(
                name: "FK_InboundEmails_EmailVerifiedSenders_VerifiedSenderId",
                table: "InboundEmails");

            migrationBuilder.DropTable(
                name: "EmailOrganisations");

            migrationBuilder.DropTable(
                name: "EmailVerifiedSenders");

            migrationBuilder.DropIndex(
                name: "IX_InboundEmails_OrganisationId",
                table: "InboundEmails");

            migrationBuilder.DropIndex(
                name: "IX_InboundEmails_VerifiedSenderId",
                table: "InboundEmails");

            migrationBuilder.DropIndex(
                name: "IX_EmailVerifiedDomains_OrganisationId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "AuthenticationDetail",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "Authenticity",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "SenderAlias",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "VerifiedSenderId",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "RequireAuthentication",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "VerificationPolicy",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "EmailMailboxes");
        }
    }
}
