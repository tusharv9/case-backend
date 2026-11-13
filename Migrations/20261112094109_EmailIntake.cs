using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class EmailIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Username = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: true),
                    PasswordUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImapHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ImapPort = table.Column<int>(type: "integer", nullable: false),
                    ImapSecurity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpSecurity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SendAcknowledgement = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    NextSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: true),
                    LastUid = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMailboxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailVerifiedDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    OrganisationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IncludeSubdomains = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CaseType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PreferredLanguage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerifiedDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailVerifiedDomains_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboundEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MailboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageKey = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    InternetMessageId = table.Column<string>(type: "text", nullable: true),
                    InReplyTo = table.Column<string>(type: "text", nullable: true),
                    References = table.Column<string>(type: "text", nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: true),
                    ToList = table.Column<string>(type: "text", nullable: true),
                    CcList = table.Column<string>(type: "text", nullable: true),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: false),
                    Snippet = table.Column<string>(type: "text", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RawKey = table.Column<string>(type: "text", nullable: true),
                    AttachmentNames = table.Column<string>(type: "text", nullable: true),
                    AttachmentCount = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Verification = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VerificationNote = table.Column<string>(type: "text", nullable: true),
                    VerifiedDomainId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StatusNote = table.Column<string>(type: "text", nullable: true),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AckStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AckMessageId = table.Column<string>(type: "text", nullable: true),
                    AckSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AckError = table.Column<string>(type: "text", nullable: true),
                    AckAttempts = table.Column<int>(type: "integer", nullable: false),
                    AckNextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundEmails_Cases_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Cases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_InboundEmails_EmailMailboxes_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailMailboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboundEmails_EmailVerifiedDomains_VerifiedDomainId",
                        column: x => x.VerifiedDomainId,
                        principalTable: "EmailVerifiedDomains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMailboxes_Address",
                table: "EmailMailboxes",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedDomains_DepartmentId",
                table: "EmailVerifiedDomains",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedDomains_Domain",
                table: "EmailVerifiedDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_AckMessageId",
                table: "InboundEmails",
                column: "AckMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_CaseId",
                table: "InboundEmails",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_InternetMessageId",
                table: "InboundEmails",
                column: "InternetMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_MailboxId",
                table: "InboundEmails",
                column: "MailboxId");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_MessageKey",
                table: "InboundEmails",
                column: "MessageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_ReceivedAt",
                table: "InboundEmails",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_Status_ReceivedAt",
                table: "InboundEmails",
                columns: new[] { "Status", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundEmails_VerifiedDomainId",
                table: "InboundEmails",
                column: "VerifiedDomainId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundEmails");

            migrationBuilder.DropTable(
                name: "EmailMailboxes");

            migrationBuilder.DropTable(
                name: "EmailVerifiedDomains");
        }
    }
}
