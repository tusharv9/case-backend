using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class CaseContactEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Cases",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            // Cases made from email keep to email: replies go to the address the email came from, and both channels are Email.
            // From email tickets (the ticket's requester)…
            migrationBuilder.Sql("""
                UPDATE "Cases" c
                SET "ContactEmail" = lower(t."RequesterEmail"), "SourceChannel" = 'Email', "PreferredCommunicationChannel" = 'Email'
                FROM "Tickets" t
                WHERE c."SourceTicketId" = t."Id" AND t."Source" = 'Email' AND t."RequesterEmail" <> '' AND c."ContactEmail" IS NULL;
                """);
            // …and every other case an email opened: directly (before tickets existed), or through a ticket converted before cases
            // kept the ticket's link. The first email's sender.
            migrationBuilder.Sql("""
                UPDATE "Cases" c
                SET "ContactEmail" = lower(e."FromAddress"), "SourceChannel" = 'Email', "PreferredCommunicationChannel" = 'Email'
                FROM (SELECT DISTINCT ON ("CaseId") "CaseId", "FromAddress" FROM "InboundEmails"
                      WHERE "Status" IN ('CaseCreated', 'TicketCreated') AND "CaseId" IS NOT NULL AND "FromAddress" <> '' ORDER BY "CaseId", "ReceivedAt") e
                WHERE c."Id" = e."CaseId" AND c."ContactEmail" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "Cases");
        }
    }
}
