using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>The database's own rule on who a case is for (CK_Cases_CustomerReference), against real PostgreSQL.</summary>
/// <remarks>
/// The in-memory provider every other test uses does not enforce check constraints, which is how organisation cases first shipped
/// while the database still refused them: the code saved, the database said no, and assigning an organisation's ticket failed with
/// an unexpected error. These tests run the real migrations and save the three kinds of case, plus the mixes the rule must refuse.
/// </remarks>
public class OrganisationCaseRuleTests
{
    private static async Task<(TempDatabase Db, Guid Team, Guid Owner, Guid Org, Guid Customer)> WorldAsync()
    {
        var db = await TempDatabase.CreateAsync();
        await using var ctx = db.NewContext();
        await ctx.Database.MigrateAsync();
        var owner = new User { Name = "Owner", Email = $"owner-{Guid.NewGuid():N}@x.test", Role = "Agent", IsActive = true, CreatedAt = DateTime.UtcNow };
        var team = new Team { Name = $"Team {Guid.NewGuid():N}"[..20], Code = Guid.NewGuid().ToString("N")[..8], IsActive = true, CreatedAt = DateTime.UtcNow };
        var org = new EmailOrganisation { Name = "ABC Financial Services", NameKey = "abc financial services", CreatedAt = DateTime.UtcNow };
        var customer = new Customer { FullName = "Rahul", NRIC = Guid.NewGuid().ToString("N")[..12], PhoneNumber = "+60" + Random.Shared.NextInt64(1000000000, 1999999999), CreatedAt = DateTime.UtcNow };
        ctx.AddRange(owner, team, org, customer);
        await ctx.SaveChangesAsync();
        return (db, team.Id, owner.Id, org.Id, customer.Id);
    }

    private static Case NewCase(Guid team, Guid owner) => new()
    {
        CaseNumber = "C-" + Guid.NewGuid().ToString("N")[..8], Title = "Loan query", CaseType = "Inquiry", Severity = "High",
        TeamId = team, OwnerId = owner, Status = CaseStatus.Open, SlaStartTime = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
        FirstResponseStatus = "Pending",
    };

    [PostgresFact]
    public async Task A_case_for_a_registered_organisation_is_accepted_by_the_database()
    {
        var (db, team, owner, org, _) = await WorldAsync();
        await using (db)
        {
            await using var ctx = db.NewContext();
            var c = NewCase(team, owner);
            (c.CustomerSource, c.OrganisationId) = (CustomerSources.Organisation, org);
            ctx.Cases.Add(c);

            await ctx.SaveChangesAsync();

            Assert.Equal(CustomerSources.Organisation, (await ctx.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id)).CustomerSource);
        }
    }

    [PostgresFact]
    public async Task An_organisation_case_that_also_names_a_person_customer_is_refused()
    {
        var (db, team, owner, org, customer) = await WorldAsync();
        await using (db)
        {
            await using var ctx = db.NewContext();
            var c = NewCase(team, owner);
            (c.CustomerSource, c.OrganisationId, c.CustomerId) = (CustomerSources.Organisation, org, customer);
            ctx.Cases.Add(c);

            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [PostgresFact]
    public async Task A_person_customers_case_is_still_accepted_and_one_without_the_person_is_still_refused()
    {
        var (db, team, owner, _, customer) = await WorldAsync();
        await using (db)
        {
            await using (var ok = db.NewContext())
            {
                var c = NewCase(team, owner);
                (c.CustomerSource, c.CustomerId) = (CustomerSources.Local, customer);
                ok.Cases.Add(c);
                await ok.SaveChangesAsync();
            }
            await using var bad = db.NewContext();
            var missing = NewCase(team, owner);
            missing.CustomerSource = CustomerSources.Local;
            bad.Cases.Add(missing);

            await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
        }
    }
}
