namespace CaseManagement.Api.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Configuration;
using CaseManagement.Api.HostIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class NotificationService : INotificationService
{
    private sealed record SlaNotificationPolicy(string Priority, int CooldownMinutes, int MaxReminders, bool EnableAggregation, int AggregationThreshold);

    private readonly AppDbContext _context;
    private readonly IPermissionProvider _permissions;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationService> _logger;
    private readonly SlaNotificationPolicy BreachPolicy;

    public NotificationService(AppDbContext context, IPermissionProvider permissions, IOptions<NotificationOptions> options, ILogger<NotificationService>? logger = null)
    {
        _context = context;
        _logger = logger ?? NullLogger<NotificationService>.Instance;
        _permissions = permissions;
        _options = options.Value;
        BreachPolicy = new SlaNotificationPolicy(_options.BreachPriority, _options.BreachReminderCooldownMinutes, _options.BreachMaxReminders,
            _options.BreachGroupingThreshold > 0, Math.Max(1, _options.BreachGroupingThreshold));
    }

    public async Task CreateNotificationAsync(Guid recipientUserId, string type, string title, string message, Guid? caseId = null, string? caseNumber = null, string priority = "High", int reminderCount = 0, CancellationToken ct = default, string? eventKey = null)
    {
        // An event with a key is reported once, ever. Without a key, an identical notification inside the duplicate window (Notifications:DuplicateWindowMinutes) is treated as a duplicate.
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(0, _options.DuplicateWindowMinutes));
        var exists = eventKey != null
            ? await _context.Notifications.AnyAsync(n => n.RecipientUserId == recipientUserId && n.EventKey == eventKey, ct)
            : await _context.Notifications.AnyAsync(n =>
                n.RecipientUserId == recipientUserId &&
                n.Type == type &&
                n.CaseId == caseId &&
                n.CreatedAt >= cutoff, ct);

        if (exists) return;

        var notification = new NotificationItem
        {
            Id = Guid.NewGuid(),
            RecipientUserId = recipientUserId,
            Type = type,
            Title = title,
            Message = message,
            CaseId = caseId,
            CaseNumber = caseNumber,
            IsRead = false,
            Priority = string.IsNullOrWhiteSpace(priority) ? "High" : priority,
            ReminderCount = reminderCount,
            CreatedAt = DateTime.UtcNow,
            EventKey = eventKey
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<PagedResponseDto<NotificationDto>> GetNotificationsAsync(Guid userId, int page = 1, int pageSize = 15, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 15;
        if (pageSize > 100) pageSize = 100;

        var query = _context.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == userId);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                RecipientUserId = n.RecipientUserId,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                CaseId = n.CaseId,
                CaseNumber = n.CaseNumber,
                IsRead = n.IsRead,
                Priority = n.Priority ?? "High",
                ReminderCount = n.ReminderCount,
                CreatedAt = n.CreatedAt,
                ReadAt = n.ReadAt
            })
            .ToListAsync(ct);

        return new PagedResponseDto<NotificationDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken ct = default)
    {
        return await _context.Notifications
            .AsNoTracking()
            .CountAsync(n => n.RecipientUserId == userId && !n.IsRead, ct);
    }

    public async Task MarkAsReadAsync(Guid notificationId, Guid userId, CancellationToken ct = default)
    {
        var item = await _context.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientUserId == userId, ct);
        if (item != null && !item.IsRead)
        {
            item.IsRead = true;
            item.ReadAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task MarkAllAsReadAsync(Guid userId, CancellationToken ct = default)
    {
        var unreadItems = await _context.Notifications
            .Where(n => n.RecipientUserId == userId && !n.IsRead)
            .ToListAsync(ct);

        if (unreadItems.Any())
        {
            var now = DateTime.UtcNow;
            foreach (var item in unreadItems)
            {
                item.IsRead = true;
                item.ReadAt = now;
            }
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteNotificationAsync(Guid notificationId, Guid userId, CancellationToken ct = default)
    {
        var item = await _context.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientUserId == userId, ct);
        if (item != null)
        {
            _context.Notifications.Remove(item);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task PublishSlaBreachNotificationsAsync(IReadOnlyList<SlaBreachCandidate> breached, DateTime now, CancellationToken ct = default)
    {
        var breachRule = BreachPolicy;
        var dueBreaches = breached.ToList();

        var toInsert = new List<NotificationItem>();

        // Process SLA Breaches with Cooldown & Reminder Engine (Grouped Per Owner)
        if (dueBreaches.Any())
        {
            var breachCaseIds = dueBreaches.Select(b => b.Id).ToList();
            var existingNotifications = await _context.Notifications
                .AsNoTracking()
                .Where(n => n.CaseId.HasValue && breachCaseIds.Contains(n.CaseId.Value) && (n.Type == "SLA_BREACHED" || n.Type == "SLA_BREACH_REMINDER"))
                .ToListAsync(ct);

            // Map latest notification per case for the current SLA cycle
            var notifsByCase = dueBreaches.ToDictionary(
                c => c.Id,
                c => existingNotifications
                    .Where(n => n.CaseId == c.Id && n.CreatedAt >= c.SlaStartTime.AddMinutes(-1))
                    .OrderByDescending(n => n.CreatedAt)
                    .FirstOrDefault()
            );

            // Group breaches by OwnerId so each owner gets appropriate aggregated or individual notifications
            var breachesByOwner = dueBreaches.GroupBy(b => b.OwnerId);

            foreach (var ownerGroup in breachesByOwner)
            {
                var ownerId = ownerGroup.Key;
                var ownerBreaches = ownerGroup.ToList();
                var ownerNewlyBreached = ownerBreaches.Where(b => notifsByCase[b.Id] == null).ToList();

                if (breachRule.EnableAggregation && ownerNewlyBreached.Count >= breachRule.AggregationThreshold)
                {
                    // 1 Grouped notification for this owner's newly breached cases
                    var actualCount = ownerNewlyBreached.Count;
                    toInsert.Add(new NotificationItem
                    {
                        Id = Guid.NewGuid(),
                        RecipientUserId = ownerId,
                        Type = "SLA_BREACHED",
                        Title = $"{actualCount} Cases Breached SLA",
                        Message = $"{actualCount} active cases assigned to you have breached SLA. Click to inspect affected cases.",
                        CaseId = null,
                        CaseNumber = $"{actualCount} Cases",
                        Priority = breachRule.Priority,
                        IsRead = false,
                        CreatedAt = now
                    });

                    // Evaluate cooldown & reminder limits for existing breaches belonging to this owner
                    foreach (var c in ownerBreaches.Where(b => notifsByCase[b.Id] != null))
                    {
                        var latestNotif = notifsByCase[c.Id]!;
                        var minutesElapsed = (now - latestNotif.CreatedAt).TotalMinutes;
                        if (minutesElapsed >= breachRule.CooldownMinutes && latestNotif.ReminderCount < breachRule.MaxReminders)
                        {
                            var nextReminderCount = latestNotif.ReminderCount + 1;
                            toInsert.Add(new NotificationItem
                            {
                                Id = Guid.NewGuid(),
                                RecipientUserId = ownerId,
                                Type = "SLA_BREACH_REMINDER",
                                Title = "SLA Breach Reminder",
                                Message = $"Reminder ({nextReminderCount}/{breachRule.MaxReminders}): Case {c.CaseNumber} ({c.Title}) remains unresolved and is still outside its SLA.",
                                CaseId = c.Id,
                                CaseNumber = c.CaseNumber,
                                Priority = breachRule.Priority,
                                ReminderCount = nextReminderCount,
                                LastReminderAt = now,
                                IsRead = false,
                                CreatedAt = now
                            });
                        }
                    }
                }
                else
                {
                    // Individual breach or reminder notifications for each of this owner's cases
                    foreach (var c in ownerBreaches)
                    {
                        var latestNotif = notifsByCase[c.Id];
                        if (latestNotif == null)
                        {
                            // 1. Initial SLA Breach Notification
                            toInsert.Add(new NotificationItem
                            {
                                Id = Guid.NewGuid(),
                                RecipientUserId = ownerId,
                                Type = "SLA_BREACHED",
                                Title = "SLA Breached",
                                Message = $"Case {c.CaseNumber} ({c.Title}) has breached its SLA.",
                                CaseId = c.Id,
                                CaseNumber = c.CaseNumber,
                                Priority = breachRule.Priority,
                                ReminderCount = 0,
                                IsRead = false,
                                CreatedAt = now
                            });
                        }
                        else
                        {
                            // 2. Cooldown & Max Reminders Evaluation
                            var minutesElapsed = (now - latestNotif.CreatedAt).TotalMinutes;
                            if (minutesElapsed >= breachRule.CooldownMinutes && latestNotif.ReminderCount < breachRule.MaxReminders)
                            {
                                var nextReminderCount = latestNotif.ReminderCount + 1;
                                toInsert.Add(new NotificationItem
                                {
                                    Id = Guid.NewGuid(),
                                    RecipientUserId = ownerId,
                                    Type = "SLA_BREACH_REMINDER",
                                    Title = "SLA Breach Reminder",
                                    Message = $"Reminder ({nextReminderCount}/{breachRule.MaxReminders}): Case {c.CaseNumber} ({c.Title}) remains unresolved and is still outside its SLA.",
                                    CaseId = c.Id,
                                    CaseNumber = c.CaseNumber,
                                    Priority = breachRule.Priority,
                                    ReminderCount = nextReminderCount,
                                    LastReminderAt = now,
                                    IsRead = false,
                                    CreatedAt = now
                                });
                            }
                        }
                    }
                }
            }
        }

        if (toInsert.Count > 0)
        {
            _context.Notifications.AddRange(toInsert);
            await _context.SaveChangesAsync(ct);
        }
    }

    public async Task CreateConfigChangedNotificationAsync(string title, string message, CancellationToken ct = default)
    {
        try
        {
            const string priority = "Info";

            // Only the people who can change configuration hear about configuration changes (matched on the exact roles the
            // Host assigns that permission to), not every agent.
            var roles = _permissions.RolesGranting(Permissions.ConfigManage);
            var managers = _context.Users.AsNoTracking().Where(u => u.IsActive);
            if (roles != null)
            {
                var roleList = roles.Select(r => r.ToLower()).ToList();
                managers = managers.Where(u => roleList.Contains(u.Role.ToLower()));
            }
            var adminUserIds = await managers.Select(u => u.Id).ToListAsync(ct);

            if (!adminUserIds.Any()) return;

            var now = DateTime.UtcNow;
            var notifs = adminUserIds.Select(userId => new NotificationItem
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                Type = "CONFIG_CHANGED",
                Title = title,
                Message = message,
                Priority = priority,
                IsRead = false,
                CreatedAt = now
            });

            _context.Notifications.AddRange(notifs);
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CreateConfigChangedNotification Error");
        }
    }

    public async Task<int> PurgeExpiredAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var readCutoff = nowUtc.AddDays(-Math.Max(1, _options.ReadRetentionDays));
        var maxCutoff = nowUtc.AddDays(-Math.Max(1, _options.MaxAgeDays));
        return await _context.Notifications
            .Where(n => (n.IsRead && n.CreatedAt < readCutoff) || n.CreatedAt < maxCutoff)
            .ExecuteDeleteAsync(ct);
    }
}
