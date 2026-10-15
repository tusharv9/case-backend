namespace CaseManagement.Api.Services.Customer360;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

/// <summary>Where Customer 360 is, bound from the <c>Customer360</c> section (<c>Customer360__BaseUrl</c> in .env).</summary>
public class Customer360Options
{
    public const string SectionName = "Customer360";

    /// <summary>Customer360Service's base URL, e.g. http://localhost:5059. No default: blank means "not configured".</summary>
    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>Extra attempts after a timeout or 5xx. One by default: an operator is waiting.</summary>
    public int RetryCount { get; set; } = 1;
    /// <summary>How long Customer 360's list of search types is reused.</summary>
    public int LookupsCacheSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>Why a Customer 360 read failed, in terms the person using the form can act on.</summary>
public enum Customer360Failure { NotConfigured, NotPermitted, Unavailable, Rejected }

public sealed class Customer360Exception(Customer360Failure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public Customer360Failure Failure { get; } = failure;
}

/// <summary>
/// HTTP to Customer360Service, with the caller's own token.
/// </summary>
/// <remarks>
/// <para><b>The caller's token is passed through</b>, as Lead Management does: Customer 360 enforces
/// <c>profile:View</c> / <c>contact:View</c> against that person and writes one audit row per lookup naming
/// them. A service identity would hide who looked up whom, so there is none.</para>
///
/// <para>No caching of profiles: every read is an audited access on Customer 360's side, and a cached copy
/// would also be a second, unaudited store of the customer's data.</para>
/// </remarks>
public sealed class Customer360Client(
    HttpClient http,
    IHttpContextAccessor httpContext,
    IOptions<Customer360Options> options,
    ILogger<Customer360Client> logger)
{
    private readonly Customer360Options _options = options.Value;

    /// <summary>
    /// GETs <paramref name="pathAndQuery"/> and returns the parsed body, or null for 404 (Customer 360's
    /// "no such customer"). Throws <see cref="Customer360Exception"/> for everything else that is not a success.
    /// </summary>
    public async Task<JsonDocument?> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            throw new Customer360Exception(Customer360Failure.NotConfigured,
                "Customer 360 is not connected to Case Management yet (Customer360:BaseUrl is not set).");

        var url = new Uri(new Uri(_options.BaseUrl!.TrimEnd('/') + "/"), pathAndQuery);
        var authorization = httpContext.HttpContext?.Request.Headers.Authorization.ToString();
        var attempts = 1 + Math.Max(0, _options.RetryCount);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
            try
            {
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    try { return JsonDocument.Parse(body); }
                    catch (JsonException ex)
                    {
                        throw new Customer360Exception(Customer360Failure.Unavailable, "Customer 360 answered with something Case Management could not read.", ex);
                    }
                }

                var status = (int)response.StatusCode;
                // Path only: the query carries the identifier being looked up, which never goes into a log.
                logger.LogWarning("Customer 360 {Path} answered {Status} (attempt {Attempt}/{Attempts}).", url.AbsolutePath, status, attempt, attempts);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.NotFound:
                        return null;
                    case HttpStatusCode.Unauthorized:
                    case HttpStatusCode.Forbidden:
                        throw new Customer360Exception(Customer360Failure.NotPermitted,
                            "You do not have access to look up customers in Customer 360. Ask your administrator for Customer 360 profile access.");
                    case HttpStatusCode.BadRequest:
                        throw new Customer360Exception(Customer360Failure.Rejected,
                            "Customer 360 did not accept that search. Check the identifier and try again.");
                }
                if (status < 500 || attempt >= attempts)
                    throw new Customer360Exception(Customer360Failure.Unavailable, "Customer 360 is currently unavailable. Please try again shortly.");
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Customer 360 {Path} did not answer in time (attempt {Attempt}/{Attempts}).", url.AbsolutePath, attempt, attempts);
                if (attempt >= attempts)
                    throw new Customer360Exception(Customer360Failure.Unavailable, "Customer 360 did not answer in time. Please try again shortly.", ex);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Customer 360 {Path} could not be reached (attempt {Attempt}/{Attempts}): {Error}.", url.AbsolutePath, attempt, attempts, ex.Message);
                if (attempt >= attempts)
                    throw new Customer360Exception(Customer360Failure.Unavailable, "Customer 360 is currently unavailable. Please try again shortly.", ex);
            }
        }
    }
}
