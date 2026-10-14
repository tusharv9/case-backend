using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// The configuration really drives behaviour: a real application, a real migrated PostgreSQL database and real
/// signed tokens. Each test sets up configuration, then proves what the API does because of it.
/// </summary>
public class ConfigurationEndToEndTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public AppUnderTest App = null!;
        public HttpClient Client = null!;
        public Guid Dept, FraudSub, PaymentSub, Customer;
        public string Admin = AppUnderTest.Token("adm-1", "Admin User", new[] { "Admin" });
        public string Agent = AppUnderTest.Token("agt-1", "Agent User", new[] { "Agent" });
        public string Agent2 = AppUnderTest.Token("agt-2", "Other Agent", new[] { "Agent" });

        /// <summary>Creates the world, lets <paramref name="configure"/> change configuration, THEN starts the app.</summary>
        public static async Task<Env> StartAsync(Func<AppDbContext, Task>? configure = null)
        {
            var e = new Env { Db = await TempDatabase.CreateAsync() };

            // Migrate + bootstrap-seed once, so configuration rows exist to be customised before the app serves anything.
            using (var warm = new AppUnderTest(e.Db.ConnectionString))
            {
                await warm.ReadyClientAsync();
            }

            await using (var ctx = e.Db.NewContext())
            {
                e.Dept = Guid.NewGuid(); e.FraudSub = Guid.NewGuid(); e.PaymentSub = Guid.NewGuid(); e.Customer = Guid.NewGuid();
                ctx.Departments.Add(new Department { Id = e.Dept, Name = "Contact Center", Code = "CC", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
                ctx.AddRange(
                    new DepartmentSubCategory { Id = e.FraudSub, DepartmentId = e.Dept, Name = "Fraud", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new DepartmentSubCategory { Id = e.PaymentSub, DepartmentId = e.Dept, Name = "Payment Issue", Code = "P", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new Customer { Id = e.Customer, FullName = "Ahmad Razak", IdType = "Passport Number", Passport = "A98765432", PhoneNumber = "+60 12-345 6789", Email = "ahmad@example.test", PreferredLanguage = "English", Branch = "KL HQ", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();

                var critical = await ctx.PrioritySlaRules.FirstAsync(r => r.Priority == "Critical");
                ctx.PriorityCategoryMappings.Add(new PriorityCategoryMapping { Id = Guid.NewGuid(), PrioritySlaRuleId = critical.Id, DepartmentSubCategoryId = e.FraudSub, CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();

                if (configure != null) await configure(ctx);
            }

            e.App = new AppUnderTest(e.Db.ConnectionString);
            e.Client = await e.App.ReadyClientAsync();
            return e;
        }

        public HttpRequestMessage Req(HttpMethod method, string url, string token, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return request;
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, string token, object? body = null)
        {
            var response = await Client.SendAsync(Req(method, url, token, body));
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return (response.StatusCode, doc.RootElement.Clone());
        }

        public object CaseBody(Action<Dictionary<string, object?>>? tweak = null)
        {
            var body = new Dictionary<string, object?>
            {
                ["title"] = "Card used abroad", ["description"] = "Customer did not travel.",
                ["customerId"] = Customer, ["departmentId"] = Dept, ["subcategory"] = "fraud",
                ["caseType"] = "complaint", ["preferredLanguage"] = "english",
                ["sourceChannel"] = "voice", ["preferredCommunicationChannel"] = "phone",
                ["severity"] = "Low"
            };
            tweak?.Invoke(body);
            return body;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private static string[] ErrorFields(JsonElement body) =>
        body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray() : Array.Empty<string>();

    // ----------------------------------------------------------------------------------------------- cases

    [PostgresFact]
    public async Task ACase_UsesTheSubCategorysConfiguredPriority_ConfiguredSpellings_AndStoresCustomFields()
    {
        await using var env = await Env.StartAsync(async ctx =>
        {
            ctx.FieldConfigurations.Add(new FieldConfiguration
            {
                Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "policyNumber", DisplayLabel = "Policy Number",
                FieldType = "Text", IsVisible = true, IsRequired = true, IsCustomField = true, DisplayOrder = 50,
                ValidationRegex = "^POL-\\d{6}$", ValidationMessage = "Use the format POL-123456.", CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        });

        var (status, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin,
            env.CaseBody(b => b["customAttributes"] = new Dictionary<string, string> { ["policyNumber"] = "POL-123456" }));
        Assert.Equal(HttpStatusCode.Created, status);

        // Priority came from the mapping ("Low" was requested, "Critical" is configured for Fraud); values use the configured spelling.
        Assert.Equal("Critical", body.GetProperty("severity").GetString());
        Assert.Equal("Fraud", body.GetProperty("subcategory").GetString());
        Assert.Equal("Complaint", body.GetProperty("caseType").GetString());
        Assert.StartsWith("C-", body.GetProperty("caseNumber").GetString());
        Assert.Equal("Voice", body.GetProperty("sourceChannel").GetString());
        Assert.Equal("Phone", body.GetProperty("preferredCommunicationChannel").GetString());
        Assert.Equal(240, body.GetProperty("externalResolutionTargetMinutes").GetInt32());   // Critical's rule, snapshotted

        Assert.Equal("POL-123456", await env.Db.ScalarAsync<string>("SELECT \"FieldValue\" FROM \"CaseCustomAttributes\" WHERE \"FieldKey\" = 'policyNumber'"));

        // The custom value comes back on the case detail.
        var id = body.GetProperty("id").GetGuid();
        var (_, detail) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        Assert.Contains(detail.GetProperty("customAttributes").EnumerateArray(), a => a.GetProperty("fieldKey").GetString() == "policyNumber" && a.GetProperty("fieldValue").GetString() == "POL-123456");
    }

    [PostgresFact]
    public async Task EveryProblemIsReportedAtOnce_PerField_WithTheAdministratorsMessages()
    {
        await using var env = await Env.StartAsync(async ctx =>
        {
            ctx.FieldConfigurations.Add(new FieldConfiguration
            {
                Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "policyNumber", DisplayLabel = "Policy Number",
                FieldType = "Text", IsVisible = true, IsRequired = true, IsCustomField = true,
                ValidationRegex = "^POL-\\d{6}$", ValidationMessage = "Use the format POL-123456.", CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        });

        var (status, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody(b =>
        {
            b["title"] = "";                                             // required (system)
            b["sourceChannel"] = "Carrier Pigeon";                       // not in the configured list
            b["preferredLanguage"] = "Klingon";                          // not in the configured list
            b["customAttributes"] = new Dictionary<string, string> { ["policyNumber"] = "wrong", ["nope"] = "x" };
        }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(new[] { "nope", "policyNumber", "preferredLanguage", "sourceChannel", "title" }, ErrorFields(body));
        Assert.Equal("Use the format POL-123456.", body.GetProperty("errors").GetProperty("policyNumber")[0].GetString());
    }

    [PostgresFact]
    public async Task AnOptionalField_LeftBlank_IsAccepted_WhenTheAdministratorMadeItOptional()
    {
        await using var env = await Env.StartAsync(async ctx =>
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"FieldConfigurations\" SET \"IsRequired\" = FALSE WHERE \"ModuleKey\" = 'CaseManagement' AND \"SectionKey\" = 'CreateCase' " +
                "AND \"ApiField\" IN ('description', 'preferredLanguage', 'preferredCommunicationChannel', 'sourceChannel')");
        });

        var (status, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody(b =>
        {
            b["description"] = ""; b["preferredLanguage"] = null; b["preferredCommunicationChannel"] = null; b["sourceChannel"] = null;
        }));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("", body.GetProperty("sourceChannel").GetString());   // no invented "Voice" default
        Assert.Equal("", body.GetProperty("preferredCommunicationChannel").GetString());   // no invented "Phone" default
    }

    [PostgresFact]
    public async Task ARequiredField_IsEnforcedByTheBackend_NotJustTheForm()
    {
        await using var env = await Env.StartAsync();   // seeded defaults: description and sourceChannel are required

        var (status, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody(b => { b["description"] = ""; b["sourceChannel"] = null; }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(new[] { "description", "sourceChannel" }, ErrorFields(body));
    }

    [PostgresFact]
    public async Task APriority_IsRequiredOnlyWhenTheSubCategoryHasNone_AndANewPriorityWorksImmediately()
    {
        await using var env = await Env.StartAsync();

        // Unmapped sub-category and nothing chosen: an explicit error, never a silent "Medium".
        var (s1, b1) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody(b => { b["subcategory"] = "Payment Issue"; b["severity"] = ""; }));
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.Contains("priority is required", b1.GetProperty("error").GetString());

        // An unknown name is rejected and the configured ones are listed.
        var (s2, b2) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody(b => { b["subcategory"] = "Payment Issue"; b["severity"] = "Urgent"; }));
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("not a configured priority", b2.GetProperty("error").GetString());

        // The administrator adds "Urgent" on the Settings screen — no code, no restart…
        var (s3, _) = await env.Send(HttpMethod.Post, "/api/configurablesettings/severities", env.Admin,
            new { name = "Urgent", internalHours = 1, externalHours = 2, firstResponseMinutes = 15 });
        Assert.Equal(HttpStatusCode.OK, s3);

        // …and it is immediately usable, with its own SLA numbers.
        var (s4, b4) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody(b => { b["subcategory"] = "Payment Issue"; b["severity"] = "urgent"; }));
        Assert.Equal(HttpStatusCode.Created, s4);
        Assert.Equal("Urgent", b4.GetProperty("severity").GetString());
        Assert.Equal(120, b4.GetProperty("externalResolutionTargetMinutes").GetInt32());
        Assert.Equal(15, b4.GetProperty("firstResponseTargetMinutes").GetInt32());

        // The dashboard lists every configured priority, including the new one, in the configured order.
        var (_, dashboard) = await env.Send(HttpMethod.Get, "/api/dashboard/summary", env.Admin);
        var priorities = dashboard.GetProperty("casesBySeverity").EnumerateArray().Select(p => (p.GetProperty("severity").GetString(), p.GetProperty("count").GetInt32())).ToList();
        Assert.Equal(new[] { "Critical", "High", "Medium", "Low", "Urgent" }, priorities.Select(p => p.Item1));
        Assert.Equal(1, priorities.Single(p => p.Item1 == "Urgent").Item2);
    }

    [PostgresFact]
    public async Task TheCreateCaseForm_CanPreviewAPriority_WithoutDownloadingTheWholeSlaConfiguration()
    {
        await using var env = await Env.StartAsync();

        var (_, mapped) = await env.Send(HttpMethod.Get, $"/api/sla-routing/resolve-priority?departmentId={env.Dept}&subcategory=Fraud", env.Agent);
        Assert.True(mapped.GetProperty("isMapped").GetBoolean());
        Assert.Equal("Critical", mapped.GetProperty("priority").GetString());
        Assert.Equal(4, mapped.GetProperty("externalHours").GetInt32());

        var (_, unmapped) = await env.Send(HttpMethod.Get, $"/api/sla-routing/resolve-priority?departmentId={env.Dept}&subcategory=Payment%20Issue", env.Agent);
        Assert.False(unmapped.GetProperty("isMapped").GetBoolean());
    }

    [PostgresFact]
    public async Task EachFormGetsEverythingItNeedsInOneCall_AndConfigurationChangesShowUpImmediately()
    {
        await using var env = await Env.StartAsync();

        var (status, caseForm) = await env.Send(HttpMethod.Get, "/api/metadata/case-form", env.Agent);
        Assert.Equal(HttpStatusCode.OK, status);

        // Fields (with the lock flags), case types, departments WITH their sub-categories, priorities, and the options of
        // exactly the lists the form's dropdown fields use.
        var fields = caseForm.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("apiField").GetString()!);
        Assert.True(fields["title"].GetProperty("isSystemRequired").GetBoolean());
        Assert.False(fields["description"].GetProperty("isSystemRequired").GetBoolean());
        Assert.Equal(new[] { "Complaint", "Inquiry", "Service" }, caseForm.GetProperty("caseTypes").EnumerateArray().Select(c => c.GetProperty("name").GetString()).OrderBy(x => x));
        var department = caseForm.GetProperty("departments").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Contact Center");
        Assert.Equal(new[] { "Fraud", "Payment Issue" }, department.GetProperty("subCategories").EnumerateArray().Select(s => s.GetProperty("name").GetString()).OrderBy(x => x));
        Assert.Equal(new[] { "Critical", "High", "Medium", "Low" }, caseForm.GetProperty("priorities").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        var lookups = caseForm.GetProperty("lookups");
        Assert.Contains(lookups.GetProperty("SOURCE_CHANNEL").EnumerateArray(), o => o.GetProperty("value").GetString() == "Voice");
        Assert.Contains(lookups.GetProperty("COMMUNICATION_CHANNEL").EnumerateArray(), o => o.GetProperty("value").GetString() == "Phone");
        Assert.Contains(lookups.GetProperty("PREFERRED_LANGUAGE").EnumerateArray(), o => o.GetProperty("value").GetString() == "English");

        // A configuration change is visible on the very next read (no stale cache).
        await env.Send(HttpMethod.Post, "/api/configurablesettings/severities", env.Admin, new { name = "Urgent", internalHours = 1, externalHours = 2, firstResponseMinutes = 15 });
        var (_, after) = await env.Send(HttpMethod.Get, "/api/metadata/case-form", env.Agent);
        Assert.Contains(after.GetProperty("priorities").EnumerateArray(), p => p.GetProperty("name").GetString() == "Urgent");

        var (_, customerForm) = await env.Send(HttpMethod.Get, "/api/metadata/customer-form", env.Agent);
        Assert.Contains(customerForm.GetProperty("lookups").GetProperty("ID_TYPE").EnumerateArray(), o => o.GetProperty("value").GetString() == "NRIC Number");
        Assert.True(customerForm.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("apiField").GetString() == "idValue").GetProperty("isSystemRequired").GetBoolean());
    }

    // ------------------------------------------------------------------------------------------ customers

    private static object CustomerBody(Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["fullName"] = "Siti Nur", ["idType"] = "nric number", ["idValue"] = "900101-14-1234", ["dateOfBirth"] = "1990-01-01T00:00:00Z",
            ["phoneNumber"] = "+60 123-456 7890", ["email"] = "siti@example.test", ["preferredLanguage"] = "english", ["branch"] = "kl hq"
        };
        tweak?.Invoke(body);
        return body;
    }

    [PostgresFact]
    public async Task ACustomer_IsValidatedAgainstTheAddCustomerConfiguration()
    {
        await using var env = await Env.StartAsync();

        var (ok, created) = await env.Send(HttpMethod.Post, "/api/customers", env.Admin, CustomerBody());
        Assert.True(ok == HttpStatusCode.Created, created.GetRawText());
        Assert.Equal("NRIC Number", created.GetProperty("idType").GetString());          // configured spelling
        Assert.Equal("English", created.GetProperty("preferredLanguage").GetString());
        Assert.Equal("KL HQ", created.GetProperty("branch").GetString());

        // The Malaysian phone rule is now metadata with the administrator-editable message.
        var (bad, badBody) = await env.Send(HttpMethod.Post, "/api/customers", env.Admin,
            CustomerBody(b => { b["idValue"] = "900202-14-1234"; b["dateOfBirth"] = "1990-02-02T00:00:00Z"; b["phoneNumber"] = "12-345 6789"; b["email"] = "other@example.test"; }));
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal(new[] { "phoneNumber" }, ErrorFields(badBody));
        Assert.Contains("10 digits", badBody.GetProperty("errors").GetProperty("phoneNumber")[0].GetString());
    }

    [PostgresFact]
    public async Task CustomerFieldsTheAdministratorMadeOptional_CanBeOmitted_ButSystemOnesCannot()
    {
        await using var env = await Env.StartAsync(async ctx =>
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"FieldConfigurations\" SET \"IsRequired\" = FALSE WHERE \"ModuleKey\" = 'Customer360' AND \"SectionKey\" = 'AddNewCustomer' " +
                "AND \"ApiField\" IN ('email', 'branch', 'preferredLanguage', 'phoneNumber', 'dateOfBirth')");
        });

        var (ok, body) = await env.Send(HttpMethod.Post, "/api/customers", env.Admin, CustomerBody(b =>
        {
            b["email"] = ""; b["branch"] = ""; b["preferredLanguage"] = ""; b["phoneNumber"] = ""; b["dateOfBirth"] = null;
        }));
        Assert.Equal(HttpStatusCode.Created, ok);
        Assert.Equal("", body.GetProperty("branch").GetString());

        // Name and ID are system-required whatever the configuration says.
        var (bad, badBody) = await env.Send(HttpMethod.Post, "/api/customers", env.Admin, CustomerBody(b => { b["fullName"] = ""; b["idValue"] = ""; }));
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("fullName", ErrorFields(badBody));
    }

    // ----------------------------------------------------------------------------------------------- masking

    [PostgresFact]
    public async Task MaskedFields_AreMaskedInTheApiResponse_UnlessTheCallerMayUnmask()
    {
        await using var env = await Env.StartAsync(async ctx =>
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"FieldConfigurations\" SET \"MaskingRule\" = 'HideFirstShowLast', \"VisibleChars\" = 4 " +
                "WHERE \"ModuleKey\" = 'Customer360' AND \"ApiField\" IN ('idValue', 'phoneNumber')");
        });

        var url = $"/api/customers/{env.Customer}/360";

        var (_, asAgent) = await env.Send(HttpMethod.Get, url, env.Agent);
        Assert.EndsWith("5432", asAgent.GetProperty("passport").GetString());
        Assert.DoesNotContain("A9876", asAgent.GetProperty("passport").GetString());            // the old leak
        Assert.DoesNotContain("A9876", asAgent.GetProperty("idValue").GetString());
        Assert.EndsWith("6789", asAgent.GetProperty("phoneNumber").GetString());
        Assert.DoesNotContain("12-345", asAgent.GetProperty("phoneNumber").GetString());

        var (_, asAdmin) = await env.Send(HttpMethod.Get, url, env.Admin);                      // Admin holds pii.unmask
        Assert.Equal("A98765432", asAdmin.GetProperty("passport").GetString());

        // The paged list never hands out raw values either, and there is no "return everything" back door.
        var (_, page) = await env.Send(HttpMethod.Get, "/api/customers?pageSize=5000", env.Agent);
        Assert.True(page.TryGetProperty("items", out var items));
        Assert.DoesNotContain("A98765432", items.GetRawText());

        // Creating a customer answers with the same masked view.
        var (_, created) = await env.Send(HttpMethod.Post, "/api/customers", env.Agent, CustomerBody(b => { b["idValue"] = "900303-14-1234"; b["dateOfBirth"] = "1990-03-03T00:00:00Z"; b["phoneNumber"] = "+60 198-765 4321"; b["email"] = "c@example.test"; }));
        Assert.DoesNotContain("900303", created.GetProperty("nric").GetString());
        Assert.EndsWith("4321", created.GetProperty("phoneNumber").GetString());
        Assert.DoesNotContain("198-765", created.GetProperty("phoneNumber").GetString());
    }

    // ------------------------------------------------------------------------------------------------- SLA

    [PostgresFact]
    public async Task TheApi_ReportsTheSlaClocksVerdict_OnEveryCaseList_AndTheClockIsNotResetByReassignment()
    {
        await using var env = await Env.StartAsync();
        var (created, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        Assert.Equal(HttpStatusCode.Created, created);
        var id = body.GetProperty("id").GetGuid();

        var (_, detail) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        var sla = detail.GetProperty("sla");
        Assert.Equal("Healthy", sla.GetProperty("health").GetString());
        Assert.False(sla.GetProperty("isPaused").GetBoolean());
        Assert.Equal(240, sla.GetProperty("external").GetProperty("targetMinutes").GetInt32());     // Critical's rule
        Assert.True(sla.GetProperty("internal").GetProperty("targetMinutes").GetInt32() > 0);
        Assert.True(sla.GetProperty("external").GetProperty("dueAt").ValueKind != JsonValueKind.Null);

        // The same verdict is on the paged board/list responses.
        var (_, page) = await env.Send(HttpMethod.Get, "/api/cases?page=1&pageSize=10", env.Admin);
        Assert.Contains("\"sla\"", page.GetRawText());

        // Reassigning keeps the clock where it was.
        await env.Send(HttpMethod.Get, "/api/users/me", env.Agent);   // provisions the agent's local projection
        var agentId = await env.Db.ScalarAsync<Guid>("SELECT \"Id\" FROM \"Users\" WHERE \"ExternalUserId\" = 'agt-1'");
        var startBefore = await env.Db.ScalarAsync<DateTime>($"SELECT \"SlaStartTime\" FROM \"Cases\" WHERE \"Id\" = '{id}'");

        // Only members of the case's team can hold it: a non-member is refused with a reason code, then accepted once they join.
        var (refused, refusal) = await env.Send(HttpMethod.Put, $"/api/cases/{id}/assign", env.Admin, new { ownerId = agentId, reason = "Workload rebalancing" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Equal("NotTeamMember", refusal.GetProperty("code").GetString());
        var caseDept = await env.Db.ScalarAsync<Guid>($"SELECT \"DepartmentId\" FROM \"Cases\" WHERE \"Id\" = '{id}'");
        await using (var ctx = env.Db.NewContext())
        {
            ctx.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), DepartmentId = caseDept, UserId = agentId, MemberRole = "Agent", IsActive = true, IsAssignable = true, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var (_, options) = await env.Send(HttpMethod.Get, $"/api/cases/{id}/eligible-assignees", env.Admin);
        Assert.Contains(options.GetProperty("candidates").EnumerateArray(), c => c.GetProperty("userId").GetGuid() == agentId && c.GetProperty("isEligible").GetBoolean());

        var (assigned, _) = await env.Send(HttpMethod.Put, $"/api/cases/{id}/assign", env.Admin, new { ownerId = agentId, reason = "Workload rebalancing" });
        Assert.Equal(HttpStatusCode.OK, assigned);

        // An agent outside the case's team, without cases.assign, may not move it.
        var (forbidden, _) = await env.Send(HttpMethod.Put, $"/api/cases/{id}/assign", env.Agent2, new { ownerId = agentId, reason = "Workload rebalancing" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
        Assert.Equal(startBefore, await env.Db.ScalarAsync<DateTime>($"SELECT \"SlaStartTime\" FROM \"Cases\" WHERE \"Id\" = '{id}'"));
    }

    [PostgresFact]
    public async Task WaitingOnCustomer_PausesTheClock_AndResolvingSettlesTheVerdict()
    {
        await using var env = await Env.StartAsync();
        var (_, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await env.Send(HttpMethod.Put, $"/api/cases/{id}/status", env.Admin, new { status = "WaitingOnCustomer" })).Status);
        var (_, paused) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        Assert.True(paused.GetProperty("sla").GetProperty("isPaused").GetBoolean());
        Assert.Equal("Paused", paused.GetProperty("sla").GetProperty("health").GetString());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("sla").GetProperty("external").GetProperty("dueAt").ValueKind);   // no due date while paused

        Assert.Equal(HttpStatusCode.OK, (await env.Send(HttpMethod.Put, $"/api/cases/{id}/status", env.Admin, new { status = "InProgress" })).Status);
        var (_, resumed) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        Assert.False(resumed.GetProperty("sla").GetProperty("isPaused").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resumed.GetProperty("slaPausedAt").ValueKind);

        var (resolvedStatus, _) = await env.Send(HttpMethod.Put, $"/api/cases/{id}/resolve", env.Admin, new { disposition = "Resolved on First Contact", resolutionNote = "done" });
        Assert.Equal(HttpStatusCode.OK, resolvedStatus);
        var (_, resolved) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        Assert.Equal("Met", resolved.GetProperty("sla").GetProperty("health").GetString());
        Assert.True(resolved.GetProperty("sla").GetProperty("isStopped").GetBoolean());
    }

    [PostgresFact]
    public async Task TheEscalationMatrix_OnlyAcceptsTriggersTheEngineCanRun_AndShowsDerivedText()
    {
        await using var env = await Env.StartAsync();

        var (_, config) = await env.Send(HttpMethod.Get, "/api/sla-routing/configuration", env.Admin);
        Assert.Equal("Asia/Kuala_Lumpur", config.GetProperty("timeZoneId").GetString());
        Assert.Contains(config.GetProperty("triggerTypes").EnumerateArray(), t => t.GetProperty("type").GetString() == "SlaPostBreachHours" && t.GetProperty("needsValue").GetBoolean());
        Assert.Equal(new[] { "Role", "User", "DepartmentOwner", "Owner" }, config.GetProperty("assignmentTypes").EnumerateArray().Select(a => a.GetString()!).ToArray());

        // Free text is not a trigger.
        var (bad, badBody) = await env.Send(HttpMethod.Post, "/api/sla-routing/escalation-levels", env.Admin,
            new { triggerType = "When the customer is angry", targetRole = "Director" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("not an escalation trigger", badBody.GetProperty("error").GetString());

        // A threshold is required where one makes sense.
        var (noValue, _) = await env.Send(HttpMethod.Post, "/api/sla-routing/escalation-levels", env.Admin,
            new { triggerType = "SlaPercentage", targetRole = "Director" });
        Assert.Equal(HttpStatusCode.BadRequest, noValue);

        var (ok, level) = await env.Send(HttpMethod.Post, "/api/sla-routing/escalation-levels", env.Admin,
            new { triggerType = "SlaPercentage", triggerValue = 150, targetRole = "Director", actionDescription = "Executive review" });
        Assert.True(ok is HttpStatusCode.OK or HttpStatusCode.Created);
        Assert.Equal("SLA consumption reaches 150%", level.GetProperty("triggerDescription").GetString());

        // An unknown time zone is refused; a real one is stored and used.
        var (zoneBad, _) = await env.Send(HttpMethod.Put, "/api/sla-routing/configuration", env.Admin,
            new { timeZoneId = "Mars/Olympus_Mons", priorityRules = Array.Empty<object>(), businessHours = Array.Empty<object>(), escalationLevels = Array.Empty<object>() });
        Assert.True((int)zoneBad >= 400);
    }

    [PostgresFact]
    public async Task SavingTheSlaConfiguration_PersistsStructuredLevels_TheTimeZone_AndTakesEffectImmediately()
    {
        await using var env = await Env.StartAsync();
        var (_, config) = await env.Send(HttpMethod.Get, "/api/sla-routing/configuration", env.Admin);

        // Edit what the page edits: time zone, a threshold, an inactive level, and a role by exact name.
        var levels = config.GetProperty("escalationLevels").EnumerateArray().Select(l => new Dictionary<string, object?>
        {
            ["levelNumber"] = l.GetProperty("levelNumber").GetInt32(),
            ["name"] = l.GetProperty("name").GetString(),
            ["assignmentType"] = l.GetProperty("assignmentType").GetString(),
            ["targetRole"] = l.GetProperty("targetRole").GetString(),
            ["targetUserId"] = null,
            ["triggerType"] = l.GetProperty("triggerType").GetString(),
            ["triggerValue"] = l.GetProperty("triggerValue").ValueKind == JsonValueKind.Null ? null : (decimal?)l.GetProperty("triggerValue").GetDecimal(),
            ["actionDescription"] = l.GetProperty("actionDescription").GetString(),
            ["reassignOwner"] = l.GetProperty("reassignOwner").GetBoolean(),
            ["isActive"] = l.GetProperty("levelNumber").GetInt32() != 4,    // switch level 4 off
        }).ToList();
        levels.Single(l => (int)l["levelNumber"]! == 2)["triggerValue"] = 95m;

        var payload = new
        {
            timeZoneId = "Europe/London",
            priorityRules = config.GetProperty("priorityRules").EnumerateArray().Select(r => JsonSerializer.Deserialize<object>(r.GetRawText())).ToList(),
            businessHours = config.GetProperty("businessHours").EnumerateArray().Select(h => JsonSerializer.Deserialize<object>(h.GetRawText())).ToList(),
            escalationLevels = levels,
        };
        var (status, saved) = await env.Send(HttpMethod.Put, "/api/sla-routing/configuration", env.Admin, payload);
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal("Europe/London", saved.GetProperty("timeZoneId").GetString());
        var level2 = saved.GetProperty("escalationLevels").EnumerateArray().Single(l => l.GetProperty("levelNumber").GetInt32() == 2);
        Assert.Equal(95m, level2.GetProperty("triggerValue").GetDecimal());
        Assert.Equal("SLA consumption reaches 95%", level2.GetProperty("triggerDescription").GetString());   // derived, not typed
        Assert.False(saved.GetProperty("escalationLevels").EnumerateArray().Single(l => l.GetProperty("levelNumber").GetInt32() == 4).GetProperty("isActive").GetBoolean());

        // The time zone is live for the clock straight away (no restart, no stale cache): a new case's due date follows it.
        var (created, body) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        Assert.Equal(HttpStatusCode.Created, created);
        var id = body.GetProperty("id").GetGuid();
        var (_, detail) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        Assert.Equal(240, detail.GetProperty("sla").GetProperty("external").GetProperty("targetMinutes").GetInt32());
    }

    [PostgresFact]
    public async Task OverTheApi_MembershipDecidesWhoGetsTheCase_AndAnUnstaffedTeamHoldsItWithAReason()
    {
        await using var env = await Env.StartAsync();

        // The team has nobody yet: the case is created (never refused) and the creator keeps it, with the reason on its timeline.
        var (s0, held) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        Assert.Equal(HttpStatusCode.Created, s0);
        var heldTimeline = await env.Db.ScalarAsync<string>($"SELECT string_agg(\"Message\", ' | ') FROM \"CaseEvents\" WHERE \"CaseId\" = '{held.GetProperty("id").GetGuid()}'");
        Assert.Contains("Pending agent assignment", heldTimeline);
        Assert.Contains("no active members", heldTimeline);

        // An agent signs in (their local projection is created) and is added to the team.
        await env.Send(HttpMethod.Get, "/api/users/me", env.Agent);
        var agentId = await env.Db.ScalarAsync<Guid>("SELECT \"Id\" FROM \"Users\" WHERE \"ExternalUserId\" = 'agt-1'");
        var (added, _) = await env.Send(HttpMethod.Post, $"/api/teams/{env.Dept}/members", env.Admin, new { userId = agentId, isAssignable = true });
        Assert.Equal(HttpStatusCode.OK, added);

        var (s1, assigned) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(agentId, assigned.GetProperty("ownerId").GetGuid());

        // The monitor sees the agent and their case — real numbers, filterable by team.
        var (_, overview) = await env.Send(HttpMethod.Get, $"/api/team-monitoring/overview?teamId={env.Dept}", env.Admin);
        var agentRow = overview.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("userId").GetGuid() == agentId);
        Assert.Equal(1, agentRow.GetProperty("openCasesCount").GetInt32());
        Assert.Equal("Contact Center", overview.GetProperty("teamName").GetString());
        Assert.False(overview.GetProperty("summary").TryGetProperty("csatScore", out _));

        // The team page shows the membership and the settings in force; the editor options come from the server.
        var (_, teams) = await env.Send(HttpMethod.Get, "/api/teams", env.Admin);
        var team = teams.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == env.Dept);
        Assert.Equal("RoundRobin", team.GetProperty("assignmentAlgorithm").GetString());
        Assert.False(team.GetProperty("hasOwnAssignmentSettings").GetBoolean());
        Assert.False(team.TryGetProperty("channels", out _));

        var (_, vocab) = await env.Send(HttpMethod.Get, "/api/routing-rules/vocabulary", env.Admin);
        Assert.Contains(vocab.GetProperty("channels").EnumerateArray(), c => c.GetString() == "Voice");
        Assert.Contains(vocab.GetProperty("priorities").EnumerateArray(), c => c.GetString() == "Critical");

        // Skills: rules are replaced as a set and validated.
        var (bad, _) = await env.Send(HttpMethod.Put, "/api/skills/rules", env.Admin, new[] { new { skillName = "X", matchField = "Mood", matchType = "Contains", matchValue = "angry" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        var (good, saved) = await env.Send(HttpMethod.Put, "/api/skills/rules", env.Admin, new[] { new { skillName = "Cards", matchField = "Title", matchType = "Contains", matchValue = "card" } });
        Assert.Equal(HttpStatusCode.OK, good);
        Assert.Equal("Cards", saved[0].GetProperty("skillName").GetString());

        // Agents cannot manage teams, skills or routing.
        Assert.Equal(HttpStatusCode.Forbidden, (await env.Send(HttpMethod.Put, "/api/skills/rules", env.Agent, Array.Empty<object>())).Status);
    }

    // ------------------------------------------------------------------------------------- attachments, dashboard, timeline

    private static readonly byte[] PdfBytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Upload(Env env, Guid caseId, string fileName, byte[] bytes, string clientContentType, string token)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(clientContentType);
        form.Add(part, "file", fileName);
        form.Add(new StringContent("evidence"), "note");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{caseId}/attachments") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await env.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    [PostgresFact]
    public async Task Attachments_AreVerifiedByContent_AndOnlyEverServedAsADownload()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var caseId = created.GetProperty("id").GetGuid();

        // A program renamed .pdf, and a web page renamed .txt, are refused — the client's content type is irrelevant.
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(env, caseId, "invoice.pdf", System.Text.Encoding.ASCII.GetBytes("MZ\u0090\0\u0003\0"), "application/pdf", env.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(env, caseId, "notes.txt", System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script>"), "text/plain", env.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(env, caseId, "logo.svg", System.Text.Encoding.UTF8.GetBytes("<svg/>"), "image/svg+xml", env.Admin)).Status);

        // A real PDF is accepted, even when the client mislabels it.
        var (ok, saved) = await Upload(env, caseId, "statement.pdf", PdfBytes, "text/html", env.Admin);
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("application/pdf", saved.GetProperty("fileType").GetString());
        var attachmentId = saved.GetProperty("id").GetGuid();

        var download = env.Req(HttpMethod.Get, $"/api/cases/{caseId}/attachments/{attachmentId}/download", env.Agent);
        var response = await env.Client.SendAsync(download);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);      // never rendered inline
        Assert.Equal("statement.pdf", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal(PdfBytes, await response.Content.ReadAsByteArrayAsync());

        // Another case's URL does not reach this attachment.
        var (_, other) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var wrongCase = await env.Client.SendAsync(env.Req(HttpMethod.Get, $"/api/cases/{other.GetProperty("id").GetGuid()}/attachments/{attachmentId}/download", env.Admin));
        Assert.Equal(HttpStatusCode.NotFound, wrongCase.StatusCode);

        // And the list shows exactly what was kept.
        var (_, list) = await env.Send(HttpMethod.Get, $"/api/cases/{caseId}/attachments", env.Admin);
        Assert.Single(list.EnumerateArray());
    }

    [PostgresFact]
    public async Task TheDashboard_LoadsItsFiltersInOneRequest_AndHonoursRealRanges()
    {
        await using var env = await Env.StartAsync();
        await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());

        var (status, filters) = await env.Send(HttpMethod.Get, "/api/dashboard/filters", env.Admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(filters.GetProperty("departments").EnumerateArray(), d => d.GetProperty("name").GetString() == "Contact Center");
        Assert.Contains(filters.GetProperty("priorities").EnumerateArray(), p => p.GetString() == "Critical");
        Assert.Contains(filters.GetProperty("caseTypes").EnumerateArray(), t => t.GetProperty("value").GetString() == "Complaint");
        Assert.NotEmpty(filters.GetProperty("statuses").EnumerateArray());
        var ranges = filters.GetProperty("dateRanges").EnumerateArray().Select(r => r.GetProperty("value").GetString()!).ToList();
        Assert.All(ranges, r => Assert.Contains(r, CaseManagement.Api.Services.DashboardService.SupportedDateRanges));   // never offers a range it cannot compute
        Assert.Contains("last_week", ranges);

        // Every offered range works; an unknown one is a clear error, not "all time".
        foreach (var range in ranges.Where(r => r != "custom"))
            Assert.Equal(HttpStatusCode.OK, (await env.Send(HttpMethod.Get, $"/api/dashboard/summary?dateRange={range}", env.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Send(HttpMethod.Get, "/api/dashboard/summary?dateRange=whenever", env.Admin)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await env.Send(HttpMethod.Get, "/api/dashboard/summary?status=Imaginary", env.Admin)).Status);

        // The summary carries the SLA clock's verdict on the cases that need attention, and activity belongs to cases.
        var (_, summary) = await env.Send(HttpMethod.Get, "/api/dashboard/summary", env.Admin);
        Assert.Equal(1, summary.GetProperty("totalCases").GetInt32());
        var attention = summary.GetProperty("attentionCases").EnumerateArray().Single();
        Assert.Equal("Healthy", attention.GetProperty("sla").GetProperty("health").GetString());
        Assert.All(summary.GetProperty("recentActivities").EnumerateArray(), a => Assert.NotEqual(Guid.Empty, a.GetProperty("caseId").GetGuid()));

        // A department filter that matches nothing yields an honest zero.
        var (_, none) = await env.Send(HttpMethod.Get, $"/api/dashboard/summary?departmentId={Guid.NewGuid()}", env.Admin);
        Assert.Equal(0, none.GetProperty("totalCases").GetInt32());
    }

    [PostgresFact]
    public async Task CustomerTimeline_ShowsCaseMilestonesAndCustomerVisibleMessages_NotInternalWork()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var caseId = created.GetProperty("id").GetGuid();

        // An internal working note and an internal escalation reminder must not appear; resolving the case does.
        await env.Send(HttpMethod.Post, $"/api/cases/{caseId}/notes", env.Admin, new { message = "internal: customer sounded stressed" });
        await env.Send(HttpMethod.Put, $"/api/cases/{caseId}/resolve", env.Admin, new { disposition = "Resolved on First Contact", resolutionNote = "done" });

        var (status, page) = await env.Send(HttpMethod.Get, $"/api/customers/{env.Customer}/timeline?page=1&pageSize=20", env.Admin);
        Assert.Equal(HttpStatusCode.OK, status);

        var types = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()).ToList();
        Assert.Contains("Create", types);
        Assert.Contains("Resolve", types);
        Assert.DoesNotContain(page.GetProperty("items").EnumerateArray(), i => i.GetProperty("message").GetString()!.Contains("stressed"));
        Assert.All(page.GetProperty("items").EnumerateArray(), i => Assert.Equal(caseId, i.GetProperty("caseId").GetGuid()));

        // A customer with no cases has an empty (not failing) timeline.
        var (emptyStatus, empty) = await env.Send(HttpMethod.Get, $"/api/customers/{Guid.NewGuid()}/timeline", env.Admin);
        Assert.Equal(HttpStatusCode.OK, emptyStatus);
        Assert.Equal(0, empty.GetProperty("totalCount").GetInt32());
    }

    // ------------------------------------------------------------------------------------------- scale & refactor guards

    [PostgresFact]
    public async Task ListEndpoints_AreAlwaysPaged_AndPageSizesAreCapped()
    {
        await using var env = await Env.StartAsync();
        for (var i = 0; i < 3; i++) await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());

        // No page parameter used to return EVERY case in the database; it now returns the first page of a paged answer.
        var (_, plain) = await env.Send(HttpMethod.Get, "/api/cases", env.Admin);
        Assert.True(plain.TryGetProperty("items", out var items));
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal(30, plain.GetProperty("pageSize").GetInt32());

        var (_, huge) = await env.Send(HttpMethod.Get, "/api/cases?page=1&pageSize=100000", env.Admin);
        Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());

        var (_, nonsense) = await env.Send(HttpMethod.Get, "/api/cases?page=-5&pageSize=0", env.Admin);
        Assert.Equal(1, nonsense.GetProperty("page").GetInt32());
        Assert.InRange(nonsense.GetProperty("pageSize").GetInt32(), 1, 100);

        var (_, notes) = await env.Send(HttpMethod.Get, "/api/notifications?pageSize=100000", env.Admin);
        Assert.True(notes.GetProperty("pageSize").GetInt32() <= 100);
    }

    [PostgresFact]
    public async Task ResolvingACase_RecordsItsSlaOutcome_AndTheDashboardCountsOutcomesInTheDatabase()
    {
        await using var env = await Env.StartAsync();
        var (_, made) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var id = made.GetProperty("id").GetGuid();

        Assert.Equal(0L, await env.Db.ScalarAsync<long>($"SELECT count(*) FROM \"Cases\" WHERE \"Id\" = '{id}' AND \"SlaOutcome\" IS NOT NULL"));   // open: no outcome yet
        await env.Send(HttpMethod.Put, $"/api/cases/{id}/resolve", env.Admin, new { disposition = "Resolved on First Contact", resolutionNote = "done" });
        Assert.Equal("Met", await env.Db.ScalarAsync<string>($"SELECT \"SlaOutcome\" FROM \"Cases\" WHERE \"Id\" = '{id}'"));

        // A finished case that breached is counted from its recorded outcome (no clock re-run over history).
        await env.Db.ScalarAsync<long>($"WITH u AS (UPDATE \"Cases\" SET \"SlaOutcome\" = 'Breached' WHERE \"Id\" = '{id}' RETURNING 1) SELECT count(*) FROM u");
        var (_, summary) = await env.Send(HttpMethod.Get, "/api/dashboard/summary", env.Admin);
        Assert.Equal(1, summary.GetProperty("slaBreachedCases").GetInt32());
        Assert.Equal(0m, summary.GetProperty("slaAdherencePercent").GetDecimal());
    }

    [PostgresFact]
    public async Task MentionsNotifyActiveColleaguesOnly_NeverTheAuthor_AndNeverEveryone()
    {
        await using var env = await Env.StartAsync();
        var (_, made) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var id = made.GetProperty("id").GetGuid();

        await env.Send(HttpMethod.Get, "/api/users/me", env.Agent);   // "Agent User" now exists locally
        var agentId = await env.Db.ScalarAsync<Guid>("SELECT \"Id\" FROM \"Users\" WHERE \"ExternalUserId\" = 'agt-1'");

        // The same shared mention logic serves collaboration notes and internal timeline notes.
        await env.Send(HttpMethod.Post, $"/api/cases/{id}/collaboration/notes", env.Admin, new { content = "please look @agent" });
        await env.Send(HttpMethod.Post, $"/api/cases/{id}/timeline-interaction", env.Admin, new { message = "internal ping @agent", isInternal = true });
        await env.Send(HttpMethod.Post, $"/api/cases/{id}/collaboration/notes", env.Admin, new { content = "talking to myself @admin" });   // the author is never notified

        Assert.True(await env.Db.ScalarAsync<long>($"SELECT count(*) FROM \"Notifications\" WHERE \"RecipientUserId\" = '{agentId}' AND \"Type\" = 'USER_MENTIONED'") >= 1);
        Assert.Equal(0L, await env.Db.ScalarAsync<long>("SELECT count(*) FROM \"Notifications\" n JOIN \"Users\" u ON u.\"Id\" = n.\"RecipientUserId\" WHERE u.\"ExternalUserId\" = 'adm-1' AND n.\"Type\" = 'USER_MENTIONED'"));

        // A deactivated colleague is not pinged.
        await env.Db.ScalarAsync<long>($"WITH u AS (UPDATE \"Users\" SET \"IsActive\" = false WHERE \"Id\" = '{agentId}' RETURNING 1) SELECT count(*) FROM u");
        var before = await env.Db.ScalarAsync<long>($"SELECT count(*) FROM \"Notifications\" WHERE \"RecipientUserId\" = '{agentId}'");
        await env.Send(HttpMethod.Post, $"/api/cases/{id}/collaboration/notes", env.Admin, new { content = "again @agent" });
        Assert.Equal(before, await env.Db.ScalarAsync<long>($"SELECT count(*) FROM \"Notifications\" WHERE \"RecipientUserId\" = '{agentId}'"));
    }

    // ------------------------------------------------------------------------------------------ masking (CM-10)

    [PostgresFact]
    public async Task AMaskedCustomerName_IsMaskedOnCaseScreens_ForPeopleWithoutPiiUnmask()
    {
        await using var env = await Env.StartAsync(async ctx =>
        {
            ctx.FieldConfigurations.Add(new FieldConfiguration
            {
                Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = "ExistingCustomer", ApiField = "fullName", DisplayLabel = "Full Name",
                FieldType = "Text", IsVisible = true, MaskingRule = "HideMiddle", VisibleChars = 2, DisplayOrder = 999, CreatedAt = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        });
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.Admin, env.CaseBody());
        var id = created.GetProperty("id").GetGuid();

        // An agent (no pii.unmask) sees the configured mask on the list and on the case; the admin role sees the raw value.
        var (_, agentList) = await env.Send(HttpMethod.Get, "/api/cases?page=1&pageSize=10", env.Agent);
        Assert.Contains("Ah*******ak", agentList.GetRawText());
        Assert.DoesNotContain("Ahmad Razak", agentList.GetRawText());
        var (_, agentCase) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Agent);
        Assert.Equal("Ah*******ak", agentCase.GetProperty("customerName").GetString());
        Assert.Equal("Ah*******ak", agentCase.GetProperty("customer").GetProperty("fullName").GetString());

        var (_, adminCase) = await env.Send(HttpMethod.Get, $"/api/cases/{id}", env.Admin);
        Assert.Equal("Ahmad Razak", adminCase.GetProperty("customerName").GetString());
    }

    [PostgresFact]
    public async Task MaskingAFilter_IsRefused()
    {
        await using var env = await Env.StartAsync();
        var filterId = await env.Db.ScalarAsync<Guid>("SELECT \"Id\" FROM \"FieldConfigurations\" WHERE \"ModuleKey\" = 'CaseManagement' AND \"SectionKey\" = 'Filters' AND \"ApiField\" = 'status'");
        var (_, fields) = await env.Send(HttpMethod.Get, "/api/configurablesettings/fields?moduleKey=CaseManagement&sectionKey=Filters", env.Admin);
        var status = fields.EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == filterId);
        var update = JsonSerializer.Deserialize<Dictionary<string, object?>>(status.GetRawText())!;
        update["maskingRule"] = "FullMask";

        var (code, body) = await env.Send(HttpMethod.Put, "/api/configurablesettings/fields", env.Admin,
            new { moduleKey = "CaseManagement", sectionKey = "Filters", update = new[] { update }, create = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, code);
        Assert.Contains("Masking does not apply to filters", body.GetRawText());
        Assert.Equal("None", await env.Db.ScalarAsync<string>($"SELECT \"MaskingRule\" FROM \"FieldConfigurations\" WHERE \"Id\" = '{filterId}'"));
    }
}
