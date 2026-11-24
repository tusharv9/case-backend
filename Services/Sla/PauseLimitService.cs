namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The maximum pause a status may impose (Configurable Settings → Status → Pause policy), applied by the SLA monitor each cycle.
/// </summary>
/// <remarks>
/// <para>The limit counts working time (business hours and holidays) from when the case entered its status, so moving between
/// two paused statuses starts the new status's own limit while the clock stays paused. When it is reached the status's chosen
/// action happens once: <b>ResumeSla</b> — the clock runs again though the case keeps its status; <b>MoveToStatus</b> — the case
/// moves to the chosen status (the clock runs or stays paused as that status says); <b>NotifyOnly</b> — its holder is told and
/// the clock stays paused. Each is written on the case's timeline as an automatic entry and the holder is notified.</para>
/// <para>It runs inside the monitor's single-instance lock, so two servers never act on the same pause.</para>
/// </remarks>
public sealed class PauseLimitService(AppDbContext db, INotificationService notifications, ILogger<PauseLimitService> logger)
{
    public const string Marker = "[Pause limit]";

    public async Task<int> ApplyAsync(SlaClock clock, DateTime now, CancellationToken ct = default)
    {
        var definitions = await db.CaseStatusDefinitions.AsNoTracking().ToListAsync(ct);
        var limited = definitions.Where(d => d.SlaBehaviour == SlaBehaviours.Paused && d.MaxPauseMinutes > 0)
            .ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        if (limited.Count == 0) return 0;

        var pauses = await db.CaseSlaPauses.Include(p => p.Case)
            .Where(p => p.EndedAt == null && p.LimitReachedAt == null && p.Case != null && p.Case.SlaPausedAt != null)
            .Take(500).ToListAsync(ct);

        var acted = 0;
        foreach (var pause in pauses)
        {
            var c = pause.Case!;
            if (!limited.TryGetValue(c.EffectiveStatusKey, out var status)) continue;
            var since = c.StatusEnteredAt ?? pause.StartedAt;
            if (clock.Calendar.ElapsedBusinessMinutes(since, now) < status.MaxPauseMinutes!.Value) continue;

            try
            {
                await ActAsync(c, pause, status, definitions, clock, now, ct);
                acted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The pause limit of {Status} could not be applied to {Case}.", status.Label, c.CaseNumber);
            }
        }
        return acted;
    }

    private async Task ActAsync(Case c, CaseSlaPause pause, CaseStatusDefinition status, List<CaseStatusDefinition> definitions,
        SlaClock clock, DateTime now, CancellationToken ct)
    {
        var limit = Describe(status.MaxPauseMinutes!.Value);
        string message, notice;
        switch (status.OnPauseExpiry)
        {
            case PauseExpiryActions.MoveToStatus when definitions.FirstOrDefault(d => d.Key == status.PauseExpiryStatusKey && d.IsActive) is { } target:
            {
                var system = target.IsSystem && Enum.TryParse<CaseStatus>(target.Key, out var s) ? s : CaseStatusCategories.SystemStatusOf(target.Category);
                if (target.SlaBehaviour != SlaBehaviours.Paused)
                {
                    pause.EndReason = "PauseLimit";
                    clock.Resume(c, now);
                }
                c.Status = system;
                if (!target.IsSystem) c.StatusKey = target.Key;
                message = $"{Marker} Status changed from {status.Label} to {target.Label}: the case reached the {limit} pause limit of {status.Label}.";
                notice = $"Case {c.CaseNumber} moved from {status.Label} to {target.Label} after {limit} paused.";
                break;
            }
            case PauseExpiryActions.NotifyOnly:
                pause.LimitReachedAt = now;
                message = $"{Marker} The case has been {status.Label} for {limit}, the pause limit. The SLA is still paused.";
                notice = $"Case {c.CaseNumber} has been {status.Label} for {limit} (the pause limit).";
                break;
            default:   // ResumeSla, or a move whose target was removed: never leave the clock stopped past its limit
                pause.LimitReachedAt = now;
                pause.EndReason = "PauseLimit";
                clock.Resume(c, now);
                message = $"{Marker} SLA resumed: the case reached the {limit} pause limit of {status.Label}. It stays {status.Label}.";
                notice = $"Case {c.CaseNumber}: the SLA is running again after {limit} {status.Label}.";
                break;
        }

        db.CaseEvents.Add(new CaseEvent
        {
            CaseId = c.Id, EventType = EventType.Update, ActionType = "SLA_PAUSE_LIMIT", Message = message, CreatedAt = now,
            OldValue = status.Label, IsInternal = true,
        });
        await db.SaveChangesAsync(ct);
        try
        {
            await notifications.CreateNotificationAsync(c.OwnerId, "CASE_STATUS_CHANGED", "SLA pause limit reached", notice, c.Id, c.CaseNumber,
                "High", ct: ct, eventKey: $"pause-limit:{pause.Id}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not notify the holder of {Case} about its pause limit.", c.CaseNumber);
        }
    }

    public static string Describe(int minutes)
    {
        foreach (var (unit, size) in new[] { ("day", 60 * 24), ("hour", 60) })
            if (minutes % size == 0) return $"{minutes / size} {unit}{(minutes / size == 1 ? "" : "s")}";
        return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
    }
}
