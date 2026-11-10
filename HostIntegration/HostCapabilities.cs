namespace CaseManagement.Api.HostIntegration;

using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>Why the caller could not be let in, with the status to answer — refused (401/403) or not known (503).</summary>
public sealed class HostAccessException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Asks AuthService what one user may do right now (<c>GET /internal/capabilities/{userId}</c>, guarded by this
/// service's own internal API key).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> The Host's token no longer carries <c>perms</c> or <c>administrator</c>: AuthService answers
/// them live, so a revoked permission stops working at once instead of when the token expires. Read from the token
/// alone, every caller — a Super Admin included — would hold nothing here.</para>
/// <para><b>Fails closed and says which way.</b> An unknown user is refused (401), a deactivated one 403, and an
/// AuthService that cannot be reached is an outage (503) — never a pass, and never "you have no permission".</para>
/// <para>Used only when <c>AuthService:InternalApiKey</c> is set; without it the token's own claims are read as
/// before (older Hosts, and the tests that sign tokens carrying them).</para>
/// <para><b>Reused briefly.</b> Asking costs AuthService two database reads, which every Case Management request used to
/// wait for. An answer is now reused for <see cref="AuthServiceOptions.CapabilityCacheSeconds"/> (default 15 s; 0 = ask every
/// time), and requests that arrive together (a page loads several at once) share one question. Only a granted answer is kept:
/// "unknown", "inactive" and "could not reach AuthService" are asked again on the next request. So a permission revoked in
/// the Host stops working here within that many seconds rather than at once.</para>
/// </remarks>
public sealed class HostCapabilitiesClient(HttpClient http, IOptions<AuthServiceOptions> options, ILogger<HostCapabilitiesClient> logger,
    Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<Answer>>> InFlight = new();
    private readonly AuthServiceOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BaseUrl) && !string.IsNullOrWhiteSpace(_options.InternalApiKey);

    public sealed record Answer(bool IsAdministrator, IReadOnlyList<string> Permissions);

    private sealed record Response(bool IsKnown, bool IsActive, bool IsAdministrator, IReadOnlyList<string>? Permissions);

    /// <exception cref="HostAccessException">The user is unknown or inactive, or AuthService could not answer.</exception>
    public async Task<Answer> ForUserAsync(string userId, CancellationToken ct)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(0, _options.CapabilityCacheSeconds));
        if (cache == null || ttl == TimeSpan.Zero) return await FetchAsync(userId, ct);

        var key = "host-capabilities:" + userId;
        if (cache.TryGetValue(key, out Answer? hit) && hit != null) return hit;

        // One question per user at a time: the requests a page fires together wait for the same answer. The shared fetch
        // is not tied to any one caller's cancellation (it has its own deadline).
        var shared = InFlight.GetOrAdd(key, _ => new Lazy<Task<Answer>>(() => FetchAsync(userId, CancellationToken.None)));
        try
        {
            var answer = await shared.Value.WaitAsync(ct);
            cache.Set(key, answer, ttl);
            return answer;
        }
        finally
        {
            if (shared.IsValueCreated && shared.Value.IsCompleted) InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<Answer>>>(key, shared));
        }
    }

    private async Task<Answer> FetchAsync(string userId, CancellationToken ct)
    {
        Response? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl!.TrimEnd('/')}/internal/capabilities/{Uri.EscapeDataString(userId)}");
            request.Headers.Add("X-Internal-Api-Key", _options.InternalApiKey);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Deadline);

            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("AuthService answered {StatusCode} for the permissions of user {UserId}.", (int)response.StatusCode, userId);
                throw Unavailable();
            }
            body = await response.Content.ReadFromJsonAsync<Response>(cancellationToken: deadline.Token);
        }
        catch (HostAccessException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach AuthService for the permissions of user {UserId}.", userId);
            throw Unavailable();
        }

        if (body is null) throw Unavailable();
        if (!body.IsKnown) throw new HostAccessException(StatusCodes.Status401Unauthorized, "User not found or unauthorized.");
        if (!body.IsActive) throw new HostAccessException(StatusCodes.Status403Forbidden, "This user account is inactive.");
        return new Answer(body.IsAdministrator, body.Permissions ?? []);
    }

    private static HostAccessException Unavailable() =>
        new(StatusCodes.Status503ServiceUnavailable, "Case Management could not check your permissions with OmniConnect. Try again in a moment.");
}
