using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class Customer360CustomerReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "CustomerId",
                table: "Cases",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "CustomerLookupKeyEncrypted",
                table: "Cases",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerLookupKeyHash",
                table: "Cases",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerLookupType",
                table: "Cases",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerSource",
                table: "Cases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Local");

            migrationBuilder.AddColumn<string>(
                name: "CustomerType",
                table: "Cases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalCustomerId",
                table: "Cases",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "Cases",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cases_CustomerLookupKeyHash",
                table: "Cases",
                column: "CustomerLookupKeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_Cases_CustomerType_ExternalCustomerId",
                table: "Cases",
                columns: new[] { "CustomerType", "ExternalCustomerId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cases_CustomerReference",
                table: "Cases",
                sql: "(\"CustomerSource\" = 'Local' AND \"CustomerId\" IS NOT NULL AND \"ExternalCustomerId\" IS NULL AND \"CustomerLookupKeyEncrypted\" IS NULL) OR (\"CustomerSource\" = 'Customer360' AND \"CustomerId\" IS NULL AND \"CustomerType\" IN ('Individual','NonIndividual') AND \"ExternalCustomerId\" IS NOT NULL AND \"CustomerLookupType\" IS NOT NULL AND \"CustomerLookupKeyEncrypted\" IS NOT NULL AND \"CustomerLookupKeyHash\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Cases_CustomerLookupKeyHash",
                table: "Cases");

            migrationBuilder.DropIndex(
                name: "IX_Cases_CustomerType_ExternalCustomerId",
                table: "Cases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cases_CustomerReference",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "CustomerLookupKeyEncrypted",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "CustomerLookupKeyHash",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "CustomerLookupType",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "CustomerSource",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "CustomerType",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ExternalCustomerId",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "Cases");

            migrationBuilder.AlterColumn<Guid>(
                name: "CustomerId",
                table: "Cases",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
