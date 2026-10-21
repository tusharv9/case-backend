using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.Controllers;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace CaseManagement.Tests;

/// <summary>
/// Host-issued permissions decide what a person can do in Case Management, and the API — not the screen — enforces it.
/// </summary>
/// <remarks>
/// Hiding a button is a courtesy. These tests pin the server side down: which endpoints can be called without any Case
/// Management permission (exactly two, on purpose), and that each case action needs its own capability exactly as the
/// Host's Role editor offers it.
/// </remarks>
public class PermissionEnforcementTests
{
    [Fact]
    public void Only_permission_discovery_who_am_I_and_the_Customer_360_handoff_redeem_need_no_Case_Management_permission()
    {
        var open = typeof(PermissionsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Where(m => m.IsDefined(typeof(NoPermissionRequiredAttribute)) || t.IsDefined(typeof(NoPermissionRequiredAttribute)))
                .Select(m => $"{t.Name}.{m.Name}"))
            .OrderBy(x => x)
            .ToList();

        // Redeem is called from Customer 360 by people who may hold no Case Management permission at all; it requires
        // the Host permission to open Customer 360 profiles instead (Customer360Controller.MayOpenCustomer360).
        Assert.Equal(new[] { "Customer360Controller.Redeem", "PermissionsController.Get", "UsersController.GetMe" }, open);
    }

    [Fact]
    public void Each_case_action_is_a_capability_the_Host_can_grant()
    {
        var cases = HostPermissionCatalog.Modules.Single(m => m.Key == "cases").Capabilities.Select(c => c.Key);

        Assert.Equal(new[] { "View", "Create", "Edit", "Transfer", "Resolve", "Assign", "ViewAll" }, cases);
    }

    private sealed class Env : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public AppUnderTest App = null!;
        public HttpClient Client = null!;
        public Guid Team, Other, Customer, Case;

        public static async Task<Env> StartAsync()
        {
            var e = new Env { Db = await TempDatabase.CreateAsync() };
            using (var warm = new AppUnderTest(e.Db.ConnectionString)) await warm.ReadyClientAsync();
            await using (var ctx = e.Db.NewContext())
            {
                e.Team = Guid.NewGuid(); e.Other = Guid.NewGuid(); e.Customer = Guid.NewGuid();
                ctx.Departments.AddRange(
                    new Department { Id = e.Team, Name = "Loans", Code = "LN", CreatedAt = DateTime.UtcNow },
                    new Department { Id = e.Other, Name = "Cards", Code = "CD", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
                ctx.AddRange(
                    new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = e.Team, Name = "Repayment", Code = "R", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = e.Other, Name = "Fraud", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new Customer { Id = e.Customer, FullName = "Siti Aminah", IdType = "Passport Number", Passport = "B12345678", PhoneNumber = "+60 12-111 2222", PreferredLanguage = "English", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
            }
            e.App = new AppUnderTest(e.Db.ConnectionString);
            e.Client = await e.App.ReadyClientAsync();
            var (status, body) = await e.Send(HttpMethod.Post, "/api/cases", Token("admin", administrator: true), e.CaseBody());
            Assert.Equal(HttpStatusCode.Created, status);
            e.Case = body.GetProperty("id").GetGuid();
            return e;
        }

        /// <summary>A token carrying Host capabilities in the Host's own form (remote.casemanagement.cases:View …).</summary>
        public static string Token(string subject, bool administrator = false, params string[] capabilities) =>
            AppUnderTest.Token(subject, subject, extraClaims: new Dictionary<string, object>
            {
                ["perms"] = JsonSerializer.Serialize(capabilities.Select(c => $"remote.casemanagement.{c}")),
                ["administrator"] = administrator ? "true" : "false",
            });

        public Dictionary<string, object?> CaseBody() => new()
        {
            ["title"] = "Repayment query", ["description"] = "d", ["customerId"] = Customer, ["departmentId"] = Team,
            ["subcategory"] = "repayment", ["caseType"] = "complaint", ["preferredLanguage"] = "english",
            ["sourceChannel"] = "voice", ["preferredCommunicationChannel"] = "phone", ["severity"] = "Low",
        };

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

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task Signing_in_to_the_Host_without_Case_Management_access_opens_nothing_but_who_am_I()
    {
        await using var env = await Env.StartAsync();
        var outsider = Env.Token("outsider");

        Assert.Equal(HttpStatusCode.OK, (await env.Send(HttpMethod.Get, "/api/users/me", outsider)).Status);
        foreach (var url in new[] { "/api/metadata/case-form", "/api/notifications", "/api/users", "/api/teams", "/api/preferences/x" })
            Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Get, url, outsider)).Status);
    }

    [PostgresFact]
    public async Task Viewing_working_creating_transferring_and_resolving_are_separate_capabilities()
    {
        await using var env = await Env.StartAsync();
        var viewer = Env.Token("viewer", false, "cases:View", "cases:ViewAll");
        var worker = Env.Token("worker", false, "cases:View", "cases:ViewAll", "cases:Edit");
        var creator = Env.Token("creator", false, "cases:View", "cases:Create");
        // Assign too: a transfer by someone outside the case's team also needs it (the case's own rule, checked after the capability).
        var full = Env.Token("full", false, "cases:View", "cases:ViewAll", "cases:Edit", "cases:Transfer", "cases:Resolve", "cases:Assign");

        Assert.Equal(HttpStatusCode.OK, (await env.Send(HttpMethod.Get, $"/api/cases/{env.Case}", viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Post, "/api/cases", viewer, env.CaseBody())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Post, $"/api/cases/{env.Case}/escalate", viewer, new { })).Status);

        Assert.Equal(HttpStatusCode.Created, (await env.Send(HttpMethod.Post, "/api/cases", creator, env.CaseBody())).Status);

        // Working a case does not include moving it to another team, nor ending it.
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, $"/api/cases/{env.Case}/transfer", worker, new { departmentId = env.Other, reason = "x" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, $"/api/cases/{env.Case}/resolve", worker, new { })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, $"/api/cases/{env.Case}/status", worker, new { status = "Closed" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, $"/api/cases/{env.Case}/status", worker, new { status = "Cancelled" })).Status);

        // With the capabilities, the same requests get past the permission check (and on to the case's own rules).
        Assert.NotEqual(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, $"/api/cases/{env.Case}/transfer", full, new { departmentId = env.Other, reason = "x" })).Status);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, $"/api/cases/{env.Case}/status", full, new { status = "Cancelled" })).Status);
    }
}
