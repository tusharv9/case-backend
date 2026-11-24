namespace CaseManagement.Api.Services.Notifications;

using System.Security.Cryptography;
using System.Text;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OmniConnect.Outbox;

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
    string? Severity = null,
    string? Link = null);

/// <summary>Where notifications are handed: the dispatcher in production, a list in tests.</summary>
public interface ICaseNotificationSink
{
    void Enqueue(CaseNotificationRequest request);
}

/// <summary>
/// Turns a <see cref="CaseNotificationRequest"/> into the Notification Service's event (<c>POST internal/notifications</c>).
/// </summary>
/// <remarks>
/// <para><b>Translating people.</b> Case Management keeps its own user rows; the platform knows Host users. Each recipient is sent
/// by its <c>ExternalUserId</c>; someone with none (or inactive) simply is not told.</para>
/// <para><b>What a card says.</b> The case number, type, severity, team, status and escalation level — never the case title or
/// the customer, which a notification would carry further than the case screen does.</para>
/// <para><b>Never twice.</b> An event with an <see cref="CaseNotificationRequest.EventKey"/> gets an id derived from it, so a
/// repeat (or a retry) is recognised by the Notification Service and written once.</para>
/// </remarks>
public static class CaseNotificationBuilder
{
    /// <summary>The event as the Notification Service takes it, or null when none of its recipients is a Host user.</summary>
    public static async Task<NotificationEventContract?> BuildAsync(AppDbContext db, CaseNotificationRequest r, string appKey, DateTimeOffset occurredAt, CancellationToken ct)
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
        appKey = string.IsNullOrWhiteSpace(appKey) ? HostPermissionCatalog.DefaultAppKey : appKey.Trim().ToLowerInvariant();
        return new NotificationEventContract(
            Kind: $"remote.{appKey}.{CaseNotificationKinds.Module}:{r.Kind}",
            EventId: r.EventKey is { Length: > 0 } key ? DeterministicGuid($"{r.Kind}:{key}") : Guid.NewGuid(),
            Body: r.Body,
            Link: r.Link is { Length: > 0 } page ? $"/apps/{appKey}/{page}" : r.CaseId is { } id ? $"/apps/{appKey}/case-management/{id}" : null,
            EntityType: r.CaseId != null ? "Case" : null,
            EntityId: r.CaseId?.ToString(),
            SubjectUserIds: subjects,
            ExcludedUserIds: actor is { } ex ? [ex] : [],
            ActorUserId: actor,
            ActorName: r.ActorName,
            Severity: r.Severity,
            Values: values,
            OccurredAt: occurredAt);
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
