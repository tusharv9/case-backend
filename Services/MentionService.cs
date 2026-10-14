namespace CaseManagement.Api.Services;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public interface IMentionService
{
    /// <summary>
    /// Notifies the colleagues @mentioned in <paramref name="text"/>. Never throws: a notification problem must not fail
    /// the note that was just saved.
    /// </summary>
    Task NotifyAsync(string text, Guid authorId, string? authorName, Guid caseId, string caseNumber, string notificationTitle, CancellationToken ct = default);
}

/// <summary>One implementation of "@name" mentions, shared by internal notes, timeline interactions and collaboration notes.</summary>
public class MentionService : IMentionService
{
    private const int MaxTokens = 5;            // a note cannot fan out to the whole company
    private const int MaxMatchesPerToken = 10;
    private static readonly Regex Token = new(@"@([a-zA-Z0-9_\.\-]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private readonly AppDbContext _context;
    private readonly INotificationService _notifications;
    private readonly ILogger<MentionService> _logger;

    public MentionService(AppDbContext context, INotificationService notifications, ILogger<MentionService> logger)
    {
        _context = context;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task NotifyAsync(string text, Guid authorId, string? authorName, Guid caseId, string caseNumber, string notificationTitle, CancellationToken ct = default)
    {
        try
        {
            var tokens = Token.Matches(text ?? string.Empty).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().Take(MaxTokens).ToList();
            if (tokens.Count == 0) return;

            var recipients = new HashSet<Guid>();
            foreach (var token in tokens)
            {
                // Only active people, never the author, and a bounded number per token (queried, not loaded wholesale).
                var ids = await _context.Users.AsNoTracking()
                    .Where(u => u.IsActive && u.Id != authorId && (u.Name.ToLower().Contains(token) || u.Email.ToLower().StartsWith(token)))
                    .OrderBy(u => u.Name).Select(u => u.Id).Take(MaxMatchesPerToken).ToListAsync(ct);
                foreach (var id in ids) recipients.Add(id);
            }

            foreach (var userId in recipients)
            {
                await _notifications.CreateNotificationAsync(
                    userId, "USER_MENTIONED", notificationTitle,
                    $"{authorName ?? "A colleague"} mentioned you in Case {caseNumber}: \"{text!.Trim()}\"",
                    caseId, caseNumber, "High", 0, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Mention notifications failed for case {CaseNumber}", caseNumber);
        }
    }
}
