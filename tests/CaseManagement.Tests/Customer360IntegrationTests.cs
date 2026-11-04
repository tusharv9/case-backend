using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Customer360;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaseManagement.Tests;

/// <summary>
/// A stand-in for Customer360Service at the HTTP boundary: the request paths it was sent, the token each carried,
/// and canned answers in Customer 360's own envelope ({ status, data }).
/// </summary>
internal sealed class FakeCustomer360 : HttpMessageHandler
{
    public List<(string PathAndQuery, string? Authorization)> Requests { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static readonly object Lookups = new
    {
        status = 200,
        data = new
        {
            idTypes = new[] { new { value = "Phone", label = "Phone Number" }, new { value = "Name", label = "Full Name" }, new { value = "NRIC", label = "National ID (NRIC)" }, new { value = "SecondaryID", label = "Secondary ID" } },
            secondaryIdTypes = new[] { new { value = "PASSPORT", label = "Passport" }, new { value = "OLDID", label = "Old IC" } },
            corpSearchTypes = new[] { new { value = "BRN", label = "BRN" }, new { value = "OLDBRN", label = "Old BRN" }, new { value = "COMPANYNAME", label = "Company Name" } },
        },
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
        if (Override?.Invoke(request) is { } overridden) return Task.FromResult(overridden);

        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        HttpResponseMessage response = path switch
        {
            "/v1/lookups" => Json(Lookups),
            "/v1/indprofile" when query.Contains("id=900101015555") || query.Contains("id=A1234567") => Json(new
            {
                status = 200,
                data = new object[]
                {
                    new { phprId = "P-1001", fullName = "Aisha  Rahman", nationalId = "900101-01-5555", passport = "A1234567", birthDate = "1990-01-01", branch = "KL Main", segmentation = "Premier", languagePreferred = "English", preferComChnl = "Email" },
                    new { phprId = "P-1002", fullName = "Aisha Rahman", nationalId = "900101015555", birthDate = "1990-01-01", branch = "Penang" },
                },
            }),
            "/v1/indprofile" => Json(new { status = 200, data = Array.Empty<object>() }),
            "/v1/corpprofile" when query.Contains("id=201901000005") => Json(new
            {
                status = 200,
                data = new object[] { new { customerId = "C-77", organizationName = "Maju Jaya Sdn Bhd", brn = "201901000005", businessRegDate = "2019-01-02", segmentation = "SME" } },
            }),
            "/v1/corpprofile" => Json(new { status = 404, message = "Not Found" }, HttpStatusCode.NotFound),
            "/v1/contactinfo" => Json(new { status = 200, data = new { contactNumber = "+60123456789", padrEmail1 = "AISHA@EXAMPLE.TEST" } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }
}

/// <summary>Masks every name it is given, so a test can see masking was applied to what it returns.</summary>
internal sealed class NameMasking : IPiiMaskingService
{
    public string Mask(string? rawValue, string maskingRule, int visibleChars = 4) => rawValue ?? string.Empty;
    public Task MaskAsync(object? dto, CancellationToken ct = default)
    {
        if (dto is Customer360CustomerDto c) { c.FullName = "***"; c.DateOfBirth = null; }
        return Task.CompletedTask;
    }
    public Task MaskCustomerSummaryAsync(CustomerSummaryDto dto, CancellationToken ct = default) => Task.CompletedTask;
    public Task MaskCustomerDetailAsync(CustomerDetailDto dto, CancellationToken ct = default) => Task.CompletedTask;
    public Task MaskCaseAsync(object? dto, CancellationToken ct = default) => Task.CompletedTask;
    public Task MaskCasesAsync(IEnumerable<object>? dtos, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// The identifiers a case keeps for a Customer 360 customer are the most sensitive thing it stores. They must be
/// unreadable without the key, findable without decrypting, and impossible to store by accident without keys.
/// </summary>
public class CustomerIdentifierProtectorTests
{
    internal static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    internal static CustomerIdentifierProtector Protector(string? encryptionKey = null, string? hashKey = null) =>
        new(Options.Create(new CustomerIdentifierOptions { EncryptionKey = encryptionKey ?? NewKey(), HashKey = hashKey ?? NewKey() }));

    [Fact]
    public void An_identifier_round_trips_but_is_never_stored_in_the_clear_and_never_encrypts_the_same_way_twice()
    {
        var protector = Protector();

        var first = protector.Protect("NRIC", "900101015555");
        var second = protector.Protect("NRIC", "900101015555");

        Assert.DoesNotContain("900101015555", first);
        Assert.NotEqual(first, second);                                  // random nonce: equal NRICs are not visible as equal
        Assert.Equal("900101015555", protector.Unprotect(first));
    }

    [Fact]
    public void The_lookup_hash_is_stable_for_one_identifier_and_differs_by_identifier_type_and_key()
    {
        var hashKey = NewKey();
        var protector = Protector(hashKey: hashKey);

        Assert.Equal(protector.Hash("NRIC", "900101015555"), protector.Hash("nric", " 900101015555 "));
        Assert.NotEqual(protector.Hash("NRIC", "900101015555"), protector.Hash("SecondaryID:PASSPORT", "900101015555"));
        Assert.NotEqual(protector.Hash("NRIC", "900101015555"), Protector().Hash("NRIC", "900101015555"));
    }

    [Fact]
    public void A_tampered_value_or_another_key_cannot_be_read()
    {
        var protector = Protector();
        var stored = protector.Protect("BRN", "201901000005");
        var bytes = Convert.FromBase64String(stored[3..]);
        bytes[^1] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect("v1:" + Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => Protector().Unprotect(stored));
    }

    [Fact]
    public void Without_keys_nothing_is_protected_and_a_malformed_key_is_refused_at_startup()
    {
        var unconfigured = new CustomerIdentifierProtector(Options.Create(new CustomerIdentifierOptions()));

        Assert.False(unconfigured.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => unconfigured.Protect("NRIC", "900101015555"));
        Assert.Throws<InvalidOperationException>(() => Protector(encryptionKey: Convert.ToBase64String(new byte[16])));
    }
}

/// <summary>
/// The existing-customer lookup against Customer 360's real contract: which searches are offered, what is sent
/// (and with whose token), what comes back, and how every failure reads to the person at the form.
/// </summary>
public class Customer360CustomerServiceTests
{
    private const string CallerToken = "Bearer caller-token";

    private static (Customer360CustomerService Service, FakeCustomer360 Fake, CustomerIdentifierProtector Protector) Create(IPiiMaskingService? masking = null)
    {
        var fake = new FakeCustomer360();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = CallerToken;
        var options = Options.Create(new Customer360Options { BaseUrl = "http://c360.test", RetryCount = 1 });
        var client = new Customer360Client(new HttpClient(fake), new HttpContextAccessor { HttpContext = context }, options, NullLogger<Customer360Client>.Instance);
        var protector = CustomerIdentifierProtectorTests.Protector();
        var service = new Customer360CustomerService(client, protector, masking ?? new NameMasking(), new MemoryCache(new MemoryCacheOptions()), options);
        return (service, fake, protector);
    }

    [Fact]
    public async Task Only_identifier_searches_are_offered_with_Customer_360s_own_labels()
    {
        var (service, _, _) = Create();

        var options = await service.GetSearchOptionsAsync(CancellationToken.None);

        Assert.Equal(new[] { "NRIC", "SecondaryID:PASSPORT", "SecondaryID:OLDID", "BRN", "OLDBRN" }, options.Select(o => o.Key));
        Assert.Equal("National ID (NRIC)", options[0].Label);
        Assert.DoesNotContain(options, o => o.Key is "Phone" or "Name" or "COMPANYNAME");
    }

    [Fact]
    public async Task An_NRIC_search_is_sent_normalised_with_the_callers_token_and_returns_every_match_masked()
    {
        var (service, fake, _) = Create();

        var result = await service.SearchAsync("Individual", "NRIC", "900101-01-5555", CancellationToken.None);

        Assert.Contains(fake.Requests, r => r.PathAndQuery == "/v1/indprofile?type=NRIC&id=900101015555");
        Assert.All(fake.Requests, r => Assert.Equal(CallerToken, r.Authorization));
        Assert.Equal(new[] { "P-1001", "P-1002" }, result.Matches.Select(m => m.ExternalCustomerId));
        Assert.All(result.Matches, m => Assert.Equal("***", m.FullName));     // masking ran on what is returned
        Assert.Equal("+60123456789", result.Matches[0].PhoneNumber);
        Assert.Equal("aisha@example.test", result.Matches[0].Email);
        Assert.Contains(fake.Requests, r => r.PathAndQuery == "/v1/contactinfo?id=900101015555");
    }

    [Fact]
    public async Task A_secondary_ID_and_a_company_search_use_Customer_360s_wire_values()
    {
        var (service, fake, _) = Create();

        await service.SearchAsync("Individual", "SecondaryID:PASSPORT", "a1234567", CancellationToken.None);
        var company = await service.SearchAsync("NonIndividual", "BRN", "201901000005", CancellationToken.None);

        Assert.Contains(fake.Requests, r => r.PathAndQuery == "/v1/indprofile?type=SECONDARYID&subtype=PASSPORT&id=A1234567");
        Assert.Contains(fake.Requests, r => r.PathAndQuery == "/v1/corpprofile?type=BRN&id=201901000005");
        Assert.Contains(fake.Requests, r => r.PathAndQuery == "/v1/contactinfo?type=CORPORATE&id=201901000005");
        Assert.Equal("C-77", Assert.Single(company.Matches).ExternalCustomerId);
    }

    [Theory]
    [InlineData("Individual", "NRIC", "abc")]
    [InlineData("Individual", "NRIC", "")]
    [InlineData("Individual", "Phone", "0123456789")]       // offered by Customer 360, but not an identifier
    [InlineData("Robot", "NRIC", "900101015555")]
    [InlineData("NonIndividual", "BRN", "<script>")]
    public async Task An_unusable_search_is_refused_before_any_customer_lookup(string type, string by, string value)
    {
        var (service, fake, _) = Create();

        await Assert.ThrowsAsync<Customer360SearchException>(() => service.SearchAsync(type, by, value, CancellationToken.None));

        Assert.DoesNotContain(fake.Requests, r => r.PathAndQuery.Contains("profile"));   // no audited lookup for a typo
    }

    [Fact]
    public async Task No_match_is_an_empty_result_for_both_kinds_of_customer()
    {
        var (service, _, _) = Create();

        Assert.Empty((await service.SearchAsync("Individual", "NRIC", "111111111111", CancellationToken.None)).Matches);
        Assert.Empty((await service.SearchAsync("NonIndividual", "BRN", "999999999999", CancellationToken.None)).Matches);
    }

    [Fact]
    public async Task Refused_access_and_an_outage_are_told_apart_and_an_outage_is_retried_once()
    {
        var (forbidden, f1, _) = Create();
        await forbidden.GetSearchOptionsAsync(CancellationToken.None);
        f1.Override = r => r.RequestUri!.AbsolutePath.Contains("profile") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null;
        var refused = await Assert.ThrowsAsync<Customer360Exception>(() => forbidden.SearchAsync("Individual", "NRIC", "900101015555", CancellationToken.None));
        Assert.Equal(Customer360Failure.NotPermitted, refused.Failure);

        var (down, f2, _) = Create();
        await down.GetSearchOptionsAsync(CancellationToken.None);
        f2.Override = r => r.RequestUri!.AbsolutePath.Contains("profile") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        var outage = await Assert.ThrowsAsync<Customer360Exception>(() => down.SearchAsync("Individual", "NRIC", "900101015555", CancellationToken.None));
        Assert.Equal(Customer360Failure.Unavailable, outage.Failure);
        Assert.Equal(2, f2.Requests.Count(r => r.PathAndQuery.Contains("indprofile")));
    }

    [Fact]
    public async Task Contact_details_are_optional_the_customer_is_still_found_when_they_are_refused()
    {
        var (service, fake, _) = Create();
        fake.Override = r => r.RequestUri!.AbsolutePath == "/v1/contactinfo" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null;

        var result = await service.SearchAsync("Individual", "NRIC", "900101015555", CancellationToken.None);

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, m => { Assert.False(m.ContactDetailsAvailable); Assert.Null(m.PhoneNumber); });
    }

    [Fact]
    public async Task Choosing_a_customer_keeps_the_chosen_match_and_its_NRIC_even_when_found_by_passport()
    {
        var (service, _, _) = Create();

        var resolved = await service.ResolveForNewCaseAsync(new Customer360SelectionDto
        {
            CustomerType = "Individual", SearchBy = "SecondaryID:PASSPORT", Value = "A1234567", ExternalCustomerId = "P-1001",
        }, CancellationToken.None);

        Assert.Equal("P-1001", resolved.ExternalCustomerId);
        Assert.Equal("NRIC", resolved.LookupType);
        Assert.Equal("900101015555", resolved.LookupKey);
        Assert.Equal("Premier", resolved.CustomerSegment);
    }

    [Fact]
    public async Task A_customer_the_search_does_not_return_cannot_be_chosen()
    {
        var (service, _, _) = Create();

        await Assert.ThrowsAsync<Customer360SearchException>(() => service.ResolveForNewCaseAsync(new Customer360SelectionDto
        {
            CustomerType = "Individual", SearchBy = "NRIC", Value = "900101015555", ExternalCustomerId = "P-9999",
        }, CancellationToken.None));
    }

    [Fact]
    public async Task A_cases_customer_is_read_live_by_its_stored_identifier_and_an_outage_says_so_instead_of_inventing_details()
    {
        var (service, fake, protector) = Create();
        var caseItem = new Case
        {
            CustomerSource = CustomerSources.Customer360, CustomerType = CustomerTypes.Individual, ExternalCustomerId = "P-1002",
            CustomerLookupType = "NRIC", CustomerLookupKeyEncrypted = protector.Protect("NRIC", "900101015555"),
            CustomerLookupKeyHash = protector.Hash("NRIC", "900101015555"),
        };

        var live = await service.GetForCaseAsync(caseItem, CancellationToken.None);
        Assert.Equal("Available", live.Status);
        Assert.Equal("P-1002", live.Customer!.ExternalCustomerId);
        Assert.Equal("***", live.Customer.FullName);

        fake.Override = r => r.RequestUri!.AbsolutePath.Contains("profile") ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null;
        var down = await service.GetForCaseAsync(caseItem, CancellationToken.None);
        Assert.Equal("Unavailable", down.Status);
        Assert.Null(down.Customer);
    }
}

/// <summary>Creating and reading cases for Customer 360 customers through the real API and database.</summary>
public class Customer360CaseTests
{
    private sealed class Env : IAsyncDisposable
    {
        public TempDatabase Db = null!;
        public AppUnderTest App = null!;
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory = null!;
        public HttpClient Client = null!;
        public FakeCustomer360 Customer360 = new();
        public Guid Dept, LocalCustomer;
        public string Admin = AppUnderTest.Token("adm-1", "Admin User", new[] { "Admin" });

        public static async Task<Env> StartAsync(bool withIdentifierKeys = true)
        {
            var e = new Env { Db = await TempDatabase.CreateAsync() };
            using (var warm = new AppUnderTest(e.Db.ConnectionString)) await warm.ReadyClientAsync();

            await using (var ctx = e.Db.NewContext())
            {
                e.Dept = Guid.NewGuid(); e.LocalCustomer = Guid.NewGuid();
                ctx.Teams.Add(new Team { Id = e.Dept, Name = "Contact Center", Code = "CC", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
                ctx.AddRange(
                    new DepartmentSubCategory { Id = Guid.NewGuid(), DepartmentId = TestDepartments.IdFor(e.Dept), Name = "Fraud", Code = "F", IsActive = true, CreatedAt = DateTime.UtcNow },
                    new Customer { Id = e.LocalCustomer, FullName = "Ahmad Razak", IdType = "Passport Number", Passport = "A98765432", PhoneNumber = "+60 12-345 6789", PreferredLanguage = "English", CreatedAt = DateTime.UtcNow });
                await ctx.SaveChangesAsync();
                await TestDepartments.LinkTeamsAsync(ctx);
            }

            var settings = new Dictionary<string, string> { ["Customer360:BaseUrl"] = "http://c360.test" };
            if (withIdentifierKeys)
            {
                settings["CustomerIdentifiers:EncryptionKey"] = CustomerIdentifierProtectorTests.NewKey();
                settings["CustomerIdentifiers:HashKey"] = CustomerIdentifierProtectorTests.NewKey();
            }
            e.App = new AppUnderTest(e.Db.ConnectionString, settings);
            var fake = e.Customer360;
            e.Factory = e.App.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddHttpClient<Customer360Client>().ConfigurePrimaryHttpMessageHandler(() => fake)));
            e.Client = e.Factory.CreateClient();
            for (var i = 0; i < 100 && (await e.Client.GetAsync("/ready")).StatusCode != HttpStatusCode.OK; i++) await Task.Delay(100);
            return e;
        }

        public Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, object? body = null) => SendAs(Admin, method, url, body);

        /// <summary>A Host user (not an administrator) holding exactly <paramref name="permissions"/>, in the Host's own form.</summary>
        public static string Person(string subject, params string[] permissions) =>
            AppUnderTest.Token(subject, subject, extraClaims: new Dictionary<string, object>
            {
                ["perms"] = JsonSerializer.Serialize(permissions),
                ["administrator"] = "false",
            });

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAs(string token, HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return (response.StatusCode, doc.RootElement.Clone());
        }

        public Dictionary<string, object?> CaseBody(object? customer360 = null, Guid? customerId = null) => new()
        {
            ["title"] = "Card used abroad", ["description"] = "Customer did not travel.",
            ["customerId"] = customerId, ["customer360"] = customer360,
            ["departmentId"] = Dept, ["subcategory"] = "fraud", ["caseType"] = "complaint", ["preferredLanguage"] = "english",
            ["sourceChannel"] = "voice", ["preferredCommunicationChannel"] = "phone", ["severity"] = "Low",
        };

        public static object Aisha(string externalId = "P-1001") => new
        {
            customerType = "Individual", searchBy = "NRIC", value = "900101-01-5555", externalCustomerId = externalId,
        };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await App.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task A_case_for_a_Customer_360_customer_stores_identifiers_only_and_no_customer_record()
    {
        await using var env = await Env.StartAsync();
        var customersBefore = await env.Db.ScalarAsync<long>("SELECT count(*) FROM \"Customers\"");

        var (status, body) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));

        Assert.Equal(HttpStatusCode.Created, status);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal(customersBefore, await env.Db.ScalarAsync<long>("SELECT count(*) FROM \"Customers\""));
        await using var ctx = env.Db.NewContext();
        var stored = await ctx.Cases.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.Equal(CustomerSources.Customer360, stored.CustomerSource);
        Assert.Null(stored.CustomerId);
        Assert.Equal("P-1001", stored.ExternalCustomerId);
        Assert.Equal("NRIC", stored.CustomerLookupType);
        Assert.DoesNotContain("900101015555", stored.CustomerLookupKeyEncrypted);
        Assert.Equal(64, stored.CustomerLookupKeyHash!.Length);
        Assert.Equal("English", stored.PreferredLanguage);
        // Nothing of the profile anywhere on the row.
        var rowText = await env.Db.ScalarAsync<string>($"SELECT row_to_json(c)::text FROM \"Cases\" c WHERE \"Id\" = '{id}'");
        Assert.DoesNotContain("Aisha", rowText);
        Assert.DoesNotContain("aisha@example.test", rowText);
    }

    [PostgresFact]
    public async Task The_case_still_lists_and_opens_and_its_customer_is_read_live_from_Customer_360()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha("P-1002")));
        var id = created.GetProperty("id").GetGuid();

        var (listStatus, list) = await env.Send(HttpMethod.Get, "/api/cases?pageSize=10");
        var (detailStatus, detail) = await env.Send(HttpMethod.Get, $"/api/cases/{id}");
        var (_, customer) = await env.Send(HttpMethod.Get, $"/api/cases/{id}/customer360");

        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal("Customer360", detail.GetProperty("customerSource").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("customerId").ValueKind);
        Assert.Equal("Available", customer.GetProperty("status").GetString());
        Assert.Equal("P-1002", customer.GetProperty("customer").GetProperty("externalCustomerId").GetString());
        Assert.Contains(id.ToString(), list.ToString());
    }

    [PostgresFact]
    public async Task Lists_never_wait_on_Customer_360_and_names_come_separately_one_lookup_per_stored_identifier()
    {
        await using var env = await Env.StartAsync();
        var ids = new List<Guid>();
        foreach (var who in new[] { "P-1001", "P-1001", "P-1002" })   // two customers behind one NRIC, one of them twice
        {
            var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha(who)));
            ids.Add(created.GetProperty("id").GetGuid());
        }
        env.Customer360.Requests.Clear();

        var (listStatus, _) = await env.Send(HttpMethod.Get, "/api/cases?pageSize=50");
        var (dashStatus, _) = await env.Send(HttpMethod.Get, "/api/dashboard/summary");
        var callsForLists = env.Customer360.Requests.Count(r => r.PathAndQuery.Contains("profile"));

        var (status, names) = await env.Send(HttpMethod.Post, "/api/cases/customer-names", new { caseIds = ids });
        var profileCalls = env.Customer360.Requests.Count(r => r.PathAndQuery.Contains("profile"));
        var contactCalls = env.Customer360.Requests.Count(r => r.PathAndQuery.Contains("contactinfo"));
        var (_, again) = await env.Send(HttpMethod.Post, "/api/cases/customer-names", new { caseIds = ids });
        var profileCallsAfterRefresh = env.Customer360.Requests.Count(r => r.PathAndQuery.Contains("profile"));

        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(HttpStatusCode.OK, dashStatus);
        Assert.Equal(0, callsForLists);             // the board and the dashboard answer without Customer 360
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, profileCalls);              // one NRIC: one call names all three cases
        Assert.Equal(0, contactCalls);              // a name, not contact details
        Assert.Equal(1, profileCallsAfterRefresh);  // asked again moments later: answered from memory
        foreach (var id in ids)
        {
            // The same name, masked the same way, as the case's own customer section shows.
            var (_, live) = await env.Send(HttpMethod.Get, $"/api/cases/{id}/customer360");
            Assert.Equal(live.GetProperty("customer").GetProperty("fullName").GetString(), names.GetProperty(id.ToString()).GetString());
        }
        Assert.Equal(names.ToString(), again.ToString());

        // Nothing about the customer was written anywhere while naming them.
        var rowText = await env.Db.ScalarAsync<string>("SELECT coalesce(string_agg(row_to_json(c)::text, ''), '') FROM \"Cases\" c");
        Assert.DoesNotContain("Aisha", rowText);
    }

    [PostgresFact]
    public async Task When_Customer_360_cannot_name_customers_nothing_fails_and_it_is_not_retried_on_every_refresh()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        var id = created.GetProperty("id").GetGuid();
        env.Customer360.Override = r => r.RequestUri!.AbsolutePath.Contains("profile") ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null;
        env.Customer360.Requests.Clear();

        var (status, names) = await env.Send(HttpMethod.Post, "/api/cases/customer-names", new { caseIds = new[] { id } });
        var callsAfterFirst = env.Customer360.Requests.Count(r => r.PathAndQuery.Contains("profile"));
        var (status2, _) = await env.Send(HttpMethod.Post, "/api/cases/customer-names", new { caseIds = new[] { id } });
        var callsAfterSecond = env.Customer360.Requests.Count(r => r.PathAndQuery.Contains("profile"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.Empty(names.EnumerateObject());                     // the screen keeps "Customer 360 · Individual"
        Assert.True(callsAfterFirst >= 1);
        Assert.Equal(callsAfterFirst, callsAfterSecond);            // the failure is remembered briefly
    }

    [PostgresFact]
    public async Task Names_are_given_only_for_cases_the_caller_may_see()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        var id = created.GetProperty("id").GetGuid();
        var outsider = Env.Person("outsider-1", "remote.casemanagement.cases:View");   // no team, no ViewAll

        var (status, names) = await env.SendAs(outsider, HttpMethod.Post, "/api/cases/customer-names", new { caseIds = new[] { id } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(names.EnumerateObject());
    }

    private static readonly string[] CaseReader =
        ["remote.casemanagement.cases:View", "remote.casemanagement.cases:ViewAll", "remote.casemanagement.customers:View"];
    private const string Customer360Profiles = "remote.customer360.profile:View";

    [PostgresFact]
    public async Task Open_Customer_360_hands_the_cases_customer_over_once_and_only_to_the_person_who_asked()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha("P-1002")));
        var caseId = created.GetProperty("id").GetGuid();
        var agent = Env.Person("agent-1", [.. CaseReader, Customer360Profiles]);
        var colleague = Env.Person("agent-2", [.. CaseReader, Customer360Profiles]);
        env.Customer360.Requests.Clear();

        var (issued, first) = await env.SendAs(agent, HttpMethod.Post, $"/api/cases/{caseId}/customer360/handoff");
        var code = first.GetProperty("code").GetString()!;
        var (redeemed, target) = await env.SendAs(agent, HttpMethod.Post, "/api/customer360/handoff/redeem", new { code });
        var (again, _) = await env.SendAs(agent, HttpMethod.Post, "/api/customer360/handoff/redeem", new { code });

        Assert.Equal(HttpStatusCode.OK, issued);
        Assert.Empty(env.Customer360.Requests);                       // issuing a code asks Customer 360 nothing
        Assert.DoesNotContain("900101", first.ToString());            // the code says nothing about the customer
        Assert.Equal(HttpStatusCode.OK, redeemed);
        Assert.Equal("Individual", target.GetProperty("customerType").GetString());
        Assert.Equal("NRIC", target.GetProperty("idType").GetString());
        Assert.Equal("900101015555", target.GetProperty("value").GetString());
        Assert.Equal("P-1002", target.GetProperty("externalCustomerId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, again);                 // once only

        var (_, second) = await env.SendAs(agent, HttpMethod.Post, $"/api/cases/{caseId}/customer360/handoff");
        var code2 = second.GetProperty("code").GetString()!;
        var (byColleague, _) = await env.SendAs(colleague, HttpMethod.Post, "/api/customer360/handoff/redeem", new { code = code2 });
        var (afterColleague, _) = await env.SendAs(agent, HttpMethod.Post, "/api/customer360/handoff/redeem", new { code = code2 });
        Assert.Equal(HttpStatusCode.NotFound, byColleague);           // someone else's code is worthless to them …
        Assert.Equal(HttpStatusCode.NotFound, afterColleague);        // … and spent by the attempt, not left to guess at
    }

    [PostgresFact]
    public async Task Without_Customer_360_profile_access_no_code_is_issued_or_redeemed()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        var caseId = created.GetProperty("id").GetGuid();
        var caseOnly = Env.Person("agent-3", CaseReader);
        var withProfiles = Env.Person("agent-3", [.. CaseReader, Customer360Profiles]);

        var (issueStatus, _) = await env.SendAs(caseOnly, HttpMethod.Post, $"/api/cases/{caseId}/customer360/handoff");
        var (_, issued) = await env.SendAs(withProfiles, HttpMethod.Post, $"/api/cases/{caseId}/customer360/handoff");
        var (redeemStatus, _) = await env.SendAs(caseOnly, HttpMethod.Post, "/api/customer360/handoff/redeem",
            new { code = issued.GetProperty("code").GetString() });

        Assert.Equal(HttpStatusCode.Forbidden, issueStatus);
        Assert.Equal(HttpStatusCode.Forbidden, redeemStatus);
    }

    [PostgresFact]
    public async Task A_customer_just_chosen_on_Create_Case_can_be_opened_in_Customer_360_in_its_own_search_terms()
    {
        await using var env = await Env.StartAsync();
        var agent = Env.Person("agent-4", [.. CaseReader, Customer360Profiles]);

        var (_, issued) = await env.SendAs(agent, HttpMethod.Post, "/api/customer360/handoff", new
        {
            customerType = "Individual", searchBy = "SecondaryID:PASSPORT", value = " a1234567 ", externalCustomerId = "P-1001",
        });
        var (_, target) = await env.SendAs(agent, HttpMethod.Post, "/api/customer360/handoff/redeem", new { code = issued.GetProperty("code").GetString() });

        Assert.Equal("SECONDARYID", target.GetProperty("idType").GetString());
        Assert.Equal("PASSPORT", target.GetProperty("idSubtype").GetString());
        Assert.Equal("A1234567", target.GetProperty("value").GetString());
        Assert.Equal("P-1001", target.GetProperty("externalCustomerId").GetString());
    }

    [PostgresFact]
    public async Task A_customer_can_have_many_cases_and_a_local_customer_still_works_as_before()
    {
        await using var env = await Env.StartAsync();

        var (s1, first) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        var (s2, _) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        var (s3, local) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(customerId: env.LocalCustomer));

        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.Equal(HttpStatusCode.Created, s3);
        Assert.Equal(1L, await env.Db.ScalarAsync<long>(
            "SELECT count(DISTINCT \"CustomerLookupKeyHash\") FROM \"Cases\" WHERE \"CustomerSource\" = 'Customer360'"));
        var (_, localDetail) = await env.Send(HttpMethod.Get, $"/api/cases/{local.GetProperty("id").GetGuid()}");
        Assert.Equal("Local", localDetail.GetProperty("customerSource").GetString());
        Assert.Equal("Ahmad Razak", localDetail.GetProperty("customerName").GetString());
        var (_, related) = await env.Send(HttpMethod.Get, $"/api/cases/{first.GetProperty("id").GetGuid()}/related-customer-cases");
        if (related.ValueKind == JsonValueKind.Array) Assert.Single(related.EnumerateArray());   // the other case of the same customer
    }

    [PostgresFact]
    public async Task Both_kinds_at_once_an_unknown_match_or_no_customer_are_refused_on_the_customer_field()
    {
        await using var env = await Env.StartAsync();

        var (both, b1) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha(), env.LocalCustomer));
        var (unknown, b2) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha("P-9999")));
        var (none, b3) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody());

        foreach (var (status, body) in new[] { (both, b1), (unknown, b2), (none, b3) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("selectCustomer", body.ToString());
        }
        Assert.Equal(0L, await env.Db.ScalarAsync<long>("SELECT count(*) FROM \"Cases\""));
    }

    [PostgresFact]
    public async Task When_Customer_360_is_down_or_refuses_no_case_is_created_and_the_answer_says_why()
    {
        await using var env = await Env.StartAsync();
        await env.Send(HttpMethod.Get, "/api/customer360/search-options");   // the search types are cached; the profile calls fail

        env.Customer360.Override = r => r.RequestUri!.AbsolutePath.Contains("profile") ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        var (down, _) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        env.Customer360.Override = r => r.RequestUri!.AbsolutePath.Contains("profile") ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null;
        var (refused, _) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, down);
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal(0L, await env.Db.ScalarAsync<long>("SELECT count(*) FROM \"Cases\""));
    }

    [PostgresFact]
    public async Task Without_identifier_keys_a_Customer_360_case_is_refused_rather_than_stored_in_the_clear()
    {
        await using var env = await Env.StartAsync(withIdentifierKeys: false);

        var (status, _) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(0L, await env.Db.ScalarAsync<long>("SELECT count(*) FROM \"Cases\""));
    }

    [PostgresFact]
    public async Task A_customers_cases_are_listed_for_either_kind_of_customer_and_only_theirs()
    {
        await using var env = await Env.StartAsync();
        await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha("P-1001")));
        await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha("P-1001")));
        await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha("P-1002")));       // same NRIC, another customer
        await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(customerId: env.LocalCustomer));

        var (s1, aisha) = await env.Send(HttpMethod.Post, "/api/cases/for-customer",
            new { customerType = "Individual", externalCustomerId = "P-1001", page = 1, pageSize = 10 });
        var (_, local) = await env.Send(HttpMethod.Post, "/api/cases/for-customer", new { customerId = env.LocalCustomer });
        var (s3, _) = await env.Send(HttpMethod.Post, "/api/cases/for-customer", new { page = 1 });

        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal(2, aisha.GetProperty("totalCount").GetInt32());
        Assert.All(aisha.GetProperty("items").EnumerateArray(), c => Assert.Equal("P-1001", c.GetProperty("externalCustomerId").GetString()));
        Assert.Equal(1, local.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, s3);   // a customer must be named
    }

    [PostgresFact]
    public async Task A_whole_NRIC_finds_a_Customer_360_customers_cases_but_part_of_one_does_not()
    {
        await using var env = await Env.StartAsync();
        var (_, created) = await env.Send(HttpMethod.Post, "/api/cases", env.CaseBody(Env.Aisha()));
        var number = created.GetProperty("caseNumber").GetString();

        var (_, byDashedNric) = await env.Send(HttpMethod.Get, "/api/cases?pageSize=10&search=900101-01-5555");
        var (_, byPartial) = await env.Send(HttpMethod.Get, "/api/cases?pageSize=10&search=9001010");
        var (_, header) = await env.Send(HttpMethod.Get, "/api/search?q=900101015555");

        Assert.Equal(1, byDashedNric.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, byPartial.GetProperty("totalCount").GetInt32());
        Assert.Contains(number!, header.ToString());
    }

    [PostgresFact]
    public async Task The_database_refuses_a_case_with_a_half_customer_reference()
    {
        await using var env = await Env.StartAsync();
        await using var ctx = env.Db.NewContext();
        var owner = await ctx.Users.Select(u => u.Id).FirstOrDefaultAsync();

        // A Customer 360 case with no identifier, written directly: the check constraint stops it.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => env.Db.ScalarAsync<long>(
            "INSERT INTO \"Cases\" (\"Id\",\"CaseNumber\",\"CaseType\",\"Title\",\"Description\",\"Status\",\"Severity\",\"SourceChannel\",\"PreferredCommunicationChannel\",\"CommunicationChannel\",\"SlaStartTime\",\"SlaTargetHours\",\"SlaTotalPausedMinutes\",\"InternalResolutionTargetMinutes\",\"ExternalResolutionTargetMinutes\",\"SlaConfigVersion\",\"FirstResponseTargetMinutes\",\"FirstResponseStatus\",\"EscalationLevel\",\"SlaReminderSent\",\"DepartmentId\",\"OwnerId\",\"SubcaseType\",\"CreatedAt\",\"CustomerSource\",\"CustomerType\") " +
            $"VALUES (gen_random_uuid(),'X-1','Complaint','t','d',0,'Low','Voice','Phone','Voice',now(),1,0,1,1,1,1,'Pending',1,false,'{env.Dept}','{owner}','Original',now(),'Customer360','Individual') RETURNING 1"));
        Assert.Contains("CK_Cases_CustomerReference", error.ToString());
    }
}
