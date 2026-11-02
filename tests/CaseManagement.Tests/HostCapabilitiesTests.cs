using System.Net;
using System.Security.Claims;
using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Permissions asked of AuthService instead of read from the token.
/// </summary>
/// <remarks>
/// The Host's token no longer carries <c>perms</c> or <c>administrator</c>. Read from the token alone, every caller —
/// a Super Admin included — held no Case Management permission and saw "No access to Case Management". These pin that
/// AuthService's live answer is what counts, that an administrator gets everything, and that not being able to ask is
/// an outage (503), never a quiet "no access".
/// </remarks>
public class HostCapabilitiesTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpContext SignedIn(params Claim[] extra)
    {
        var claims = new List<Claim> { new("sub", "11111111-1111-1111-1111-111111111111"), new("name", "Super Admin") };
        claims.AddRange(extra);
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
    }

    private static (JwtIdentityResolver Resolver, StubHandler Handler) Resolver(Func<HttpRequestMessage, HttpResponseMessage> respond, string? key = "case-key")
    {
        var handler = new StubHandler(respond);
        var client = new HostCapabilitiesClient(
            new HttpClient(handler),
            Options.Create(new AuthServiceOptions { BaseUrl = "http://auth.test", InternalApiKey = key }),
            NullLogger<HostCapabilitiesClient>.Instance);
        return (new JwtIdentityResolver(Options.Create(new HostIntegrationOptions()), client), handler);
    }

    [Fact]
    public async Task An_administrator_by_AuthServices_answer_gets_every_permission_though_the_token_carries_none()
    {
        var (resolver, handler) = Resolver(_ => Json("""{"isKnown":true,"isActive":true,"isAdministrator":true,"permissions":[],"fineCapabilities":[]}"""));

        var principal = await resolver.ResolveAsync(SignedIn(), CancellationToken.None);

        Assert.True(principal!.IsAdministrator);
        Assert.Contains(Permissions.Everything, new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions())).Resolve(principal));
        Assert.Equal("http://auth.test/internal/capabilities/11111111-1111-1111-1111-111111111111", handler.Last!.RequestUri!.ToString());
        Assert.Equal("case-key", handler.Last.Headers.GetValues("X-Internal-Api-Key").Single());
    }

    [Fact]
    public async Task Granted_capabilities_come_from_AuthService_and_translate_to_case_permissions()
    {
        var (resolver, _) = Resolver(_ => Json("""{"isKnown":true,"isActive":true,"isAdministrator":false,"permissions":["remote.casemanagement.cases:View"]}"""));

        var principal = await resolver.ResolveAsync(SignedIn(new Claim("perms", "[\"remote.casemanagement.settings:Manage\"]")), CancellationToken.None);

        var permissions = new ConfiguredPermissionProvider(Options.Create(new HostIntegrationOptions { AppKey = "casemanagement" })).Resolve(principal!);
        Assert.Contains(Permissions.CasesRead, permissions);
        Assert.DoesNotContain(Permissions.ConfigManage, permissions);
    }

    [Theory]
    [InlineData("""{"isKnown":false,"isActive":false,"isAdministrator":false}""", 401)]
    [InlineData("""{"isKnown":true,"isActive":false,"isAdministrator":true}""", 403)]
    public async Task An_unknown_or_deactivated_user_is_refused(string body, int status)
    {
        var (resolver, _) = Resolver(_ => Json(body));

        var ex = await Assert.ThrowsAsync<HostAccessException>(() => resolver.ResolveAsync(SignedIn(), CancellationToken.None));

        Assert.Equal(status, ex.Status);
    }

    [Fact]
    public async Task AuthService_not_answering_is_an_outage_not_a_lack_of_permission()
    {
        var (resolver, _) = Resolver(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HostAccessException>(() => resolver.ResolveAsync(SignedIn(), CancellationToken.None));

        Assert.Equal(503, ex.Status);
    }

    [Fact]
    public async Task Without_an_internal_key_the_tokens_own_claims_are_read_as_before()
    {
        var (resolver, handler) = Resolver(_ => throw new InvalidOperationException("must not be called"), key: null);

        var principal = await resolver.ResolveAsync(SignedIn(new Claim("administrator", "true")), CancellationToken.None);

        Assert.True(principal!.IsAdministrator);
        Assert.Null(handler.Last);
    }
}
