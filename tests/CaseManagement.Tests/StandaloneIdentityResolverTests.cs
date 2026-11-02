using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Who the standalone (no Host) app signs in as.
/// </summary>
/// <remarks>
/// The standalone user picker sends a user's own id, as <c>GET /api/users</c> lists it. A user first seen through the
/// Host keeps the Host's id as its external id, and the two differ — so on a database shared with a Host deployment,
/// every listed user was refused with 401 and the standalone app (the test deployment) could not start.
/// </remarks>
public class StandaloneIdentityResolverTests : IDisposable
{
    private readonly AppDbContext db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"standalone-{Guid.NewGuid()}").Options);

    public void Dispose()
    {
        db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static HttpContext Calling(string id)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[StandaloneIdentityResolver.HeaderName] = id;
        return context;
    }

    [Fact]
    public async Task A_user_first_seen_through_the_Host_signs_in_by_the_id_the_user_list_shows()
    {
        var user = new User { Name = "Super Admin", ExternalUserId = "6647af14-95a6-4dfd-ab76-c5c627195dff" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var principal = await new StandaloneIdentityResolver(db).ResolveAsync(Calling(user.Id.ToString()), CancellationToken.None);

        Assert.Equal("6647af14-95a6-4dfd-ab76-c5c627195dff", principal!.ExternalUserId);
    }

    [Fact]
    public async Task An_id_no_local_user_has_is_passed_on_unchanged()
    {
        var principal = await new StandaloneIdentityResolver(db).ResolveAsync(Calling("seed-user-1"), CancellationToken.None);

        Assert.Equal("seed-user-1", principal!.ExternalUserId);
    }

    [Fact]
    public async Task No_header_is_no_caller()
    {
        Assert.Null(await new StandaloneIdentityResolver(db).ResolveAsync(new DefaultHttpContext(), CancellationToken.None));
    }
}
