using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class TicketClaimsPresenceAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_CaseId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_InboundEmailId",
                table: "Tickets");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSeenAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimedById",
                table: "Tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedUntil",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseCreationRequests",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseCreationRequests", x => x.Key);
                    table.ForeignKey(
                        name: "FK_CaseCreationRequests_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CaseId",
                table: "Tickets",
                column: "CaseId",
                unique: true,
                filter: "\"CaseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ClaimedUntil",
                table: "Tickets",
                column: "ClaimedUntil");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_InboundEmailId",
                table: "Tickets",
                column: "InboundEmailId",
                unique: true,
                filter: "\"InboundEmailId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_SourceTicketId",
                table: "Cases",
                column: "SourceTicketId",
                unique: true,
                filter: "\"SourceTicketId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseCreationRequests_CaseId",
                table: "CaseCreationRequests",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseCreationRequests_CreatedAt",
                table: "CaseCreationRequests",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseCreationRequests");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_CaseId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ClaimedUntil",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_InboundEmailId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Cases_SourceTicketId",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ClaimedById",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ClaimedUntil",
                table: "Tickets");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CaseId",
                table: "Tickets",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_InboundEmailId",
                table: "Tickets",
                column: "InboundEmailId");
        }
    }
}
