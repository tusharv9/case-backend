using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseManagement.Api.Migrations
{
    /// <summary>
    /// Email intake no longer uses the per-domain case details (CaseType, DepartmentId, SubCategory, Priority, PreferredLanguage on
    /// EmailVerifiedDomains) or the EmailCaseDefaults table, so they leave the model. The DATABASE is deliberately left as it is:
    /// the shared Case DB is also used by another branch of case-backend whose code still reads those domain columns, and dropping
    /// them would break it. The columns are nullable and the table unused, so this code works with them present. Drop them in a
    /// later migration once every branch has moved on. Up and Down therefore change nothing.
    /// </summary>
    public partial class RemoveUnusedEmailCaseFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing: see the summary.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing: Up changed nothing.
        }
    }
}
