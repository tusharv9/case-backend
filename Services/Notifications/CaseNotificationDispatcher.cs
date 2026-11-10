namespace CaseManagement.Api.Services.Notifications;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// One Case Management event to report, as the code that raised it knows it: Case Management's own user ids, the case it is
/// about, and who did it. Turned into AuthService's shape by <see cref="CaseNotificationDispatcher"/>, off the request thread.
/// </summary>
/// <param name="Kind">A key of <see cref="CaseNotificationKinds"/>.</param>
/// <param name="RecipientLocalIds">The people it is about, as Case Management users (translated to Host ids before sending).</param>
/// <param name="EventKey">Same key, same event: a repeat is never shown twice. Null: a new event every time.</param>
/// <param name="Values">Extra template values beyond what the case supplies (<c>count</c>, <c>reason</c>, <c>reminder</c>…).</param>
/// <param name="Body">The card's second line for a kind declared without a context template (a nudge's own words).</param>
public sealed record CaseNotificationRequest(
    string Kind,
    IReadOnlyList<Guid> RecipientLocalIds,
    Guid? CaseId = null,
    string? CaseNumber = null,
    string? ActorExternalId = null,
    string? ActorName = null,
    string? EventKey = null,
    IReadOnlyDictionary<string, string?>? Values = null,
    string? Body = null,
    string? Severity = null);

/// <summary>Where notifications are handed: the dispatcher in production, a list in tests.</summary>
public interface ICaseNotificationSink
{
    void Enqueue(CaseNotificationRequest request);
}

/// <summary>One notification in AuthService's <c>POST internal/notifications</c> shape (matched by property name).</summary>
public sealed record HostNotification(
    string Kind,
    Guid EventId,
    string? Body,
    string? Link,
    string? EntityType,
    string? EntityId,
    IReadOnlyList<Guid> SubjectUserIds,
    IReadOnlyList<Guid> ExcludedUserIds,
    Guid? ActorUserId,
    string? ActorName,
    string? Severity,
    IReadOnlyDictionary<string, string?> Values);

public enum HostNotificationResult { Accepted, Rejected, Retry }

/// <summary>Sends one notification to AuthService with this service's own internal key (<c>AuthService:BaseUrl</c>, <c>AuthService:InternalApiKey</c>).</summary>
public class HostNotificationsClient(HttpClient http, IOptions<AuthServiceOptions> options, ILogger<HostNotificationsClient> logger)
{
    public virtual async Task<HostNotificationResult> SendAsync(HostNotification n, CancellationToken ct = default)
    {
        var baseUrl = (options.Value.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var key = options.Value.InternalApiKey?.Trim();
        if (baseUrl.Length == 0 || string.IsNullOrEmpty(key)) return HostNotificationResult.Rejected;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/internal/notifications")
            {
                Content = JsonContent.Create(new
                {
                    n.Kind, n.EventId, n.Body, n.Link, n.Severity, n.EntityType, n.EntityId,
                    n.SubjectUserIds, n.ExcludedUserIds, n.ActorUserId, n.ActorName, n.Values,
                    SourceService = "CaseManagement",
                }),
            };
            request.Headers.TryAddWithoutValidation("X-Internal-Api-Key", key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode) return HostNotificationResult.Accepted;

            var status = (int)response.StatusCode;
            var retry = status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
            logger.LogWarning("AuthService answered {Status} to notification {Kind} ({EventId}){Retry}.",
                status, n.Kind, n.EventId, retry ? "; will retry" : "");
            return retry ? HostNotificationResult.Retry : HostNotificationResult.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not reach AuthService for notification {Kind} ({EventId}): {Message}; will retry.", n.Kind, n.EventId, ex.Message);
            return HostNotificationResult.Retry;
        }
    }
}

/// <summary>
/// Turns queued <see cref="CaseNotificationRequest"/>s into AuthService notifications and sends them in the background, so a
/// case action is never slowed or failed by a notification.
/// </summary>
/// <remarks>
/// <para><b>Translating people.</b> Case Management keeps its own user rows; AuthService knows Host users. Each recipient is sent
/// by its <c>ExternalUserId</c>; someone with none (or the Host does not know) simply is not told.</para>
/// <para><b>What a card says.</b> The case number, type, severity, team, status and escalation level — never the case title or
/// the customer, which a notification would carry further than the case screen does.</para>
/// <para><b>Never twice.</b> An event with an <see cref="CaseNotificationRequest.EventKey"/> gets an id derived from it, so a
/// repeat (or a retry) is recognised by AuthService and written once. A failed send is retried with backoff (1 s, 5 s, 30 s,
/// 2 min, 10 min) with the same id.</para>
/// <para><b>Known gap, accepted</b> (as in Lead Management): what is still queued when the service stops is lost.</para>
/// </remarks>
public sealed class CaseNotificationDispatcher(IServiceScopeFactory scopes, IOptions<HostIntegrationOptions> hostOptions, ILogger<CaseNotificationDispatcher> logger)
    : BackgroundService, ICaseNotificationSink
{
    internal static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
    ];

    private sealed record Pending(CaseNotificationRequest? Request, HostNotification? Built, int Attempt);

    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
        dropped => logger.LogWarning("The notification queue is full; {Kind} was dropped.", dropped.Built?.Kind ?? dropped.Request?.Kind));

    public void Enqueue(CaseNotificationRequest request)
    {
        if (request.RecipientLocalIds.Count == 0) return;
        queue.Writer.TryWrite(new Pending(request, null, 0));
    }

    private string AppKey => string.IsNullOrWhiteSpace(hostOptions.Value.AppKey) ? "casemanagement" : hostOptions.Value.AppKey.Trim().ToLowerInvariant();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var pending in queue.Reader.ReadAllAsync(stoppingToken))
        {
            HostNotificationResult result;
            var built = pending.Built;
            try
            {
                using var scope = scopes.CreateScope();
                built ??= await BuildAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), pending.Request!, stoppingToken);
                if (built == null) continue;   // nobody the Host knows
                result = await scope.ServiceProvider.GetRequiredService<HostNotificationsClient>().SendAsync(built, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sending notification {Kind} failed.", built?.Kind ?? pending.Request?.Kind);
                result = HostNotificationResult.Retry;
            }

            if (result != HostNotificationResult.Retry) continue;
            if (pending.Attempt >= Backoff.Length)
            {
                logger.LogWarning("Gave up on notification {Kind} after {Attempts} attempts.", built?.Kind ?? pending.Request?.Kind, pending.Attempt + 1);
                continue;
            }
            _ = RequeueLaterAsync(new Pending(pending.Request, built, pending.Attempt + 1), Backoff[pending.Attempt], stoppingToken);
        }
    }

    private async Task RequeueLaterAsync(Pending pending, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            queue.Writer.TryWrite(pending);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The request in AuthService's shape, or null when none of its recipients is a Host user.</summary>
    public async Task<HostNotification?> BuildAsync(AppDbContext db, CaseNotificationRequest r, CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking()
            .Where(u => r.RecipientLocalIds.Contains(u.Id) && u.ExternalUserId != null && u.IsActive)
            .Select(u => new { u.ExternalUserId, u.Name })
            .ToListAsync(ct);
        var subjects = people.Select(p => Guid.TryParse(p.ExternalUserId, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        if (subjects.Count == 0) return null;

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (r.CaseId is { } caseId)
        {
            var c = await db.Cases.AsNoTracking().Where(x => x.Id == caseId)
                .Select(x => new { x.CaseNumber, x.CaseType, x.Severity, Team = x.Team.Name, x.Status, Owner = x.Owner.Name, x.EscalationLevel })
                .FirstOrDefaultAsync(ct);
            if (c != null)
            {
                values["record"] = c.CaseNumber;
                values["caseType"] = c.CaseType;
                values["severity"] = c.Severity;
                values["team"] = c.Team;
                values["status"] = StatusLabel(c.Status);
                values["owner"] = c.Owner;
                values["level"] = c.EscalationLevel > 0 ? $"Level {c.EscalationLevel}" : null;
            }
        }
        values.TryAdd("record", r.CaseNumber);
        values["person"] = people.Count == 1 ? people[0].Name : null;
        foreach (var (k, v) in r.Values ?? new Dictionary<string, string?>()) values[k] = v;

        Guid? actor = Guid.TryParse(r.ActorExternalId, out var a) ? a : null;
        return new HostNotification(
            Kind: $"remote.{AppKey}.{CaseNotificationKinds.Module}:{r.Kind}",
            EventId: r.EventKey is { Length: > 0 } key ? DeterministicGuid($"{r.Kind}:{key}") : Guid.NewGuid(),
            Body: r.Body,
            Link: r.CaseId is { } id ? $"/apps/{AppKey}/case-management/{id}" : null,
            EntityType: r.CaseId != null ? "Case" : null,
            EntityId: r.CaseId?.ToString(),
            SubjectUserIds: subjects,
            ExcludedUserIds: actor is { } ex ? [ex] : [],
            ActorUserId: actor,
            ActorName: r.ActorName,
            Severity: r.Severity,
            Values: values);
    }

    private static string StatusLabel(Models.CaseStatus s) => s switch
    {
        Models.CaseStatus.InProgress => "In Progress",
        Models.CaseStatus.WaitingOnCustomer => "Waiting on Customer",
        _ => s.ToString(),
    };

    /// <summary>The same text always gives the same id (how AuthService recognises a repeat of one event).</summary>
    public static Guid DeterministicGuid(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return new Guid(hash[..16]);
    }
}
