using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <inheritdoc />
    public partial class OrganisationCaseCustomerRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Cases_CustomerReference",
                table: "Cases");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cases_CustomerReference",
                table: "Cases",
                sql: "(\"CustomerSource\" = 'Local' AND \"CustomerId\" IS NOT NULL AND \"ExternalCustomerId\" IS NULL AND \"CustomerLookupKeyEncrypted\" IS NULL) OR (\"CustomerSource\" = 'Customer360' AND \"CustomerId\" IS NULL AND \"CustomerType\" IN ('Individual','NonIndividual') AND \"ExternalCustomerId\" IS NOT NULL AND \"CustomerLookupType\" IS NOT NULL AND \"CustomerLookupKeyEncrypted\" IS NOT NULL AND \"CustomerLookupKeyHash\" IS NOT NULL) OR (\"CustomerSource\" = 'Organisation' AND \"CustomerId\" IS NULL AND \"ExternalCustomerId\" IS NULL AND \"CustomerLookupKeyEncrypted\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Cases_CustomerReference",
                table: "Cases");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cases_CustomerReference",
                table: "Cases",
                sql: "(\"CustomerSource\" = 'Local' AND \"CustomerId\" IS NOT NULL AND \"ExternalCustomerId\" IS NULL AND \"CustomerLookupKeyEncrypted\" IS NULL) OR (\"CustomerSource\" = 'Customer360' AND \"CustomerId\" IS NULL AND \"CustomerType\" IN ('Individual','NonIndividual') AND \"ExternalCustomerId\" IS NOT NULL AND \"CustomerLookupType\" IS NOT NULL AND \"CustomerLookupKeyEncrypted\" IS NOT NULL AND \"CustomerLookupKeyHash\" IS NOT NULL)");
        }
    }
}
