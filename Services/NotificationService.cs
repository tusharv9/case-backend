namespace CaseManagement.Api.Services;

using CaseManagement.Api.Configuration;
using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Case Management's notifications, raised into OmniConnect's central notification system (see <see cref="INotificationService"/>).
/// Every call only queues the event (<see cref="ICaseNotificationSink"/>): the request is never slowed by AuthService, and a
/// notification problem never fails the action that raised it.
/// </summary>
public class NotificationService : INotificationService
{
    private readonly AppDbContext _context;
    private readonly IPermissionProvider _permissions;
    private readonly NotificationOptions _options;
    private readonly ICaseNotificationSink _sink;
    private readonly ICurrentUserAccessor? _currentUser;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AppDbContext context, IPermissionProvider permissions, IOptions<NotificationOptions> options, ICaseNotificationSink sink,
        ICurrentUserAccessor? currentUser = null, ILogger<NotificationService>? logger = null)
    {
        _context = context;
        _permissions = permissions;
        _options = options.Value;
        _sink = sink;
        _currentUser = currentUser;
        _logger = logger ?? NullLogger<NotificationService>.Instance;
    }

    /// <summary>Who is acting, as the Host knows them; none for the background monitors.</summary>
    private (string? Id, string? Name) Actor()
    {
        try
        {
            var p = _currentUser?.Principal;
            return (p?.ExternalUserId, p?.Name);
        }
        catch
        {
            return (null, null);
        }
    }

    private void Enqueue(CaseNotificationRequest request)
    {
        try { _sink.Enqueue(request); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not queue notification {Kind}.", request.Kind); }
    }

    public Task CreateNotificationAsync(Guid recipientUserId, string type, string title, string message, Guid? caseId = null, string? caseNumber = null, string priority = "High", int reminderCount = 0, CancellationToken ct = default, string? eventKey = null)
    {
        if (!CaseNotificationKinds.ByLegacyType.TryGetValue(type, out var kind))
        {
            _logger.LogWarning("Notification type {Type} has no declared kind; not sent.", type);
            return Task.CompletedTask;
        }

        var (actorId, actorName) = Actor();
        var values = new Dictionary<string, string?>();
        string? body = null;
        if (kind == CaseNotificationKinds.NeedsAgent)
            values["reason"] = eventKey?.StartsWith("departed:", StringComparison.Ordinal) == true
                ? "Its owner was deleted in OmniConnect. Assign it to someone."
                : "No agent could take it, so the team lead holds it. Assign it to someone.";
        if (kind == CaseNotificationKinds.Nudge) body = message;

        Enqueue(new CaseNotificationRequest(kind, [recipientUserId], caseId, caseNumber, actorId, actorName,
            eventKey is null ? null : $"{recipientUserId}:{eventKey}", values, body));
        return Task.CompletedTask;
    }

    public Task CreateBulkReassignedNotificationAsync(Guid recipientUserId, IReadOnlyList<(Guid CaseId, string CaseNumber)> cases, CancellationToken ct = default)
    {
        if (cases.Count == 0) return Task.CompletedTask;
        if (cases.Count == 1) return CreateNotificationAsync(recipientUserId, "CASE_REASSIGNED", "", "", cases[0].CaseId, cases[0].CaseNumber, ct: ct);

        var (actorId, actorName) = Actor();
        const int shown = 5;
        var numbers = cases.Select(c => c.CaseNumber).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var list = string.Join(", ", numbers.Take(shown)) + (numbers.Count > shown ? $" and {numbers.Count - shown} more" : "");
        Enqueue(new CaseNotificationRequest(CaseNotificationKinds.ReassignedMany, [recipientUserId], ActorExternalId: actorId, ActorName: actorName,
            Values: new Dictionary<string, string?> { ["count"] = cases.Count.ToString(), ["cases"] = list }));
        return Task.CompletedTask;
    }

    public async Task PublishSlaBreachNotificationsAsync(IReadOnlyList<SlaBreachCandidate> breached, DateTime now, CancellationToken ct = default)
    {
        if (breached.Count == 0) return;
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, _options.BreachReminderCooldownMinutes));
        var maxReminders = Math.Max(0, _options.BreachMaxReminders);
        var groupAt = _options.BreachGroupingThreshold > 0 ? Math.Max(1, _options.BreachGroupingThreshold) : int.MaxValue;

        var ids = breached.Select(b => b.Id).ToList();
        var alerts = await _context.CaseSlaAlerts.Where(a => ids.Contains(a.CaseId)).ToDictionaryAsync(a => a.CaseId, ct);

        foreach (var owner in breached.GroupBy(b => b.OwnerId))
        {
            // A case is newly breached when nothing was said about it in this SLA cycle.
            var fresh = owner.Where(b => !alerts.TryGetValue(b.Id, out var a) || a.CycleStart != b.SlaStartTime).ToList();
            foreach (var b in fresh)
            {
                if (alerts.TryGetValue(b.Id, out var old)) { old.CycleStart = b.SlaStartTime; old.NotifiedAt = now; old.Reminders = 0; old.LastReminderAt = null; }
                else _context.CaseSlaAlerts.Add(alerts[b.Id] = new CaseSlaAlert { CaseId = b.Id, CycleStart = b.SlaStartTime, NotifiedAt = now });
            }

            if (fresh.Count >= groupAt)
            {
                var key = string.Join(",", fresh.Select(b => b.Id).OrderBy(x => x));
                Enqueue(new CaseNotificationRequest(CaseNotificationKinds.SlaBreachedMany, [owner.Key], EventKey: $"{owner.Key}:{key}",
                    Values: new Dictionary<string, string?> { ["count"] = fresh.Count.ToString() }, Severity: "Critical"));
            }
            else
            {
                foreach (var b in fresh)
                    Enqueue(new CaseNotificationRequest(CaseNotificationKinds.SlaBreached, [owner.Key], b.Id, b.CaseNumber,
                        EventKey: $"{b.Id}:{b.SlaStartTime:O}", Severity: "Critical"));
            }

            // Reminders, on a cooldown, for cases already reported in this cycle.
            foreach (var b in owner.Except(fresh))
            {
                var a = alerts[b.Id];
                var last = a.LastReminderAt ?? a.NotifiedAt;
                if (a.Reminders >= maxReminders || now - last < cooldown) continue;
                a.Reminders++;
                a.LastReminderAt = now;
                Enqueue(new CaseNotificationRequest(CaseNotificationKinds.SlaReminder, [owner.Key], b.Id, b.CaseNumber,
                    EventKey: $"{b.Id}:{b.SlaStartTime:O}:{a.Reminders}",
                    Values: new Dictionary<string, string?> { ["reminder"] = $"{a.Reminders} of {maxReminders}" }, Severity: "Warning"));
            }
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task CreateConfigChangedNotificationAsync(string title, string message, CancellationToken ct = default)
    {
        try
        {
            // Only the people who can change configuration hear about it (the roles the Host grants that permission to).
            var roles = _permissions.RolesGranting(Permissions.ConfigManage);
            var managers = _context.Users.AsNoTracking().Where(u => u.IsActive && u.ExternalUserId != null);
            if (roles != null)
            {
                var roleList = roles.Select(r => r.ToLower()).ToList();
                managers = managers.Where(u => roleList.Contains(u.Role.ToLower()));
            }
            var ids = await managers.Select(u => u.Id).ToListAsync(ct);
            if (ids.Count == 0) return;

            var (actorId, actorName) = Actor();
            Enqueue(new CaseNotificationRequest(CaseNotificationKinds.SettingsChanged, ids, ActorExternalId: actorId, ActorName: actorName,
                Values: new Dictionary<string, string?> { ["detail"] = message }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not raise the settings-changed notification.");
        }
    }
}
