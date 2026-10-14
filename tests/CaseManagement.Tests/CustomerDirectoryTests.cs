using System.Diagnostics;
using CaseManagement.Api.Controllers;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>The customer list at scale (real PostgreSQL) and the per-user preferences store.</summary>
public class CustomerDirectoryTests
{
    [PostgresFact]
    public async Task TenThousandCustomers_PageQuickly_SortOnAnyWhitelistedColumn_AndNeverRepeatOrSkipARow()
    {
        await using var temp = await TempDatabase.CreateAsync();
        await using var db = temp.NewContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO ""Customers"" (""Id"", ""FullName"", ""NRIC"", ""IdType"", ""PhoneNumber"", ""PhoneCountryIso2"", ""Email"", ""Branch"", ""PreferredLanguage"", ""CreatedAt"")
            SELECT gen_random_uuid(), 'Customer ' || lpad(g::text, 5, '0'), lpad(g::text, 12, '0'), 'NRIC Number', '+60 ' || (1000000000 + g)::text, 'MY',
                   'user' || g || '@example.com', 'Branch ' || (g % 7), CASE WHEN g % 3 = 0 THEN 'English' ELSE 'Chinese' END, NOW() - (g || ' minutes')::interval
              FROM generate_series(1, 10000) g;
            ANALYZE ""Customers"";");

        var repo = new CustomerRepository(db);

        // Paging cost does not grow with the table: one page of 50 out of 10,000, with the total.
        var timer = Stopwatch.StartNew();
        var first = await repo.GetPaginatedAsync(null, null, null, 1, 50);
        timer.Stop();
        Assert.Equal(10_000, first.TotalCount);
        Assert.Equal(50, first.Items.Count());
        Assert.True(timer.ElapsedMilliseconds < 3000, $"first page took {timer.ElapsedMilliseconds} ms");

        // Every whitelisted column sorts, ascending and descending, and walking all pages visits each row exactly once (stable tie-break).
        foreach (var column in CustomerRepository.SortableColumns)
        foreach (var descending in new[] { false, true })
        {
            var seen = new HashSet<Guid>();
            for (var page = 1; page <= 20; page++)   // 20 pages x 500 = every row; 'branch' / 'language' have thousands of ties
            {
                var result = await repo.GetPaginatedAsync(null, null, null, page, 500, column, descending);
                foreach (var item in result.Items) Assert.True(seen.Add(item.Id), $"{column} {(descending ? "desc" : "asc")} repeated a row on page {page}");
            }
            Assert.Equal(10_000, seen.Count);
        }

        var newest = await repo.GetPaginatedAsync(null, null, null, 1, 1, "createdAt", descending: true);
        Assert.Equal("Customer 00001", newest.Items.Single().FullName);          // created 1 minute ago
        Assert.NotEqual(default, newest.Items.Single().CreatedAt);
        Assert.Equal("user1@example.com", newest.Items.Single().Email);

        // Search covers email, and filters still apply with the sort.
        var byEmail = await repo.GetPaginatedAsync("user9999@", null, null, 1, 10);
        Assert.Single(byEmail.Items);
        var filtered = await repo.GetPaginatedAsync(null, "English", "Branch 3", 1, 10, "name");
        Assert.All(filtered.Items, c => { Assert.Equal("English", c.PreferredLanguage); Assert.Equal("Branch 3", c.Branch); });
    }

    // ------------------------------------------------------------------------------------------------- preferences

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static PreferencesController As(AppDbContext db, Guid userId) => new(db)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Items = { ["UserId"] = userId } } }
    };

    [Fact]
    public async Task APreference_IsSavedPerUser_ReadBack_AndNeverVisibleToAnotherUser()
    {
        using var db = NewDb();
        var alice = Guid.NewGuid(); var bob = Guid.NewGuid();

        await As(db, alice).Put("customer360.view-mode", new PreferenceValueDto { Value = "list" }, default);
        await As(db, alice).Put("customer360.view-mode", new PreferenceValueDto { Value = "card" }, default);   // update, not a second row
        await As(db, alice).Put("customer360.view-mode", new PreferenceValueDto { Value = "list" }, default);

        Assert.Equal(1, await db.UserPreferences.CountAsync());
        Assert.Equal("list", ValueOf(await As(db, alice).Get("customer360.view-mode", default)));
        Assert.Null(ValueOf(await As(db, bob).Get("customer360.view-mode", default)));
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("../etc")]
    [InlineData("")]
    public async Task InvalidKeys_AreRejected(string key)
    {
        using var db = NewDb();
        Assert.IsType<BadRequestObjectResult>(await As(db, Guid.NewGuid()).Put(key.Length == 0 ? " " : key, new PreferenceValueDto { Value = "x" }, default));
    }

    [Fact]
    public async Task OversizedValues_AndTooManyKeys_AreRejected()
    {
        using var db = NewDb();
        var user = Guid.NewGuid();
        Assert.IsType<BadRequestObjectResult>(await As(db, user).Put("k", new PreferenceValueDto { Value = new string('x', 4001) }, default));
        for (var i = 0; i < 100; i++) Assert.IsType<OkObjectResult>(await As(db, user).Put($"k{i}", new PreferenceValueDto { Value = "v" }, default));
        Assert.IsType<BadRequestObjectResult>(await As(db, user).Put("one-too-many", new PreferenceValueDto { Value = "v" }, default));
    }

    private static string? ValueOf(IActionResult result) =>
        (string?)((OkObjectResult)result).Value!.GetType().GetProperty("value")!.GetValue(((OkObjectResult)result).Value);
}
