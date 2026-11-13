using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <summary>
    /// Removes what email intake no longer uses: the per-domain case details (CaseType, DepartmentId, SubCategory, Priority,
    /// PreferredLanguage on EmailVerifiedDomains) and the EmailCaseDefaults table. Email cases take their details from Case
    /// Settings. Down recreates the columns (empty) and the table.
    /// </summary>
    public partial class RemoveUnusedEmailCaseFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmailVerifiedDomains_Departments_DepartmentId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropTable(
                name: "EmailCaseDefaults");

            migrationBuilder.DropIndex(
                name: "IX_EmailVerifiedDomains_DepartmentId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "CaseType",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "EmailVerifiedDomains");

            migrationBuilder.DropColumn(
                name: "SubCategory",
                table: "EmailVerifiedDomains");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CaseType",
                table: "EmailVerifiedDomains",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "EmailVerifiedDomains",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "EmailVerifiedDomains",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "EmailVerifiedDomains",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubCategory",
                table: "EmailVerifiedDomains",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EmailCaseDefaults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreferredLanguage = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Priority = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailCaseDefaults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailCaseDefaults_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifiedDomains_DepartmentId",
                table: "EmailVerifiedDomains",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailCaseDefaults_DepartmentId",
                table: "EmailCaseDefaults",
                column: "DepartmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_EmailVerifiedDomains_Departments_DepartmentId",
                table: "EmailVerifiedDomains",
                column: "DepartmentId",
                principalTable: "Departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
