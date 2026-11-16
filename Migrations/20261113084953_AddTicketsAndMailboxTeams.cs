using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketsAndMailboxTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TicketId",
                table: "InboundEmails",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultCaseType",
                table: "EmailMailboxes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DefaultDepartmentId",
                table: "EmailMailboxes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultPreferredLanguage",
                table: "EmailMailboxes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultPriority",
                table: "EmailMailboxes",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultSubCategory",
                table: "EmailMailboxes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntakeMode",
                table: "EmailMailboxes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Tickets");

            migrationBuilder.CreateTable(
                name: "EmailMailboxTeams",
                columns: table => new
                {
                    MailboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMailboxTeams", x => new { x.MailboxId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_EmailMailboxTeams_EmailMailboxes_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailMailboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EmailMailboxTeams_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    TicketNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    MailboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    InboundEmailId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequesterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequesterEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    RequesterPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Priority = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CaseType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SenderVerification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SenderNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CloseReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    HandledById = table.Column<Guid>(type: "uuid", nullable: true),
                    HandledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tickets_EmailMailboxes_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailMailboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketAttachments_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_TicketId",
                table: "InboundEmails",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMailboxTeams_TeamId",
                table: "EmailMailboxTeams",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_TicketId",
                table: "TicketAttachments",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CaseId",
                table: "Tickets",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_InboundEmailId",
                table: "Tickets",
                column: "InboundEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_MailboxId_ReceivedAt",
                table: "Tickets",
                columns: new[] { "MailboxId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Sequence",
                table: "Tickets",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Status_ReceivedAt",
                table: "Tickets",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TicketNumber",
                table: "Tickets",
                column: "TicketNumber",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_InboundEmails_Tickets_TicketId",
                table: "InboundEmails",
                column: "TicketId",
                principalTable: "Tickets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InboundEmails_Tickets_TicketId",
                table: "InboundEmails");

            migrationBuilder.DropTable(
                name: "EmailMailboxTeams");

            migrationBuilder.DropTable(
                name: "TicketAttachments");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_InboundEmails_TicketId",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "TicketId",
                table: "InboundEmails");

            migrationBuilder.DropColumn(
                name: "DefaultCaseType",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "DefaultDepartmentId",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "DefaultPreferredLanguage",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "DefaultPriority",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "DefaultSubCategory",
                table: "EmailMailboxes");

            migrationBuilder.DropColumn(
                name: "IntakeMode",
                table: "EmailMailboxes");
        }
    }
}
