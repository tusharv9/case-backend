namespace CaseManagement.Api.HostIntegration;

using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
/// The platform's one time zone, as the Host manages it (Settings → Manage Time Zone; AuthService
/// <c>GET internal/system-time-zone</c>, service key only). Case Management keeps no time zone of its own any more: SLA
/// clocks, business hours, holidays, escalation, ticket deadlines and every time written into a message follow this one.
/// </summary>
/// <remarks>
/// Read at most once a minute and remembered. When the Host cannot be reached (or this service runs standalone) the answer
/// is null and the caller falls back to the last zone it stored, so an SLA never stops because the Host is briefly away.
/// </remarks>
public interface IHostTimeZone
{
    /// <summary>The Host's IANA zone (e.g. <c>Asia/Kuala_Lumpur</c>), or null when it cannot be read right now.</summary>
    Task<string?> GetAsync(CancellationToken ct = default);
}

public sealed class HostTimeZone(HttpClient http, IOptions<AuthServiceOptions> authOptions, IMemoryCache cache, ILogger<HostTimeZone> logger) : IHostTimeZone
{
    private const string Key = "host-time-zone";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private sealed record Dto(string? TimeZone);

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key, out string? known)) return known;

        var options = authOptions.Value;
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.InternalApiKey)) return null;   // standalone

        string? zone = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/internal/system-time-zone");
            request.Headers.Add("X-Internal-Api-Key", options.InternalApiKey);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            using var response = await http.SendAsync(request, deadline.Token);
            if (response.IsSuccessStatusCode)
                zone = Known((await response.Content.ReadFromJsonAsync<Dto>(cancellationToken: deadline.Token))?.TimeZone);
            else
                logger.LogWarning("AuthService answered {Status} for the system time zone.", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not read the system time zone from AuthService; using the stored one."); }

        cache.Set(Key, zone, zone == null ? RetryAfter : CacheFor);
        return zone;
    }

    /// <summary>The id when this server knows the zone, otherwise null (never a zone it could not convert with).</summary>
    private static string? Known(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); return id.Trim(); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}
