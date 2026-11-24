using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class PauseHistoryAndMeetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "StatusEnteredAt",
                table: "Cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MeetingId",
                table: "CaseEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarIcs",
                table: "CaseEmailMessages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CalendarMethod",
                table: "CaseEmailMessages",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseMeetings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Agenda = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    JoinUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderMeetingId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OrganizerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttendeeEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    AttendeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IcsUid = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    CancelReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseMeetings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseMeetings_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaseMeetings_Users_OrganizerUserId",
                        column: x => x.OrganizerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseSlaPauses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatusKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PausedMinutes = table.Column<int>(type: "integer", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LimitReachedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseSlaPauses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseSlaPauses_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvents_MeetingId",
                table: "CaseEvents",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseMeetings_CaseId_StartUtc",
                table: "CaseMeetings",
                columns: new[] { "CaseId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseMeetings_OrganizerUserId",
                table: "CaseMeetings",
                column: "OrganizerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseMeetings_StartUtc",
                table: "CaseMeetings",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CaseSlaPauses_CaseId_StartedAt",
                table: "CaseSlaPauses",
                columns: new[] { "CaseId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseSlaPauses_EndedAt",
                table: "CaseSlaPauses",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_CaseEvents_CaseMeetings_MeetingId",
                table: "CaseEvents",
                column: "MeetingId",
                principalTable: "CaseMeetings",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
            // Cases already paused today get their open pause row, so their pause history and any status pause limit apply.
            migrationBuilder.Sql("""
                INSERT INTO "CaseSlaPauses" ("Id", "CaseId", "StatusKey", "StartedAt")
                SELECT gen_random_uuid(), c."Id", COALESCE(c."StatusKey", c."Status"), c."SlaPausedAt"
                FROM "Cases" c
                WHERE c."SlaPausedAt" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "CaseSlaPauses" p WHERE p."CaseId" = c."Id" AND p."EndedAt" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CaseEvents_CaseMeetings_MeetingId",
                table: "CaseEvents");

            migrationBuilder.DropTable(
                name: "CaseMeetings");

            migrationBuilder.DropTable(
                name: "CaseSlaPauses");

            migrationBuilder.DropIndex(
                name: "IX_CaseEvents_MeetingId",
                table: "CaseEvents");

            migrationBuilder.DropColumn(
                name: "StatusEnteredAt",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "MeetingId",
                table: "CaseEvents");

            migrationBuilder.DropColumn(
                name: "CalendarIcs",
                table: "CaseEmailMessages");

            migrationBuilder.DropColumn(
                name: "CalendarMethod",
                table: "CaseEmailMessages");
        }
    }
}
