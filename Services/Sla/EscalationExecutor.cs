namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Notifications;
using Microsoft.EntityFrameworkCore;

/// <summary>One escalation to carry out, manual or automatic, already decided and resolved.</summary>
/// <param name="NewOwnerId">The person who takes the case over; null when the escalation only notifies.</param>
public sealed record EscalationCommand(
    string Type,
    EscalationTarget Target,
    Guid? NewOwnerId,
    string ReasonText,
    string? Comment = null,
    string? Trigger = null,
    decimal? SlaConsumedPercent = null,
    Guid? ActorUserId = null,
    string? ActorName = null,
    string? ActorExternalId = null);

public interface IEscalationExecutor
{
    /// <summary>
    /// Carries out an escalation of the tracked case in one transaction: claims the move from the level the case was read at
    /// (of two escalations at once, the second is refused with <see cref="CaseHandleException"/>), hands the case over when there
    /// is a new owner, writes the history row and the timeline entry, and queues the notifications in the outbox.
    /// </summary>
    Task<CaseEscalation> ApplyAsync(Case c, EscalationCommand command, CancellationToken ct = default);
}

/// <param name="notifications">The notification outbox; without one (some unit tests) the escalation raises no notification.</param>
public sealed class EscalationExecutor(AppDbContext db, ICaseAudience? audience = null, NotificationOutboxStore? notifications = null) : IEscalationExecutor
{
    public async Task<CaseEscalation> ApplyAsync(Case c, EscalationCommand command, CancellationToken ct = default)
    {
        var level = command.Target.Level;
        var fromLevel = c.EscalationLevel;
        var cycle = c.EscalationCycle;
        var now = DateTime.UtcNow;
        var previousOwnerId = c.OwnerId;
        var previousOwner = await db.Users.AsNoTracking().Where(u => u.Id == previousOwnerId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "the previous holder";
        var recipients = command.Target.Recipients.Select(r => r.User).ToList();
        var newOwner = command.NewOwnerId is { } ownerId ? recipients.FirstOrDefault(u => u.Id == ownerId) : null;
        var automatic = command.Type == EscalationTypes.Automatic;

        // The people told: whoever now holds the case, and the other recipients who may open it (a role holder without access
        // to the case would only be shown a number that answers "not found").
        var told = recipients.Select(u => u.Id).Where(id => id != newOwner?.Id).ToList();
        if (audience != null && told.Count > 0) told = (await audience.WhoCanSeeAsync(c.Id, told, ct)).ToList();
        if (newOwner != null) told.Insert(0, newOwner.Id);

        var names = recipients.Count == 0 ? "nobody" : string.Join(", ", recipients.Select(u => u.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        var reached = level.Name + (EscalationLevelRules.RoleNames(level) is { Count: > 0 } roles ? $" ({string.Join(", ", roles)})" : "");
        var message = automatic
            ? $"[Automatic Escalation] Reached {reached}. Trigger: {command.Trigger}. Reason: {command.ReasonText}."
            : $"[Manual Escalation] Case escalated to {reached} from {previousOwner} by {command.ActorName ?? "a user"}. Reason: {command.ReasonText}.";
        message += newOwner != null ? $" Now held by {newOwner.Name}." : $" Escalated to: {names}.";
        if (!string.IsNullOrWhiteSpace(command.Comment)) message += $" Note: {command.Comment.Trim()}";
        if (command.Target.FallbackNote is { } fallback) message += $" {fallback}";

        var caseEvent = new CaseEvent
        {
            Id = Guid.NewGuid(), CaseId = c.Id, EventType = EventType.Escalate,
            ActionType = automatic ? "AUTO_ESCALATION" : "MANUAL_ESCALATION", Module = "SlaEscalation",
            OldValue = $"{previousOwner} · {(fromLevel > 0 ? $"Level {fromLevel}" : "Not escalated")}",
            NewValue = $"{newOwner?.Name ?? names} · {level.Name}",
            Message = message, IsInternal = true, CreatedAt = now,
            UserId = command.ActorUserId ?? previousOwnerId,
        };
        var history = new CaseEscalation
        {
            CaseId = c.Id, Cycle = cycle, FromLevel = fromLevel, ToLevel = level.LevelNumber, ToLevelName = level.Name, Type = command.Type,
            Trigger = command.Trigger, SlaConsumedPercent = command.SlaConsumedPercent, ReasonText = Cut(command.ReasonText, 1000) ?? "",
            Comment = Cut(command.Comment?.Trim(), 2000), FromOwnerId = previousOwnerId, ToOwnerId = newOwner?.Id,
            RecipientIds = string.Join(',', recipients.Select(u => u.Id)), RecipientNames = names,
            FallbackNote = Cut(command.Target.FallbackNote, 1000), ActorUserId = command.ActorUserId, CaseEventId = caseEvent.Id, CreatedAt = now,
        };

        var values = new Dictionary<string, string?>
        {
            ["level"] = level.Name, ["reason"] = command.ReasonText, ["previous"] = previousOwner,
            ["escalatedAt"] = now.ToString("u"),
        };
        var raised = told.Distinct().Select(id => new CaseNotificationRequest(
            automatic ? CaseNotificationKinds.AutoEscalated : CaseNotificationKinds.Escalated, [id], c.Id, c.CaseNumber,
            command.ActorExternalId, command.ActorName, EventKey: $"escalation:{c.Id}:{cycle}:{level.LevelNumber}:{id}", Values: values,
            Severity: "Warning")).ToList();
        var notified = 0;

        void ChangeCase()
        {
            c.EscalationLevel = level.LevelNumber;
            if (automatic) c.SlaReminderSent = true;
            if (newOwner != null)
            {
                c.OwnerId = newOwner.Id;
                if (CaseWorkflow.CanEscalateFrom(c.Status)) c.Status = CaseStatus.Escalated;
            }
            c.UpdatedAt = now;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
                if (db.Database.IsRelational())
                {
                    // Claimed from the level (and SLA period) this escalation was decided at: a second escalation decided at the
                    // same time finds the case already moved and changes nothing.
                    var claimed = await db.Cases.Where(x => x.Id == c.Id && x.EscalationLevel == fromLevel && x.EscalationCycle == cycle)
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.EscalationLevel, level.LevelNumber), ct);
                    if (claimed == 0)
                        throw new CaseHandleException($"{c.CaseNumber} was escalated by someone else a moment ago. Reload the case to see where it stands.");
                }
                ChangeCase();   // the same values on a retry
                db.CaseEvents.Add(caseEvent);
                db.CaseEscalations.Add(history);
                await db.SaveChangesAsync(ct);   // the case, its timeline entry, its history and (CaseAuditInterceptor) its audit copy
                // The notifications ride the escalation's own transaction: both or neither (NotificationOutboxMessages).
                if (notifications != null)
                    notified = await notifications.StageAsync(db, raised.Select(n => (n, new DateTimeOffset(now, TimeSpan.Zero))).ToList(), ct);
                if (tx != null) await tx.CommitAsync(ct);
            });
        }
        catch
        {
            // Nothing of a refused or failed escalation may be saved later by whoever shares this context (the SLA monitor's batch).
            foreach (var e in new object[] { caseEvent, history }) db.Entry(e).State = EntityState.Detached;
            // …nor the audit copy the interceptor staged for the timeline entry.
            foreach (var e in db.ChangeTracker.Entries<OmniConnect.Outbox.OutboxMessage>().Where(e => e.State == EntityState.Added && e.Entity.EventId == caseEvent.Id).ToList())
                e.State = EntityState.Detached;
            if (db.Entry(c).State != EntityState.Detached) await db.Entry(c).ReloadAsync(CancellationToken.None);
            throw;
        }

        if (notified > 0) notifications?.Wake();
        return history;
    }

    private static string? Cut(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];
}
