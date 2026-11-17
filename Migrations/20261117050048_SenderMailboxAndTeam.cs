using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SenderMailboxAndTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MailboxId",
                table: "EmailVerifiedSenders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TeamId",
                table: "EmailVerifiedSenders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedSenders_MailboxId",
                table: "EmailVerifiedSenders",
                column: "MailboxId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedSenders_TeamId",
                table: "EmailVerifiedSenders",
                column: "TeamId");

            migrationBuilder.AddForeignKey(
                name: "FK_EmailVerifiedSenders_EmailMailboxes_MailboxId",
                table: "EmailVerifiedSenders",
                column: "MailboxId",
                principalTable: "EmailMailboxes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_EmailVerifiedSenders_Teams_TeamId",
                table: "EmailVerifiedSenders",
                column: "TeamId",
                principalTable: "Teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmailVerifiedSenders_EmailMailboxes_MailboxId",
                table: "EmailVerifiedSenders");

            migrationBuilder.DropForeignKey(
                name: "FK_EmailVerifiedSenders_Teams_TeamId",
                table: "EmailVerifiedSenders");

            migrationBuilder.DropIndex(
                name: "IX_EmailVerifiedSenders_MailboxId",
                table: "EmailVerifiedSenders");

            migrationBuilder.DropIndex(
                name: "IX_EmailVerifiedSenders_TeamId",
                table: "EmailVerifiedSenders");

            migrationBuilder.DropColumn(
                name: "MailboxId",
                table: "EmailVerifiedSenders");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "EmailVerifiedSenders");
        }
    }
}
