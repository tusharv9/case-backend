namespace CaseManagement.Api.HostIntegration;

using System.Net;
using System.Text.Json;
using CaseManagement.Api.DTOs;
using Microsoft.Extensions.Options;

/// <summary>
/// A person's sign-ins and sign-outs, from the Host's own audit log (AuthService <c>GET /api/audit-logs</c>), read with the
/// VIEWER's token. Case Management never records sign-ins itself: the Host does, and it shows them only to people it allows
/// (System Audit Logs: View). Someone without that access is told so, rather than shown an empty history as if nothing happened.
/// </summary>
public sealed class HostSignInHistoryClient(HttpClient http, IHttpContextAccessor httpContext, IOptions<AuthServiceOptions> options, ILogger<HostSignInHistoryClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record HostPage(List<Item> Items, int Total, int Page, int PageSize);
    private sealed record Item(Guid Id, DateTimeOffset OccurredAt, Guid? ActorUserId, string? ActorName, string Action, string Result, string? AuthMethod, string? FailureReason);

    /// <summary>Thrown when the history cannot be shown; <see cref="Status"/> 403 = the viewer may not read it, 503 = the Host did not answer.</summary>
    public sealed class UnavailableException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public async Task<PagedResponseDto<CaseAuditEventDto>> ListAsync(string hostUserId, Guid caseUserId, DateTime? from, DateTime? to, int page, int pageSize, CancellationToken ct)
    {
        var baseUrl = options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new UnavailableException(503, "Sign-in history comes from the Host, which is not connected (AuthService:BaseUrl is not set).");

        // "auth.log" matches the Host's auth.login_succeeded, auth.login_failed and auth.logout.
        var query = $"api/audit-logs?ActorUserId={Uri.EscapeDataString(hostUserId)}&Action=auth.log&page={Math.Max(page, 1)}&pageSize={Math.Clamp(pageSize, 1, 50)}&sortDir=desc";
        if (from.HasValue) query += $"&From={Uri.EscapeDataString(DateTime.SpecifyKind(from.Value, DateTimeKind.Utc).ToString("O"))}";
        if (to.HasValue) query += $"&To={Uri.EscapeDataString(DateTime.SpecifyKind(to.Value, DateTimeKind.Utc).ToString("O"))}";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), query));
        var authorization = httpContext.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, timeout.Token); }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("The Host audit log could not be reached: {Error}", ex.Message);
            throw new UnavailableException(503, "The Host's sign-in history is unavailable. Please try again shortly.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UnavailableException(403, "Sign-in history comes from the Host's audit log. Viewing it needs System Audit Logs access in the Host; ask your administrator.");
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The Host audit log answered {Status}.", (int)response.StatusCode);
                throw new UnavailableException(503, "The Host's sign-in history is unavailable. Please try again shortly.");
            }
            var body = JsonSerializer.Deserialize<HostPage>(await response.Content.ReadAsStringAsync(ct), Json) ?? new HostPage(new(), 0, page, pageSize);
            return new PagedResponseDto<CaseAuditEventDto>
            {
                Items = body.Items.Select(i => Map(i, caseUserId)).ToList(),
                TotalCount = body.Total,
                Page = body.Page,
                PageSize = body.PageSize,
            };
        }
    }

    private static CaseAuditEventDto Map(Item i, Guid caseUserId)
    {
        var (type, label, text) = i.Action switch
        {
            "auth.logout" => ("SIGN_OUT", "Signed out", "Signed out of OmniConnect"),
            "auth.login_failed" => ("SIGN_IN_FAILED", "Sign-in failed", "Sign-in attempt failed" + (string.IsNullOrWhiteSpace(i.FailureReason) ? "" : $" ({i.FailureReason})")),
            _ => ("SIGN_IN", "Signed in", "Signed in to OmniConnect" + (string.IsNullOrWhiteSpace(i.AuthMethod) ? "" : $" ({i.AuthMethod})")),
        };
        return new CaseAuditEventDto
        {
            Id = i.Id, Timestamp = i.OccurredAt.UtcDateTime, ActionType = type, ActionLabel = label,
            ActorId = caseUserId, ActorName = i.ActorName ?? string.Empty, Description = text,
            Status = i.Result, Module = "Sign-in (Host)",
        };
    }
}
