using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class SharedFileStoreAndEmailLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InboundEmailId",
                table: "CaseEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_InboundEmailId",
                table: "CaseEvents",
                column: "InboundEmailId");

            migrationBuilder.AddForeignKey(
                name: "FK_CaseEvents_InboundEmails_InboundEmailId",
                table: "CaseEvents",
                column: "InboundEmailId",
                principalTable: "InboundEmails",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CaseEvents_InboundEmails_InboundEmailId",
                table: "CaseEvents");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropIndex(
                name: "IX_CaseEvents_InboundEmailId",
                table: "CaseEvents");

            migrationBuilder.DropColumn(
                name: "InboundEmailId",
                table: "CaseEvents");
        }
    }
}
