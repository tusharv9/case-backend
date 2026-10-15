using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.Controllers;
using CaseManagement.Api.HostIntegration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CaseManagement.Tests;

/// <summary>
/// Case Management as an OmniConnect remote: it accepts the tokens AuthService actually issues and
/// nothing else, reads permissions only in the Host's own shape, and tells the Host what can be granted.
/// </summary>
/// <remarks>
/// AuthService signs RS256 tokens with one platform key and writes <c>perms</c> as a JSON array of
/// <c>remote.{appKey}.{module}:{Capability}</c> strings plus a separate <c>administrator</c> flag. Getting any of
/// that wrong fails in one of two directions — everybody is refused, or anybody with an OmniConnect login is
/// let in — and neither shows up until a real Host signs someone in. These tests pin the contract down.
/// </remarks>
public class HostPermissionCatalogTests
{
    private static ConfiguredPermissionProvider Provider() => new(Options.Create(new HostIntegrationOptions()));

    private static HostPrincipal P(string[] perms, bool admin = false) =>
        new("u1", "U", null, Array.Empty<string>(), perms, admin);

    [Fact]
    public void Every_capability_in_the_catalog_grants_exactly_its_internal_permission()
    {
        foreach (var module in HostPermissionCatalog.Modules)
        foreach (var capability in module.Capabilities)
        {
            var host = HostPermissionCatalog.HostPermission("casemanagement", module.Key, capability.Key);

            var granted = HostPermissionCatalog.Translate(new[] { host }, "casemanagement").ToList();

            Assert.Equal(new[] { capability.Grants }, granted);
        }
    }

    [Fact]
    public void Every_internal_permission_can_be_granted_by_some_Host_capability()
    {
        var grantable = HostPermissionCatalog.Modules.SelectMany(m => m.Capabilities).Select(c => c.Grants).ToHashSet();

        Assert.All(Permissions.All, p => Assert.Contains(p, grantable));
    }

    [Fact]
    public void Another_apps_capabilities_and_near_misses_grant_nothing()
    {
        var granted = Provider().Resolve(P(new[]
        {
            "remote.leadmanagement.cases:View",       // same module name, other app
            "remote.casemanagement.cases:View:extra",
            "remote.casemanagement.cases",
            "remote.casemanagement.*",
            "host.settings.users:View",
        }));

        Assert.Empty(granted);
    }

    [Fact]
    public void A_Host_administrator_gets_everything_but_unmasked_customer_data()
    {
        var granted = Provider().Resolve(P(Array.Empty<string>(), admin: true));

        Assert.Contains(Permissions.Everything, granted);
        Assert.DoesNotContain(Permissions.PiiUnmask, granted);   // masking is checked by name, never via "*"
    }

    [Fact]
    public void The_app_key_is_configuration()
    {
        var options = new HostIntegrationOptions { AppKey = "cases2" };
        var provider = new ConfiguredPermissionProvider(Options.Create(options));

        Assert.Contains(Permissions.CasesRead, provider.Resolve(P(new[] { "remote.cases2.cases:View" })));
        Assert.Empty(provider.Resolve(P(new[] { "remote.casemanagement.cases:View" })));
    }

    [Fact]
    public void The_discovery_document_offers_every_capability_and_each_nav_row_names_one_of_its_modules()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(PermissionsController.Build()));
        var modules = doc.RootElement.GetProperty("modules").EnumerateArray().ToList();

        Assert.Equal(HostPermissionCatalog.Modules.Count, modules.Count);
        foreach (var module in modules)
        {
            var capabilities = module.GetProperty("capabilities").EnumerateArray().Select(c => c.GetProperty("key").GetString()).ToHashSet();
            foreach (var nav in module.GetProperty("nav").EnumerateArray())
            {
                Assert.Contains(nav.GetProperty("requiredCapability").GetString(), capabilities);
                Assert.Equal(nav.GetProperty("key").GetString(), nav.GetProperty("routeSegment").GetString());
            }
        }

        var navKeys = modules.SelectMany(m => m.GetProperty("nav").EnumerateArray()).Select(n => n.GetProperty("key").GetString()).ToList();
        Assert.Equal(navKeys.Count, navKeys.Distinct().Count());   // a route segment opens exactly one page
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_platform_public_key_is_read_as_PEM_or_as_a_one_line_env_value(bool oneLine)
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();
        var configured = oneLine ? pem.Replace("\n", "\\n") : pem;

        using var loaded = RsaPublicKey.Load(configured);

        Assert.Equal(rsa.ExportSubjectPublicKeyInfo(), loaded.ExportSubjectPublicKeyInfo());
    }
}

/// <summary>The real application in Host mode, validating RS256 tokens shaped exactly like AuthService's.</summary>
public class PlatformTokenTests
{
    private const string Issuer = "omniconnect-auth-service";
    private const string Audience = "omniconnect-host";

    private static readonly RSA PlatformKey = RSA.Create(2048);

    private static AppUnderTest App(string connectionString) => new(connectionString, new Dictionary<string, string>
    {
        ["Jwt:SigningKeyPublic"] = PlatformKey.ExportSubjectPublicKeyInfoPem().Replace("\n", "\\n"),
        ["Jwt:Issuer"] = Issuer,
        ["Jwt:Audience"] = Audience,
        // Make the platform settings the only ones: HostIntegration:Jwt is left blank, as in a real deployment.
        ["HostIntegration:Jwt:Issuer"] = "",
        ["HostIntegration:Jwt:Audience"] = "",
    });

    /// <summary>A token as AuthService's JwtTokenService writes it.</summary>
    private static string PlatformToken(string subject, string[] perms, bool administrator = false, RSA? key = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = $"{subject}@bank.test",
                ["name"] = "Nur Aisyah",
                ["administrator"] = administrator ? "true" : "false",
                ["perms"] = JsonSerializer.Serialize(perms),
                ["role"] = Guid.NewGuid().ToString(),   // AuthService's role claim is a role id, never a name
            },
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? PlatformKey), SecurityAlgorithms.RsaSha256),
        });
    }

    private static async Task<List<string>> PermissionsOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [PostgresFact]
    public async Task A_platform_token_signs_the_user_in_with_exactly_the_capabilities_it_grants()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = App(db.ConnectionString);
        var client = await app.ReadyClientAsync();
        var subject = Guid.NewGuid().ToString();
        var token = PlatformToken(subject, new[] { "remote.casemanagement.cases:View", "remote.customer360.profile:View" });

        var me = await client.SendAsync(AppUnderTest.Get("/api/users/me", token));

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(new[] { Permissions.CasesRead }, await PermissionsOf(me));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            $"SELECT count(*) FROM \"Users\" WHERE \"ExternalUserId\" = '{subject}' AND \"Role\" <> ''"));   // a role id is never shown as a role
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM \"Users\" WHERE \"ExternalUserId\" = '{subject}'"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(AppUnderTest.Get("/api/cases", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AppUnderTest.Get("/api/team-monitoring/overview", token))).StatusCode);
    }

    [PostgresFact]
    public async Task An_OmniConnect_user_without_Case_Management_capabilities_is_refused()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = App(db.ConnectionString);
        var client = await app.ReadyClientAsync();
        var token = PlatformToken(Guid.NewGuid().ToString(), new[] { "remote.customer360.profile:View" });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AppUnderTest.Get("/api/cases", token))).StatusCode);
    }

    [PostgresFact]
    public async Task The_administrator_claim_counts_only_when_it_is_exactly_true()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = App(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var admin = await client.SendAsync(AppUnderTest.Get("/api/users/me", PlatformToken(Guid.NewGuid().ToString(), Array.Empty<string>(), administrator: true)));
        var notAdmin = await client.SendAsync(AppUnderTest.Get("/api/users/me", PlatformToken(Guid.NewGuid().ToString(), Array.Empty<string>())));

        Assert.Contains(Permissions.Everything, await PermissionsOf(admin));
        Assert.Empty(await PermissionsOf(notAdmin));
    }

    [PostgresFact]
    public async Task Tokens_not_signed_with_the_platform_key_are_rejected_including_HMAC_tokens()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = App(db.ConnectionString);
        var client = await app.ReadyClientAsync();
        using var otherKey = RSA.Create(2048);

        var forged = PlatformToken("u1", new[] { "remote.casemanagement.cases:View" }, key: otherKey);
        // The fixture still configures an HMAC test key; once the platform key is set it must not be accepted.
        var hmac = AppUnderTest.Token("u1", audience: Audience, issuer: Issuer);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(AppUnderTest.Get("/api/users/me", forged))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(AppUnderTest.Get("/api/users/me", hmac))).StatusCode);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));
    }

    [PostgresFact]
    public async Task The_API_answers_under_its_platform_path_base_and_without_it()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = App(db.ConnectionString);
        var client = await app.ReadyClientAsync();
        var token = PlatformToken(Guid.NewGuid().ToString(), new[] { "remote.casemanagement.cases:View" });

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(AppUnderTest.Get("/api/casemanagement-service/api/users/me", token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(AppUnderTest.Get("/api/users/me", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/casemanagement-service/api/cases")).StatusCode);
    }

    [PostgresFact]
    public async Task Permission_discovery_needs_no_token_and_is_served_under_the_path_base()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = App(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var response = await client.GetAsync("/api/casemanagement-service/permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("cases", doc.RootElement.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("key").GetString()));
    }
}
