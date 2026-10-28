namespace CaseManagement.Api.Services;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>Someone who can be @mentioned on a case, and why they are relevant to it.</summary>
public sealed class MentionableUserDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Role { get; set; }
    /// <summary>How they relate to the case: Owner, Team Lead, Co-worker or Watcher.</summary>
    public string Relation { get; set; } = string.Empty;
}

public interface IMentionService
{
    /// <summary>
    /// The people relevant to one case — its owner, its co-workers and watchers, and its team's lead — active only, never
    /// <paramref name="excludeUserId"/> (the author). The only people a mention on the case can reach.
    /// </summary>
    Task<List<MentionableUserDto>> GetMentionableAsync(Guid caseId, Guid? excludeUserId = null, CancellationToken ct = default);

    /// <summary>
    /// Notifies the colleagues @mentioned in <paramref name="text"/>. Never throws: a notification problem must not fail
    /// the note that was just saved.
    /// </summary>
    Task NotifyAsync(string text, Guid authorId, string? authorName, Guid caseId, string caseNumber, string notificationTitle, CancellationToken ct = default);
}

/// <summary>One implementation of "@name" mentions, shared by internal notes, timeline interactions and collaboration notes.</summary>
/// <remarks>
/// A mention chosen from the suggestions is stored as <c>@[Display Name](userId)</c>, so it names a real user by id and the
/// screens highlight exactly that name. Older notes (and text typed without picking a suggestion) use a bare <c>@token</c>,
/// which is matched against the case's mentionable people only — never against everyone in the directory.
/// </remarks>
public class MentionService : IMentionService
{
    private const int MaxTokens = 5;            // a note cannot fan out to the whole company
    private static readonly Regex Markup = new(@"@\[([^\[\]\r\n]{1,120})\]\(([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Token = new(@"(?<![\w@])@([a-zA-Z0-9_\.\-]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private readonly AppDbContext _context;
    private readonly INotificationService _notifications;
    private readonly ILogger<MentionService> _logger;

    public MentionService(AppDbContext context, INotificationService notifications, ILogger<MentionService> logger)
    {
        _context = context;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>The text as a person reads it: every <c>@[Name](id)</c> becomes <c>@Name</c>.</summary>
    public static string ToPlainText(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Markup.Replace(text, m => "@" + m.Groups[1].Value);

    public async Task<List<MentionableUserDto>> GetMentionableAsync(Guid caseId, Guid? excludeUserId = null, CancellationToken ct = default)
    {
        var c = await _context.Cases.AsNoTracking()
            .Where(x => x.Id == caseId)
            .Select(x => new { x.OwnerId, LeadId = x.Department.OwnerId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Case not found");

        var participants = await _context.CaseParticipants.AsNoTracking()
            .Where(p => p.CaseId == caseId)
            .Select(p => new { p.UserId, p.Role })
            .ToListAsync(ct);

        // One relation per person, the strongest first: owner, then team lead, then co-worker, then watcher.
        var relation = new Dictionary<Guid, string>();
        void Add(Guid? id, string why) { if (id is { } v && v != Guid.Empty && !relation.ContainsKey(v)) relation[v] = why; }
        Add(c.OwnerId, "Owner");
        Add(c.LeadId, "Team Lead");
        foreach (var p in participants.Where(p => p.Role == ParticipantRole.CoWorker)) Add(p.UserId, "Co-worker");
        foreach (var p in participants.Where(p => p.Role == ParticipantRole.Watcher)) Add(p.UserId, "Watcher");
        if (excludeUserId.HasValue) relation.Remove(excludeUserId.Value);

        var ids = relation.Keys.ToList();
        var users = await _context.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.Name, u.Email, u.Role })
            .ToListAsync(ct);

        var rank = new[] { "Owner", "Team Lead", "Co-worker", "Watcher" };
        return users
            .Select(u => new MentionableUserDto { UserId = u.Id, Name = u.Name, Email = u.Email, Role = u.Role, Relation = relation[u.Id] })
            .OrderBy(u => Array.IndexOf(rank, u.Relation))
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task NotifyAsync(string text, Guid authorId, string? authorName, Guid caseId, string caseNumber, string notificationTitle, CancellationToken ct = default)
    {
        try
        {
            var raw = text ?? string.Empty;
            var chosenIds = Markup.Matches(raw).Select(m => Guid.Parse(m.Groups[2].Value)).ToHashSet();
            var tokens = Token.Matches(Markup.Replace(raw, " ")).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().Take(MaxTokens).ToList();
            if (chosenIds.Count == 0 && tokens.Count == 0) return;

            // Only the case's own people can be reached, whichever way they were mentioned.
            var eligible = await GetMentionableAsync(caseId, authorId, ct);
            var recipients = eligible.Where(u => chosenIds.Contains(u.UserId)).Select(u => u.UserId).ToHashSet();
            foreach (var token in tokens)
            {
                foreach (var u in eligible.Where(u => (u.Name ?? string.Empty).ToLowerInvariant().Contains(token)
                                                   || (u.Email ?? string.Empty).ToLowerInvariant().StartsWith(token)))
                    recipients.Add(u.UserId);
            }

            var plain = ToPlainText(raw).Trim();
            foreach (var userId in recipients)
            {
                await _notifications.CreateNotificationAsync(
                    userId, "USER_MENTIONED", notificationTitle,
                    $"{authorName ?? "A colleague"} mentioned you in Case {caseNumber}: \"{plain}\"",
                    caseId, caseNumber, "High", 0, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Mention notifications failed for case {CaseNumber}", caseNumber);
        }
    }
}
