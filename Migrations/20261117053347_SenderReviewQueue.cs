using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SenderReviewQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MatchKind",
                table: "InboundEmails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchReason",
                table: "InboundEmails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewDecision",
                table: "InboundEmails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "InboundEmails",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewStartedAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAt",
                table: "InboundEmails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedById",
                table: "InboundEmails",
                type: "uuid",
                nullable: true);

            // Only registered senders are verified from now on; a domain never verifies everyone at it. A domain that did ("entire
            // domain") keeps the people who already wrote to us from it: each address that had a verified email is registered as a
            // sender, so their next email is verified exactly as before. Everyone else at the domain is reviewed first.
            migrationBuilder.Sql("""
                INSERT INTO "EmailVerifiedSenders" ("Id", "VerifiedDomainId", "Address", "DisplayName", "Description", "IsActive", "CreatedAt")
                SELECT gen_random_uuid(), x."VerifiedDomainId", x."Address", x."DisplayName",
                       'Registered automatically: wrote to us before only registered senders were verified.', TRUE, now() AT TIME ZONE 'utc'
                FROM (
                    SELECT DISTINCT ON (lower(e."FromAddress"))
                           e."VerifiedDomainId", lower(e."FromAddress") AS "Address",
                           left(coalesce(nullif(btrim(e."FromName"), ''), split_part(e."FromAddress", '@', 1)), 100) AS "DisplayName"
                    FROM "InboundEmails" e
                    JOIN "EmailVerifiedDomains" d ON d."Id" = e."VerifiedDomainId"
                    WHERE d."VerificationPolicy" = 'EntireDomain' AND e."Verification" = 'Verified' AND e."FromAddress" <> ''
                    ORDER BY lower(e."FromAddress"), e."ReceivedAt" DESC
                ) x
                WHERE NOT EXISTS (SELECT 1 FROM "EmailVerifiedSenders" s WHERE s."Address" = x."Address");
                """);
            migrationBuilder.Sql("""
                UPDATE "EmailVerifiedDomains" SET "VerificationPolicy" = 'SpecificSenders', "UpdatedAt" = now() AT TIME ZONE 'utc'
                WHERE "VerificationPolicy" = 'EntireDomain';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The domains stay "specific senders" and the registered senders stay: undoing that would verify strangers again.
            migrationBuilder.DropColumn(
                name: "MatchKind",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "MatchReason",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewDecision",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewStartedAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "ReviewedById",
                table: "InboundEmails");
        }
    }
}
