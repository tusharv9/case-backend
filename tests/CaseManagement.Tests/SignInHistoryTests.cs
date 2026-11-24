using System.Net;
using System.Text;
using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// A person's sign-ins on Team Monitoring, read from the platform's Audit Service.
/// </summary>
/// <remarks>
/// Worth testing because the trail moved (AuthService's <c>api/audit-logs</c> is gone, the Audit Service serves it), and because
/// Login Security added its own refusal keys: a refused sign-in on a locked account must never be shown as "Signed in".
/// </remarks>
public class SignInHistoryTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static string Row(string action, string result) =>
        $$"""{"id":"{{Guid.NewGuid()}}","occurredAt":"2026-11-24T04:53:43Z","actorName":"Ann","action":"{{action}}","result":"{{result}}"}""";

    [Fact]
    public async Task Sign_ins_come_from_the_Audit_Service_and_a_refusal_is_never_shown_as_signed_in()
    {
        var handler = new StubHandler($$"""{"items":[{{Row("auth.login_refused_locked", "Failure")}},{{Row("auth.login_succeeded", "Success")}},{{Row("auth.logout", "Success")}}],"total":3,"page":1,"pageSize":10}""");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AuditService:BaseUrl"] = "http://audit.test" }).Build();
        var client = new HostSignInHistoryClient(new HttpClient(handler), new HttpContextAccessor(), Options.Create(new AuthServiceOptions { BaseUrl = "http://auth.test" }),
            NullLogger<HostSignInHistoryClient>.Instance, configuration);

        var page = await client.ListAsync(Guid.NewGuid().ToString(), Guid.NewGuid(), null, null, 1, 10, default);

        Assert.Equal("audit.test", handler.Asked!.Host);
        Assert.Equal(new[] { "Sign-in failed", "Signed in", "Signed out" }, page.Items.Select(i => i.ActionLabel));
        Assert.Contains("account locked", page.Items.First().Description);
    }
}
