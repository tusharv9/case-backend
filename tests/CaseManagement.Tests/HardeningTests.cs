using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.Extensions;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

public class OriginPatternTests
{
    [Theory]
    [InlineData("https://*.example.com", "https://app.example.com", true)]
    [InlineData("https://*.example.com", "https://a.b.example.com", true)]
    [InlineData("https://*.example.com", "https://example.com", false)]            // the bare domain is an exact origin, not a pattern match
    [InlineData("https://*.example.com", "http://app.example.com", false)]        // scheme must match
    [InlineData("https://*.example.com", "https://app.example.com.evil.test", false)]
    [InlineData("https://*.example.com", "https://evilexample.com", false)]       // not a subdomain
    [InlineData("https://*.example.com:8443", "https://app.example.com:8443", true)]
    [InlineData("https://*.example.com:8443", "https://app.example.com", false)]
    public void Matching(string pattern, string origin, bool expected)
    {
        var p = OriginPattern.TryParse(pattern);
        Assert.NotNull(p);
        Assert.Equal(expected, p!.Matches(new Uri(origin)));
    }

    [Theory]
    [InlineData("*.example.com")]          // no scheme
    [InlineData("https://*")]              // everything
    [InlineData("https://*.com")]          // a whole TLD
    [InlineData("https://app.example.com")] // not a wildcard: belongs in AllowedOrigins
    [InlineData("ftp://*.example.com")]
    [InlineData("https://*.*.example.com")]
    public void UnsafeOrUnsupportedPatternsAreRejected(string pattern) => Assert.Null(OriginPattern.TryParse(pattern));
}

/// <summary>The HTTP-level safety net: error shape, correlation ids, response headers, CORS and rate limiting.</summary>
public class HardeningTests
{
    private static readonly string Admin = AppUnderTest.Token("adm-1", "Admin User", new[] { "Admin" });

    private static async Task<(AppUnderTest App, HttpClient Client, TempDatabase Db)> StartAsync(Dictionary<string, string>? extra = null)
    {
        var db = await TempDatabase.CreateAsync();
        var app = new AppUnderTest(db.ConnectionString, extra);
        return (app, await app.ReadyClientAsync(), db);
    }

    private static HttpRequestMessage Req(string url, HttpMethod? method = null, string? token = null)
    {
        var r = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        if (token != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    [PostgresFact]
    public async Task Errors_HaveOneShape_ClassifiedByType_AndCarryACorrelationId()
    {
        var (app, client, db) = await StartAsync();
        await using var _a = app; await using var _d = db; using var _c = client;

        // Unknown case -> 404 (a KeyNotFoundException), not a 400 chosen by reading the message.
        var missing = await client.SendAsync(Req($"/api/cases/{Guid.NewGuid()}/attachments", token: Admin));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Case not found", body.GetProperty("error").GetString());
        var correlation = body.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrEmpty(correlation));
        Assert.Equal(correlation, missing.Headers.GetValues("X-Correlation-Id").Single());

        // A bad value -> 400 with the same shape.
        var bad = await client.SendAsync(Req("/api/dashboard/summary?dateRange=whenever", token: Admin));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True(JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.TryGetProperty("correlationId", out _));
    }

    [PostgresFact]
    public async Task ACorrelationIdFromTheCaller_IsUsedOnlyIfItIsSane()
    {
        var (app, client, db) = await StartAsync();
        await using var _a = app; await using var _d = db; using var _c = client;

        var good = Req("/health"); good.Headers.Add("X-Correlation-Id", "trace-1234567890");
        Assert.Equal("trace-1234567890", (await client.SendAsync(good)).Headers.GetValues("X-Correlation-Id").Single());

        var forged = Req("/health"); forged.Headers.Add("X-Correlation-Id", "x y<script>");   // would be written to logs verbatim
        var echoed = (await client.SendAsync(forged)).Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEqual("x y<script>", echoed);
        Assert.Matches("^[a-f0-9]{32}$", echoed);
    }

    [PostgresFact]
    public async Task EveryResponse_CarriesDefensiveHeaders_AndApiDataIsNeverCached()
    {
        var (app, client, db) = await StartAsync();
        await using var _a = app; await using var _d = db; using var _c = client;

        var api = await client.SendAsync(Req("/api/cases?pageSize=1", token: Admin));
        Assert.Equal("nosniff", api.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", api.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", api.Headers.GetValues("Referrer-Policy").Single());
        Assert.StartsWith("default-src 'none'", api.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", api.Headers.GetValues("Cache-Control").Single());

        // Even the unauthenticated, rejected request gets them.
        var denied = await client.SendAsync(Req("/api/cases"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("nosniff", denied.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [PostgresFact]
    public async Task Cors_AllowsOnlyConfiguredOrigins_AndNeverCredentials()
    {
        var (app, client, db) = await StartAsync(new() { ["AllowedOrigins"] = "https://app.example.com", ["Security:AllowedOriginPatterns"] = "https://*.tenant.example.org" });
        await using var _a = app; await using var _d = db; using var _c = client;

        async Task<(bool Allowed, bool Credentials)> Preflight(string origin)
        {
            var r = Req("/api/cases", HttpMethod.Options);
            r.Headers.Add("Origin", origin);
            r.Headers.Add("Access-Control-Request-Method", "GET");
            r.Headers.Add("Access-Control-Request-Headers", "authorization");
            var res = await client.SendAsync(r);
            return (res.Headers.TryGetValues("Access-Control-Allow-Origin", out var o) && o.Single() == origin,
                    res.Headers.Contains("Access-Control-Allow-Credentials"));
        }

        var exact = await Preflight("https://app.example.com");
        Assert.True(exact.Allowed);
        Assert.False(exact.Credentials);                                              // identity is in headers, never cookies

        Assert.True((await Preflight("https://eu.tenant.example.org")).Allowed);      // an explicitly configured pattern
        Assert.False((await Preflight("https://my-preview.vercel.app")).Allowed);     // hosting-provider previews are NOT allowed by default
        Assert.False((await Preflight("https://evil.example.com")).Allowed);
        Assert.False((await Preflight("http://localhost:3000")).Allowed);             // localhost is for Development only
    }

    [PostgresFact]
    public async Task RateLimit_StopsARunawayCaller_WithRetryAfter_AndSparesOthers()
    {
        var (app, client, db) = await StartAsync(new() { ["Security:RateLimit:RequestsPerMinute"] = "5" });
        await using var _a = app; await using var _d = db; using var _c = client;

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await client.SendAsync(Req("/api/cases?pageSize=1", token: Admin))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(HttpStatusCode.OK, statuses[0]);

        var limited = await client.SendAsync(Req("/api/cases?pageSize=1", token: Admin));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Contains("slow down", await limited.Content.ReadAsStringAsync());

        // Another caller has their own allowance; health probes are never limited.
        var other = AppUnderTest.Token("agt-9", "Another Agent", new[] { "Agent" });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req("/api/cases?pageSize=1", token: other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req("/health"))).StatusCode);
    }

    [PostgresFact]
    public async Task JsonResponses_AreCompressed_WhenTheClientAsks()
    {
        var (app, client, db) = await StartAsync();
        await using var _a = app; await using var _d = db; using var _c = client;

        var r = Req("/api/dashboard/filters", token: Admin);
        r.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var res = await client.SendAsync(r);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("gzip", res.Content.Headers.ContentEncoding);
    }
}
