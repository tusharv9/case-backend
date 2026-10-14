using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CaseManagement.Tests;

// ===== Unit tests: no database, no server =====

public class PermissionProviderTests
{
    private static ConfiguredPermissionProvider Provider(HostIntegrationOptions? o = null) =>
        new(Options.Create(o ?? new HostIntegrationOptions()));

    private static HostPrincipal P(string[] roles, string[]? permissions = null) =>
        new("u1", "U", null, roles, permissions ?? Array.Empty<string>());

    [Fact]
    public void AuthenticatedUserWithNoRoles_GetsOnlyTheBaseline()
    {
        var granted = Provider().Resolve(P(Array.Empty<string>()));
        Assert.Equal(new[] { Permissions.CasesRead, Permissions.CustomersRead }.OrderBy(x => x), granted.OrderBy(x => x));
    }

    [Fact]
    public void RolesMatchExactly_NotBySubstring()
    {
        // The old code treated any role containing "Lead"/"Head"/"Supervisor" as that level.
        var granted = Provider().Resolve(P(new[] { "Team Leader", "Head Teller", "Supervisor Trainee" }));
        Assert.DoesNotContain(Permissions.MonitoringView, granted);
        Assert.DoesNotContain(Permissions.TeamsManage, granted);
        Assert.DoesNotContain(Permissions.Everything, granted);
    }

    [Fact]
    public void RoleMatchIsCaseInsensitive_AndPermissionsAccumulateAcrossRoles()
    {
        var granted = Provider().Resolve(P(new[] { "agent", "SUPERVISOR" }));
        Assert.Contains(Permissions.CasesWrite, granted);
        Assert.Contains(Permissions.TeamsManage, granted);
    }

    [Fact]
    public void ConfiguredRoleMapReplacesTheDefaults()
    {
        var options = new HostIntegrationOptions();
        options.RolePermissions["CX-Ops"] = new[] { Permissions.ConfigManage };
        var granted = Provider(options).Resolve(P(new[] { "CX-Ops", "Admin" }));

        Assert.Contains(Permissions.ConfigManage, granted);
        Assert.DoesNotContain(Permissions.Everything, granted); // "Admin" is no longer defined once a map is configured
    }

    [Fact]
    public void PermissionsAssertedDirectlyByTheHost_AreHonoured()
    {
        var granted = Provider().Resolve(P(Array.Empty<string>(), new[] { Permissions.AuditView }));
        Assert.Contains(Permissions.AuditView, granted);
    }
}

public class DirectoryClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (HttpHostUserDirectory Directory, StubHandler Handler) Create(Action<HostIntegrationOptions.DirectorySettings>? configure = null)
    {
        var handler = new StubHandler();
        var settings = new HostIntegrationOptions();
        settings.Directory.ApiKey = "secret";
        configure?.Invoke(settings.Directory);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://host.example/api/") };
        return (new HttpHostUserDirectory(http, Options.Create(settings), NullLogger<HttpHostUserDirectory>.Instance), handler);
    }

    [Fact]
    public async Task ListsUsersFromAWrappedResponse_WithConfiguredFieldNames_NumericIds_AndApiKey()
    {
        var (directory, handler) = Create(d =>
        {
            d.ItemsProperty = "data";
            d.Fields.Id = "userId";
            d.Fields.Name = "fullName";
            d.Fields.IsActive = "enabled";
        });
        handler.Respond = _ => Json("""{"data":[{"userId":42,"fullName":"Aisha","email":"a@x.test","role":"Agent","enabled":true},{"userId":"u-7","fullName":"Bo","enabled":false},{"fullName":"no id"}]}""");

        var users = await directory.ListAsync(CancellationToken.None);

        Assert.Equal(2, users.Count);                       // the user without an id is skipped
        Assert.Equal("42", users[0].ExternalUserId);         // numeric id becomes an opaque string
        Assert.Equal("Agent", users[0].Role);
        Assert.False(users[1].IsActive);
        Assert.Equal("secret", handler.Requests[0].Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task GetReturnsNull_ForAnUnknownUser_AndEscapesTheIdInThePath()
    {
        var (directory, handler) = Create();
        Assert.Null(await directory.GetAsync("a/b c", CancellationToken.None));
        Assert.Equal("https://host.example/api/users/a%2Fb%20c", handler.Requests.Single().RequestUri!.AbsoluteUri);
    }
}

// ===== Integration tests: the real application, a real database, real signed JWTs =====

/// <summary>Boots the real app in a chosen HostIntegration mode against a throw-away database.</summary>
public sealed class AppUnderTest : WebApplicationFactory<Program>
{
    public const string SigningKey = "unit-test-signing-key-which-is-long-enough-32b!";
    public const string Audience = "case-management";
    public const string Issuer = "https://host.example";

    private readonly Dictionary<string, string> _settings;

    public AppUnderTest(string connectionString, Dictionary<string, string>? extra = null)
    {
        _settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:DefaultConnection"] = connectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Database:Seed"] = "Bootstrap",
            ["Database:ConnectTimeoutSeconds"] = "20",
            ["HostIntegration:Mode"] = "Host",
            ["HostIntegration:Jwt:SymmetricKey"] = SigningKey,
            ["HostIntegration:Jwt:Audience"] = Audience,
            ["HostIntegration:Jwt:Issuer"] = Issuer,
            ["HostIntegration:ProvisionCacheSeconds"] = "0",
        };
        if (extra != null) foreach (var kv in extra) _settings[kv.Key] = kv.Value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        foreach (var kv in _settings) builder.UseSetting(kv.Key, kv.Value);
    }

    public async Task<HttpClient> ReadyClientAsync()
    {
        var client = CreateClient();
        for (var i = 0; i < 100; i++)
        {
            var response = await client.GetAsync("/ready");
            if (response.StatusCode == HttpStatusCode.OK) return client;
            await Task.Delay(100);
        }
        throw new TimeoutException("The application did not become ready.");
    }

    public static string Token(
        string subject, string? name = null, string[]? roles = null, string audience = Audience, string issuer = Issuer,
        string signingKey = SigningKey, TimeSpan? lifetime = null, IDictionary<string, object>? extraClaims = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (name != null) claims["name"] = name;
        if (roles != null) claims["role"] = roles;
        if (extraClaims != null) foreach (var kv in extraClaims) claims[kv.Key] = kv.Value;

        var life = lifetime ?? TimeSpan.FromHours(1);
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = life < TimeSpan.Zero ? now + life - TimeSpan.FromHours(1) : now.AddMinutes(-1),
            Expires = life < TimeSpan.Zero ? now + life : now + life,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256)
        });
    }

    public static HttpRequestMessage Get(string url, string? bearer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }
}

public class HostModeTests
{
    [PostgresFact]
    public async Task NoToken_Is401_AndTheStandaloneHeaderIsIgnoredInHostMode()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cases")).StatusCode);
        // Even the previously-open user list requires a token in Host mode.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);

        var withHeader = new HttpRequestMessage(HttpMethod.Get, "/api/cases");
        withHeader.Headers.Add("X-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(withHeader)).StatusCode);
    }

    [PostgresFact]
    public async Task InvalidTokens_AreAllRejected_WrongAudience_WrongKey_WrongIssuer_Expired_Garbage()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var bad = new[]
        {
            AppUnderTest.Token("u1", audience: "some-other-app"),
            AppUnderTest.Token("u1", signingKey: "a-completely-different-signing-key-32bytes!!"),
            AppUnderTest.Token("u1", issuer: "https://evil.example"),
            AppUnderTest.Token("u1", lifetime: TimeSpan.FromHours(-2)),
            "not.a.jwt"
        };

        foreach (var token in bad)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(AppUnderTest.Get("/api/users/me", token))).StatusCode);

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));   // nobody was provisioned
    }

    [PostgresFact]
    public async Task FirstValidRequest_ProvisionsTheHostUser_FromTheirClaims_WithoutADepartment()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var token = AppUnderTest.Token("host-42", name: "Aisha Rahman", roles: new[] { "Team Lead" },
            extraClaims: new Dictionary<string, object> { ["email"] = "aisha@host.example" });
        var response = await client.SendAsync(AppUnderTest.Get("/api/users/me", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        Assert.Equal("host-42", user.GetProperty("externalUserId").GetString());
        Assert.Equal("Aisha Rahman", user.GetProperty("name").GetString());
        Assert.Equal("Team Lead", user.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, user.GetProperty("departmentId").ValueKind);

        var permissions = doc.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(Permissions.MonitoringView, permissions);
        Assert.DoesNotContain(Permissions.ConfigManage, permissions);

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\" WHERE \"ExternalUserId\" = 'host-42' AND \"IsActive\""));

        // A second request reuses the same local user (no duplicate).
        await client.SendAsync(AppUnderTest.Get("/api/users/me", token));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));
    }

    [PostgresFact]
    public async Task Permissions_AreEnforcedOnTheServer_PerRole()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var agent = AppUnderTest.Token("agent-1", "An Agent", new[] { "Agent" });
        var supervisor = AppUnderTest.Token("sup-1", "A Supervisor", new[] { "Supervisor" });
        var admin = AppUnderTest.Token("adm-1", "An Admin", new[] { "Admin" });
        var nobody = AppUnderTest.Token("nobody-1", "No Role");

        async Task<HttpStatusCode> Status(string url, string token) => (await client.SendAsync(AppUnderTest.Get(url, token))).StatusCode;

        // Reading cases: baseline for everyone signed in.
        Assert.Equal(HttpStatusCode.OK, await Status("/api/cases?pageSize=1", nobody));
        // Monitoring needs monitoring.view: agent no, supervisor yes.
        Assert.Equal(HttpStatusCode.Forbidden, await Status("/api/team-monitoring/overview", agent));
        Assert.Equal(HttpStatusCode.OK, await Status("/api/team-monitoring/overview", supervisor));
        // Audit trail needs audit.view.
        Assert.Equal(HttpStatusCode.Forbidden, await Status("/api/audit", agent));
        Assert.Equal(HttpStatusCode.OK, await Status("/api/audit", supervisor));
        // Reading configuration is open to any signed-in user (every form needs it)...
        Assert.Equal(HttpStatusCode.OK, await Status("/api/sla-routing/configuration", agent));
        // ...but changing it needs config.manage (and creating a team needs teams.manage).
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/sla-routing/configuration") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", supervisor);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(put)).StatusCode);
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/teams") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(post)).StatusCode);
        // Routing rules are configuration: not even readable without config.manage.
        Assert.Equal(HttpStatusCode.Forbidden, await Status("/api/routing-rules", agent));
        Assert.Equal(HttpStatusCode.OK, await Status("/api/routing-rules", admin));
    }

    [PostgresFact]
    public async Task ADeactivatedUser_IsForbidden_AndNotListedForAssignment()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var leaver = AppUnderTest.Token("leaver-1", "Leaver", new[] { "Agent" });
        var stayer = AppUnderTest.Token("stayer-1", "Stayer", new[] { "Agent" });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(AppUnderTest.Get("/api/users/me", leaver))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(AppUnderTest.Get("/api/users/me", stayer))).StatusCode);

        await using (var ctx = db.NewContext())
            await ctx.Database.ExecuteSqlRawAsync("UPDATE \"Users\" SET \"IsActive\" = false WHERE \"ExternalUserId\" = 'leaver-1'");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AppUnderTest.Get("/api/users/me", leaver))).StatusCode);

        var list = await client.SendAsync(AppUnderTest.Get("/api/users", stayer));
        var names = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("Stayer", names);
        Assert.DoesNotContain("Leaver", names);
    }

    [PostgresFact]
    public async Task TheRemoteHasNoUserCreationEndpoint()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString);
        var client = await app.ReadyClientAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users") { Content = new StringContent("""{"name":"x","email":"x@x.test","role":"Admin"}""", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AppUnderTest.Token("adm-1", "Admin", new[] { "Admin" }));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(request)).StatusCode);
    }

    [PostgresFact]
    public async Task ClaimNamesAreConfiguration_NotCode()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using var app = new AppUnderTest(db.ConnectionString, new Dictionary<string, string>
        {
            ["HostIntegration:Claims:UserId"] = "uid",
            ["HostIntegration:Claims:Name"] = "display_name",
            ["HostIntegration:Claims:Roles"] = "groups",
            ["HostIntegration:RolePermissions:CX-Admins:0"] = "*",
        });
        var client = await app.ReadyClientAsync();

        var token = AppUnderTest.Token("ignored-sub", extraClaims: new Dictionary<string, object>
        {
            ["uid"] = "emp-9001", ["display_name"] = "Custom Claims", ["groups"] = new[] { "CX-Admins" }
        });
        var response = await client.SendAsync(AppUnderTest.Get("/api/users/me", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("emp-9001", body.GetProperty("user").GetProperty("externalUserId").GetString());
        Assert.Equal("Custom Claims", body.GetProperty("user").GetProperty("name").GetString());
        Assert.Contains("*", body.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }
}

public class StandaloneModeTests
{
    [PostgresFact]
    public async Task StandaloneMode_IsRefusedOutsideDevelopment_UnlessExplicitlyAllowed()
    {
        await using var db = await TempDatabase.CreateAsync();

        await using (var refused = new AppUnderTest(db.ConnectionString, new Dictionary<string, string>
        {
            ["HostIntegration:Mode"] = "Standalone",
            // appsettings.json allows Standalone in production for the demo deployment; this case checks the default rule.
            ["HostIntegration:AllowStandaloneInProduction"] = "false",
        }))
        {
            var ex = Assert.ThrowsAny<Exception>(() => refused.CreateClient());
            Assert.Contains("NO real authentication", ex.ToString());
        }

        await using var allowed = new AppUnderTest(db.ConnectionString, new Dictionary<string, string>
        {
            ["HostIntegration:Mode"] = "Standalone",
            ["HostIntegration:AllowStandaloneInProduction"] = "true",
            ["Database:Seed"] = "Development"
        });
        var client = await allowed.ReadyClientAsync();

        // The development user picker works without an identity...
        var users = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var firstId = JsonDocument.Parse(await users.Content.ReadAsStringAsync()).RootElement[0].GetProperty("id").GetString()!;

        // ...everything else needs the header, and an unknown id is not provisioned.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/cases")).StatusCode);
        var unknown = new HttpRequestMessage(HttpMethod.Get, "/api/cases"); unknown.Headers.Add("X-User-Id", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unknown)).StatusCode);
        var known = new HttpRequestMessage(HttpMethod.Get, "/api/users/me"); known.Headers.Add("X-User-Id", firstId);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(known)).StatusCode);
    }
}

public class DirectorySyncTests
{
    private sealed class FixedDirectory : IHostUserDirectory
    {
        public List<HostUserInfo> Users { get; } = new();
        public Task<HostUserInfo?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Users.FirstOrDefault(u => u.ExternalUserId == id));
        public Task<IReadOnlyList<HostUserInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUserInfo>>(Users.ToList());
    }

    [PostgresFact]
    public async Task Sync_CreatesUpdatesAndDeactivates_ButNeverDeletes()
    {
        await using var db = await TempDatabase.CreateAsync();
        await using (var setup = db.NewContext())
            await setup.Database.MigrateAsync();

        var directory = new FixedDirectory();
        directory.Users.AddRange(new[]
        {
            new HostUserInfo("1", "One", "one@x.test", "Agent", true),
            new HostUserInfo("2", "Two", "two@x.test", "Supervisor", true),
        });
        var options = Options.Create(new HostIntegrationOptions { Directory = { DeactivateMissing = true } });

        await using (var ctx = db.NewContext())
        {
            var (created, updated, deactivated) = await new HostUserSynchronizer(ctx, directory, options).SyncAllAsync(CancellationToken.None);
            Assert.Equal((2, 0, 0), (created, updated, deactivated));
        }

        directory.Users.Clear();
        directory.Users.Add(new HostUserInfo("1", "One Renamed", "one@x.test", "Team Lead", true)); // changed; user 2 vanished

        await using (var ctx = db.NewContext())
        {
            var (created, updated, deactivated) = await new HostUserSynchronizer(ctx, directory, options).SyncAllAsync(CancellationToken.None);
            Assert.Equal((0, 1, 1), (created, updated, deactivated));
        }

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM \"Users\""));   // nobody deleted
        Assert.Equal("One Renamed", await db.ScalarAsync<string>("SELECT \"Name\" FROM \"Users\" WHERE \"ExternalUserId\" = '1'"));
        Assert.False(await db.ScalarAsync<bool>("SELECT \"IsActive\" FROM \"Users\" WHERE \"ExternalUserId\" = '2'"));
    }
}
