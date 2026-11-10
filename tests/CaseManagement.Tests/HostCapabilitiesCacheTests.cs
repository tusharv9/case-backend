using System.Net;
using CaseManagement.Api.HostIntegration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Permissions read from AuthService are reused for a few seconds, and requests that arrive together share one question.
/// </summary>
/// <remarks>
/// Asking AuthService took about 0.6 s against the hosted database, and every Case Management request waited for it — a
/// page that loads five requests asked five times. The reuse must stay safe: only a granted answer is kept, so a user who is
/// unknown, deactivated, or could not be checked is asked about again on the very next request, and 0 seconds turns reuse off.
/// </remarks>
public class HostCapabilitiesCacheTests
{
    private sealed class CountingHandler(Func<HttpResponseMessage> respond, TimeSpan? delay = null) : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (delay.HasValue) await Task.Delay(delay.Value, ct);
            return respond();
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string Granted = """{"isKnown":true,"isActive":true,"isAdministrator":false,"permissions":["remote.casemanagement.cases:View"]}""";

    private static HostCapabilitiesClient Client(CountingHandler handler, int seconds = 15) => new(
        new HttpClient(handler),
        Options.Create(new AuthServiceOptions { BaseUrl = "http://auth.test", InternalApiKey = "key", CapabilityCacheSeconds = seconds }),
        NullLogger<HostCapabilitiesClient>.Instance,
        new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task A_granted_answer_is_reused_instead_of_asking_AuthService_again()
    {
        var handler = new CountingHandler(() => Json(Granted));
        var client = Client(handler);
        var user = Guid.NewGuid().ToString();

        await client.ForUserAsync(user, CancellationToken.None);
        var second = await client.ForUserAsync(user, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Contains("remote.casemanagement.cases:View", second.Permissions);
    }

    [Fact]
    public async Task Requests_that_arrive_together_share_one_question()
    {
        var handler = new CountingHandler(() => Json(Granted), TimeSpan.FromMilliseconds(100));
        var client = Client(handler);
        var user = Guid.NewGuid().ToString();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.ForUserAsync(user, CancellationToken.None)));

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_refusal_is_never_reused_so_the_next_request_asks_again()
    {
        var handler = new CountingHandler(() => Json("""{"isKnown":true,"isActive":false,"isAdministrator":false,"permissions":[]}"""));
        var client = Client(handler);
        var user = Guid.NewGuid().ToString();

        await Assert.ThrowsAsync<HostAccessException>(() => client.ForUserAsync(user, CancellationToken.None));
        await Assert.ThrowsAsync<HostAccessException>(() => client.ForUserAsync(user, CancellationToken.None));

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Zero_seconds_asks_AuthService_on_every_request()
    {
        var handler = new CountingHandler(() => Json(Granted));
        var client = Client(handler, seconds: 0);
        var user = Guid.NewGuid().ToString();

        await client.ForUserAsync(user, CancellationToken.None);
        await client.ForUserAsync(user, CancellationToken.None);

        Assert.Equal(2, handler.Calls);
    }
}
