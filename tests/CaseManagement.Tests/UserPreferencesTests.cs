using CaseManagement.Api.Controllers;
using CaseManagement.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>The per-user preferences store: what one person saves is theirs alone, and bad keys or sizes are refused.</summary>
public class UserPreferencesTests
{
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
