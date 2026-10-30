namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Records that a person opened a case's details — the access the audit trail and Team Monitoring report as "Case viewed".
/// Written by the server when it serves the details (after the case-access check), never on the browser's word. One row per
/// person and case per <see cref="Window"/>, so reopening a drawer or refreshing does not flood the trail. The row holds who,
/// which case and when — no case content.
/// </summary>
public static class CaseViewLog
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static async Task RecordAsync(AppDbContext db, Guid caseId, Guid userId, ILogger logger, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return;
        try
        {
            var since = DateTime.UtcNow - Window;
            var recent = await db.CaseEvents.AsNoTracking()
                .AnyAsync(e => e.CaseId == caseId && e.UserId == userId && e.EventType == EventType.View && e.CreatedAt >= since, ct);
            if (recent) return;

            db.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(), CaseId = caseId, UserId = userId, EventType = EventType.View,
                Message = "Opened the case details.", CreatedAt = DateTime.UtcNow, IsInternal = true,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // Showing the case matters more than recording that it was shown; the failure is logged for follow-up.
            logger.LogWarning(ex, "Could not record a view of case {CaseId}.", caseId);
        }
    }
}
