using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class CaseEmailReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReplyMailboxId",
                table: "Cases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseEmailMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    MailboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToAddress = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    InReplyTo = table.Column<string>(type: "text", nullable: true),
                    References = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseEmailMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseEmailMessages_CaseEvents_CaseEventId",
                        column: x => x.CaseEventId,
                        principalTable: "CaseEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CaseEmailMessages_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaseEmailMessages_EmailMailboxes_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailMailboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cases_ReplyMailboxId",
                table: "Cases",
                column: "ReplyMailboxId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseEmailMessages_CaseEventId",
                table: "CaseEmailMessages",
                column: "CaseEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseEmailMessages_CaseId",
                table: "CaseEmailMessages",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseEmailMessages_MailboxId",
                table: "CaseEmailMessages",
                column: "MailboxId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseEmailMessages_MessageId",
                table: "CaseEmailMessages",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseEmailMessages_Status_NextAttemptAt",
                table: "CaseEmailMessages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Cases_EmailMailboxes_ReplyMailboxId",
                table: "Cases",
                column: "ReplyMailboxId",
                principalTable: "EmailMailboxes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Cases made from email reply from the mailbox the email came to (the first email of the case).
            migrationBuilder.Sql("""
                UPDATE "Cases" c
                SET "ReplyMailboxId" = e."MailboxId"
                FROM (SELECT DISTINCT ON ("CaseId") "CaseId", "MailboxId" FROM "InboundEmails"
                      WHERE "CaseId" IS NOT NULL AND "Status" IN ('CaseCreated', 'TicketCreated') ORDER BY "CaseId", "ReceivedAt") e
                WHERE c."Id" = e."CaseId" AND c."ReplyMailboxId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Cases_EmailMailboxes_ReplyMailboxId",
                table: "Cases");

            migrationBuilder.DropTable(
                name: "CaseEmailMessages");

            migrationBuilder.DropIndex(
                name: "IX_Cases_ReplyMailboxId",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ReplyMailboxId",
                table: "Cases");
        }
    }
}
