using System.Security.Claims;
using System.Text.Json;
using CaseManagement.Api.Controllers;
using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Hybrid mode: one backend for Case Management inside the OmniConnect Host and for the standalone test site.
/// </summary>
/// <remarks>
/// The two must never leak into each other. A request with a Host token is judged by the token alone — a bad token is
/// refused, never quietly treated as a demo user — and only a request with no token may name a demo user, and only when
/// the deployment allows it. Demo users get the standalone permission set; Host users get what the Host grants.
/// </remarks>
public class HybridModeTests
{
    private static IOptions<HostIntegrationOptions> Options(bool allowDevHeader) =>
        Microsoft.Extensions.Options.Options.Create(new HostIntegrationOptions { Mode = HostIntegrationMode.Hybrid, HybridAllowsDevHeader = allowDevHeader });

    private static HybridIdentityResolver Resolver(bool allowDevHeader) =>
        new(new JwtIdentityResolver(Options(allowDevHeader)), new StandaloneIdentityResolver(), Options(allowDevHeader));

    private static HttpContext Request(string? bearer = null, string? demoUser = null, bool authenticated = false)
    {
        var context = new DefaultHttpContext();
        if (bearer != null) context.Request.Headers.Authorization = $"Bearer {bearer}";
        if (demoUser != null) context.Request.Headers[StandaloneIdentityResolver.HeaderName] = demoUser;
        if (authenticated) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "host-user-1"), new Claim("administrator", "true")], "Bearer"));
        return context;
    }

    [Fact]
    public async Task A_request_with_a_valid_Host_token_is_the_Host_user()
    {
        var principal = await Resolver(allowDevHeader: true).ResolveAsync(Request(bearer: "token", authenticated: true), CancellationToken.None);

        Assert.Equal("host-user-1", principal!.ExternalUserId);
        Assert.Equal(HostIdentitySource.HostToken, principal.Source);
    }

    [Fact]
    public async Task A_request_with_a_rejected_token_never_falls_back_to_the_demo_user_header()
    {
        var principal = await Resolver(allowDevHeader: true).ResolveAsync(Request(bearer: "bad", demoUser: "demo-1"), CancellationToken.None);

        Assert.Null(principal);
    }

    [Fact]
    public async Task A_request_without_a_token_may_name_a_demo_user_only_when_the_deployment_allows_it()
    {
        var allowed = await Resolver(allowDevHeader: true).ResolveAsync(Request(demoUser: "demo-1"), CancellationToken.None);
        var refused = await Resolver(allowDevHeader: false).ResolveAsync(Request(demoUser: "demo-1"), CancellationToken.None);

        Assert.Equal(HostIdentitySource.DevelopmentHeader, allowed!.Source);
        Assert.Null(refused);
    }

    [Fact]
    public void Demo_users_get_the_standalone_set_and_Host_users_what_the_Host_grants()
    {
        var provider = new HybridPermissionProvider(Options(allowDevHeader: true));

        var demo = provider.Resolve(new HostPrincipal("demo-1", null, null, [], []) { Source = HostIdentitySource.DevelopmentHeader });
        var outsider = provider.Resolve(new HostPrincipal("host-user-2", null, null, [], []));

        Assert.Contains(Permissions.Everything, demo);
        Assert.Empty(outsider);
    }

    [Theory]
    [InlineData(HostIntegrationMode.Standalone, false, "Standalone", false, true)]
    [InlineData(HostIntegrationMode.Host, true, "Host", true, false)]
    [InlineData(HostIntegrationMode.Hybrid, true, "Hybrid", true, true)]
    [InlineData(HostIntegrationMode.Hybrid, false, "Hybrid", true, false)]
    public void Sign_in_options_say_which_ways_of_signing_in_the_deployment_accepts(HostIntegrationMode mode, bool allowDevHeader, string name, bool hostTokens, bool demoUsers)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new HostIntegrationOptions { Mode = mode, HybridAllowsDevHeader = allowDevHeader });

        var result = (OkObjectResult)new SessionController(options, new HostingEnvironment { EnvironmentName = Environments.Production }).GetOptions();

        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(name, body.GetProperty("mode").GetString());
        Assert.Equal(hostTokens, body.GetProperty("hostTokens").GetBoolean());
        Assert.Equal(demoUsers, body.GetProperty("demoUsers").GetBoolean());
    }
}
