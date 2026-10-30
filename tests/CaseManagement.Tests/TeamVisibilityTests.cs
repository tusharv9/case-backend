using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaseManagement.Tests;

/// <summary>
/// Opening Case Management is not a licence to read every team's cases. A person sees the cases they hold or take part
/// in and those of the teams they lead or belong to — across several teams at once — and a supervisor with
/// cases:ViewAll sees all. This is enforced in the API on every list and every single-case request, and a change of
/// membership applies on the very next request.
/// </summary>
public class TeamVisibilityTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public AppUnderTest App = null!;
        public HttpClient Client = null!;
        public Guid Loans, Cards, Customer;
        public Guid LoansCase, CardsCase;
        public string Admin = AppUnderTest.Token("adm-1", "Admin User", new[] { "Admin" });
        public string Agent = AppUnderTest.Token("agt-1", "Agent User", new[] { "Agent" });
        public string Supervisor = AppUnderTest.Token("sup-1", "Supervisor User", new[] { "Supervisor" });

        public static async Task<Env> StartAsync()
        {
            var e = new Env { Db = await TempDatabase.CreateAsync() };
            using (var warm = new AppUnderTest(e.Db.ConnectionString)) await warm.ReadyClientAsync();
            await using (var ctx = e.Db.NewContext())
            {
                e.Loans = Guid.NewGuid(); e.Cards = Guid.NewGuid(); e.Customer = Guid.NewGuid();
                ctx.Departments.AddRange(
                    new Department { Id = e.Loans, Name = "Loans", Code = "LN", CreatedAt = DateTime.UtcNow },
                    new Department { Id = e.Cards, Name = "Cards", Code = "CD", CreatedAt = DateTime.UtcNow });
                // Each team serves its own business department (cases are created for the team, as older callers do).
                var lending = Guid.NewGuid(); var cardServices = Guid.NewGuid();
                ctx.OrgDepartments.AddRange(
                    new OrgDepartment { Id = lending, Name = "Lending", Code = "LEN", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new OrgDepartment { Id = cardServices, Name = "Card Services", Code = "CRD", IsActive = true, CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
                ctx.TeamDepartments.AddRange(
                    new TeamDepartment { TeamId = e.Loans, OrgDepartmentId = lending, IsDefault = true },
                    new TeamDepartment { TeamId = e.Cards, OrgDepartmentId = cardServices, IsDefault = true });
                ctx.AddRange(
                    new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = e.Loans, OrgDepartmentId = lending, Name = "Repayment", Code = "R", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = e.Cards, OrgDepartmentId = cardServices, Name = "Fraud", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new Customer { Id = e.Customer, FullName = "Siti Aminah", IdType = "Passport Number", Passport = "B12345678", PhoneNumber = "+60 12-111 2222", PreferredLanguage = "English", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
            }
            e.App = new AppUnderTest(e.Db.ConnectionString);
            e.Client = await e.App.ReadyClientAsync();

            // Everyone signs in once (Case Management learns them from their token).
            foreach (var t in new[] { e.Admin, e.Agent, e.Supervisor }) await e.Send(HttpMethod.Get, "/api/users/me", t);
            e.LoansCase = await e.CreateCaseAsync(e.Loans, "repayment");
            e.CardsCase = await e.CreateCaseAsync(e.Cards, "fraud");
            return e;
        }

        public async Task<Guid> CreateCaseAsync(Guid team, string sub)
        {
            var (status, body) = await Send(HttpMethod.Post, "/api/cases", Admin, new Dictionary<string, object?>
            {
                ["title"] = $"Case for {sub}", ["description"] = "d", ["customerId"] = Customer, ["departmentId"] = team,
                ["subcategory"] = sub, ["caseType"] = "complaint", ["preferredLanguage"] = "english",
                ["sourceChannel"] = "voice", ["preferredCommunicationChannel"] = "phone", ["severity"] = "Low",
            });
            Assert.Equal(HttpStatusCode.Created, status);
            var id = body.GetProperty("id").GetGuid();
            // Held by the admin, so only team membership (not ownership) can show it to the agent.
            var admin = await LocalIdAsync("adm-1");
            await using var ctx = Db.NewContext();
            await ctx.Cases.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerId, admin));
            return id;
        }

        public async Task<Guid> LocalIdAsync(string externalId)
        {
            await using var ctx = Db.NewContext();
            return await ctx.Users.Where(u => u.ExternalUserId == externalId).Select(u => u.Id).SingleAsync();
        }

        public async Task SetMembershipAsync(string externalId, Guid team, bool active)
        {
            var user = await LocalIdAsync(externalId);
            await using var ctx = Db.NewContext();
            var row = await ctx.TeamMembers.FirstOrDefaultAsync(m => m.UserId == user && m.DepartmentId == team);
            if (row == null) ctx.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), DepartmentId = team, UserId = user, IsActive = active, CreatedAt = DateTime.UtcNow });
            else row.IsActive = active;
            await ctx.SaveChangesAsync();
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, string token, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return (response.StatusCode, doc.RootElement.Clone());
        }

        public async Task<HashSet<Guid>> ListedAsync(string token)
        {
            var (_, body) = await Send(HttpMethod.Get, "/api/cases?pageSize=100", token);
            return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task An_agent_sees_only_their_teams_cases_in_lists_and_cannot_open_another_teams_case()
    {
        await using var env = await Env.StartAsync();
        await env.SetMembershipAsync("agt-1", env.Loans, active: true);

        var listed = await env.ListedAsync(env.Agent);
        var (own, _) = await env.Send(HttpMethod.Get, $"/api/cases/{env.LoansCase}", env.Agent);
        var (other, _) = await env.Send(HttpMethod.Get, $"/api/cases/{env.CardsCase}", env.Agent);
        var (changeOther, _) = await env.Send(HttpMethod.Post, $"/api/cases/{env.CardsCase}/notes", env.Agent, new { });   // refused before the body is even read
        var (_, dashboard) = await env.Send(HttpMethod.Get, "/api/dashboard", env.Agent);

        Assert.Contains(env.LoansCase, listed);
        Assert.DoesNotContain(env.CardsCase, listed);
        Assert.Equal(HttpStatusCode.OK, own);
        Assert.Equal(HttpStatusCode.NotFound, other);          // as if it did not exist
        Assert.Equal(HttpStatusCode.NotFound, changeOther);    // changes are refused the same way
        Assert.DoesNotContain(env.CardsCase.ToString(), dashboard.ToString());
    }

    [PostgresFact]
    public async Task A_member_of_two_teams_sees_both_and_leaving_one_takes_effect_on_the_next_request()
    {
        await using var env = await Env.StartAsync();
        await env.SetMembershipAsync("agt-1", env.Loans, active: true);
        await env.SetMembershipAsync("agt-1", env.Cards, active: true);

        var both = await env.ListedAsync(env.Agent);
        await env.SetMembershipAsync("agt-1", env.Cards, active: false);
        var afterLeaving = await env.ListedAsync(env.Agent);
        var (opened, _) = await env.Send(HttpMethod.Get, $"/api/cases/{env.CardsCase}", env.Agent);

        Assert.Contains(env.LoansCase, both);
        Assert.Contains(env.CardsCase, both);
        Assert.Contains(env.LoansCase, afterLeaving);
        Assert.DoesNotContain(env.CardsCase, afterLeaving);
        Assert.Equal(HttpStatusCode.NotFound, opened);
    }

    [PostgresFact]
    public async Task Holding_a_case_shows_it_whatever_the_team_and_ViewAll_shows_everything()
    {
        await using var env = await Env.StartAsync();
        var agent = await env.LocalIdAsync("agt-1");
        await using (var ctx = env.Db.NewContext())
            await ctx.Cases.Where(c => c.Id == env.CardsCase).ExecuteUpdateAsync(s => s.SetProperty(c => c.OwnerId, agent));

        var agentSees = await env.ListedAsync(env.Agent);
        var supervisorSees = await env.ListedAsync(env.Supervisor);

        Assert.Equal(new HashSet<Guid> { env.CardsCase }, agentSees);   // no team: only the case they hold
        Assert.Contains(env.LoansCase, supervisorSees);
        Assert.Contains(env.CardsCase, supervisorSees);
    }

    [PostgresFact]
    public async Task Team_Monitoring_counts_a_person_in_two_teams_in_each_team_but_once_overall()
    {
        await using var env = await Env.StartAsync();
        await env.SetMembershipAsync("agt-1", env.Loans, active: true);
        await env.SetMembershipAsync("agt-1", env.Cards, active: true);

        var (status, overview) = await env.Send(HttpMethod.Get, "/api/team-monitoring/overview", env.Supervisor);

        Assert.Equal(HttpStatusCode.OK, status);
        var teams = overview.GetProperty("teams").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        Assert.Equal(1, teams["Loans"].GetProperty("memberCount").GetInt32());
        Assert.Equal(1, teams["Cards"].GetProperty("memberCount").GetInt32());
        Assert.Equal(1, teams["Loans"].GetProperty("openCaseCount").GetInt32());
        Assert.Equal(1, overview.GetProperty("summary").GetProperty("totalAgentsCount").GetInt32());   // one person, not two
        Assert.Equal(teams.Count, overview.GetProperty("summary").GetProperty("teamsCount").GetInt32());

        // Leaving a team shows on the next refresh.
        await env.SetMembershipAsync("agt-1", env.Cards, active: false);
        var (_, after) = await env.Send(HttpMethod.Get, "/api/team-monitoring/overview", env.Supervisor);
        Assert.Equal(0, after.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Cards").GetProperty("memberCount").GetInt32());
    }

    [PostgresFact]
    public async Task Team_membership_grants_no_permission_of_its_own()
    {
        await using var env = await Env.StartAsync();
        var nobody = AppUnderTest.Token("nob-1", "No Role");
        await env.Send(HttpMethod.Get, "/api/users/me", nobody);
        await env.SetMembershipAsync("nob-1", env.Loans, active: true);

        var (status, _) = await env.Send(HttpMethod.Get, $"/api/cases/{env.LoansCase}", nobody);

        Assert.Equal(HttpStatusCode.Forbidden, status);   // a member without cases.read still reads nothing
    }
}

/// <summary>
/// Team members are chosen from the Host's own user directory, read with the person's token — every Host user, including
/// those who never opened Case Management — and only a reference is kept for those who join a team.
/// </summary>
public class HostUserDirectoryTests
{
    private sealed class FakeAuthService : HttpMessageHandler
    {
        public List<(string Path, string? Authorization)> Requests { get; } = new();
        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;
        public static readonly Guid Nadia = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        public static readonly Guid Former = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

        private static object User(Guid id, string name, bool active) => new
        {
            id, salutation = (string?)null, name, email = name.Split(' ')[0].ToLowerInvariant() + "@bank.test", phoneNumber = (string?)null,
            roleId = (Guid?)null, roleName = "Agent", isAdministrator = false, isActive = active, lastLoginAt = (DateTimeOffset?)null, authProvider = "Local",
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
            var path = request.RequestUri.AbsolutePath;
            HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (path == "/api/users")
                return Task.FromResult(ListStatus != HttpStatusCode.OK ? new HttpResponseMessage(ListStatus)
                    : Json(new { items = new[] { User(Nadia, "Nadia Ismail", true) }, total = 1, page = 1, pageSize = 25 }));
            if (path == $"/api/users/{Nadia}") return Task.FromResult(Json(User(Nadia, "Nadia Ismail", true)));
            if (path == $"/api/users/{Former}") return Task.FromResult(Json(User(Former, "Former Staff", false)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HostUserDirectoryClient Client(FakeAuthService fake)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer caller";
        return new HostUserDirectoryClient(new HttpClient(fake), new HttpContextAccessor { HttpContext = context },
            Options.Create(new AuthServiceOptions { BaseUrl = "http://auth.test" }), NullLogger<HostUserDirectoryClient>.Instance);
    }

    [Fact]
    public async Task The_directory_is_read_with_the_callers_token_active_users_only()
    {
        var fake = new FakeAuthService();

        var (users, total) = await Client(fake).SearchAsync("nad", activeOnly: true, 1, 25, CancellationToken.None);

        Assert.Equal(1, total);
        Assert.Equal("Nadia Ismail", Assert.Single(users).Name);
        Assert.Equal(("/api/users?page=1&pageSize=25&search=nad&isActive=true", "Bearer caller"), fake.Requests.Single());
    }

    [Fact]
    public async Task Someone_the_Host_does_not_let_list_users_is_told_what_access_they_need()
    {
        var fake = new FakeAuthService { ListStatus = HttpStatusCode.Forbidden };

        var error = await Assert.ThrowsAsync<HostUsersException>(() => Client(fake).SearchAsync(null, true, 1, 25, CancellationToken.None));

        Assert.Equal(403, error.Status);
        Assert.Contains("Settings → Users", error.Message);
    }

    [PostgresFact]
    public async Task A_Host_user_who_never_signed_in_can_be_added_and_a_deactivated_one_cannot()
    {
        await using var db = await TempDatabase.CreateAsync();
        var fake = new FakeAuthService();
        await using var app = new AppUnderTest(db.ConnectionString, new Dictionary<string, string> { ["AuthService:BaseUrl"] = "http://auth.test" });
        await using var factory = app.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient<HostUserDirectoryClient>().ConfigurePrimaryHttpMessageHandler(() => fake)));
        var client = factory.CreateClient();
        for (var i = 0; i < 100 && (await client.GetAsync("/ready")).StatusCode != HttpStatusCode.OK; i++) await Task.Delay(100);
        var admin = AppUnderTest.Token("adm-1", "Admin User", new[] { "Admin" });

        async Task<(HttpStatusCode, string)> Post(string url, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var (linked, body) = await Post("/api/host-users/link", new { hostUserIds = new[] { FakeAuthService.Nadia } });
        var (refused, _) = await Post("/api/host-users/link", new { hostUserIds = new[] { FakeAuthService.Former } });

        Assert.Equal(HttpStatusCode.OK, linked);
        Assert.Contains(FakeAuthService.Nadia.ToString(), body);
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        await using var ctx = db.NewContext();
        var nadia = await ctx.Users.SingleAsync(u => u.ExternalUserId == FakeAuthService.Nadia.ToString());
        Assert.Equal("Nadia Ismail", nadia.Name);
        Assert.False(await ctx.Users.AnyAsync(u => u.ExternalUserId == FakeAuthService.Former.ToString()));
    }
}
