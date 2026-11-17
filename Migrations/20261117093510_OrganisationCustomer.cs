using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganisationId",
                table: "Tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VerifiedSenderId",
                table: "Tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContactSenderId",
                table: "Cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganisationId",
                table: "Cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OrganisationId",
                table: "Tickets",
                column: "OrganisationId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_VerifiedSenderId",
                table: "Tickets",
                column: "VerifiedSenderId");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_ContactSenderId",
                table: "Cases",
                column: "ContactSenderId");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_OrganisationId",
                table: "Cases",
                column: "OrganisationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Cases_EmailOrganisations_OrganisationId",
                table: "Cases",
                column: "OrganisationId",
                principalTable: "EmailOrganisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Cases_EmailVerifiedSenders_ContactSenderId",
                table: "Cases",
                column: "ContactSenderId",
                principalTable: "EmailVerifiedSenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_EmailOrganisations_OrganisationId",
                table: "Tickets",
                column: "OrganisationId",
                principalTable: "EmailOrganisations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_EmailVerifiedSenders_VerifiedSenderId",
                table: "Tickets",
                column: "VerifiedSenderId",
                principalTable: "EmailVerifiedSenders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Tickets from registered senders are for their organisation: those already waiting become organisation cases when
            // assigned. Matched by the requester's address against today's registered, active senders, since emails verified by a
            // whole domain (before only registered senders were verified) never recorded a sender. Existing cases keep their customer.
            migrationBuilder.Sql("""
                UPDATE "Tickets" t
                SET "OrganisationId" = d."OrganisationId", "VerifiedSenderId" = s."Id"
                FROM "EmailVerifiedSenders" s
                JOIN "EmailVerifiedDomains" d ON d."Id" = s."VerifiedDomainId"
                WHERE t."Source" = 'Email' AND t."SenderVerification" = 'Verified' AND t."VerifiedSenderId" IS NULL
                  AND s."Address" = lower(t."RequesterEmail") AND s."IsActive" AND d."IsActive";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cases_EmailOrganisations_OrganisationId",
                table: "Cases");

            migrationBuilder.DropForeignKey(
                name: "FK_Cases_EmailVerifiedSenders_ContactSenderId",
                table: "Cases");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_EmailOrganisations_OrganisationId",
                table: "Tickets");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_EmailVerifiedSenders_VerifiedSenderId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_OrganisationId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_VerifiedSenderId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Cases_ContactSenderId",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_Cases_OrganisationId",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "VerifiedSenderId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ContactSenderId",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "OrganisationId",
                table: "Cases");
        }
    }
}
