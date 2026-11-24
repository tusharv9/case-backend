namespace CaseManagement.Api.Services.Statuses;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>The configured statuses and allowed moves, read once and kept briefly (a case page and the board read it often).</summary>
public sealed class CaseStatusSnapshot
{
    public IReadOnlyList<CaseStatusDefinition> Definitions { get; }
    private readonly Dictionary<string, CaseStatusDefinition> _byKey;
    private readonly HashSet<(string, string)> _moves;

    public CaseStatusSnapshot(IEnumerable<CaseStatusDefinition> definitions, IEnumerable<CaseStatusTransition> transitions)
    {
        Definitions = definitions.OrderBy(d => d.Level).ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToList();
        _byKey = Definitions.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        _moves = transitions.Select(t => (t.FromKey.ToLowerInvariant(), t.ToKey.ToLowerInvariant())).ToHashSet();
    }

    public CaseStatusDefinition? Find(string? key) => key != null && _byKey.TryGetValue(key.Trim(), out var d) ? d : null;

    /// <summary>A status's display name; for a key no longer configured, the system wording (never blank).</summary>
    public string LabelOf(string key) =>
        Find(key)?.Label ?? (Enum.TryParse<CaseStatus>(key, out var system) ? CaseWorkflow.Label(system) : key);

    public string LabelOf(Case c) => LabelOf(c.EffectiveStatusKey);

    public CaseStatusDefinition? DefinitionOf(Case c) => Find(c.EffectiveStatusKey);

    /// <summary>An allowed manual move (the Status menu). Open, Escalated and Resolved are reached only by their own actions.</summary>
    public bool CanMove(string fromKey, string toKey) => _moves.Contains((fromKey.ToLowerInvariant(), toKey.ToLowerInvariant()));

    /// <summary>The active statuses a case in <paramref name="fromKey"/> may be moved to by hand, in display order.</summary>
    public IReadOnlyList<CaseStatusDefinition> MovesFrom(string fromKey) =>
        Definitions.Where(d => d.IsActive && CanMove(fromKey, d.Key)).ToList();

    public IReadOnlyCollection<(string From, string To)> Transitions => _moves;
}

public interface ICaseStatusCatalog
{
    Task<CaseStatusSnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// The statuses a case can be in and how they may change — Configurable Settings → Status. Until the table is first filled
/// (a database the migration has not reached, or the in-memory tests) the built-in defaults answer: exactly today's five statuses.
/// </summary>
public sealed class CaseStatusCatalog(AppDbContext db, IConfigCache cache) : ICaseStatusCatalog
{
    public const string CacheKey = "case-status-catalog";

    public Task<CaseStatusSnapshot> GetAsync(CancellationToken ct = default) =>
        cache.GetOrCreateAsync(CacheKey, async () =>
        {
            var definitions = await db.CaseStatusDefinitions.AsNoTracking().ToListAsync(ct);
            if (definitions.Count == 0) return new CaseStatusSnapshot(Defaults(), DefaultTransitions());
            var transitions = await db.CaseStatusTransitions.AsNoTracking().ToListAsync(ct);
            return new CaseStatusSnapshot(definitions, transitions);
        });

    /// <summary>The system statuses, as the workflow had them before they were configurable. Also what the migration seeds.</summary>
    public static IReadOnlyList<CaseStatusDefinition> Defaults() =>
    [
        System(nameof(CaseStatus.Open), "Open", CaseStatusCategories.Open, 1, SlaBehaviours.Running, "blue"),
        System(nameof(CaseStatus.InProgress), "In Progress", CaseStatusCategories.Active, 2, SlaBehaviours.Running, "indigo"),
        System(nameof(CaseStatus.WaitingOnCustomer), "Waiting on Customer", CaseStatusCategories.Waiting, 3, SlaBehaviours.Paused, "amber"),
        System(nameof(CaseStatus.Escalated), "Escalated", CaseStatusCategories.Escalated, 4, SlaBehaviours.Running, "red"),
        System(nameof(CaseStatus.Resolved), "Resolved", CaseStatusCategories.Closed, 5, SlaBehaviours.Stopped, "green"),
        // Legacy end states some old cases carry: kept so they still have a name, never offered.
        System(nameof(CaseStatus.Closed), "Closed", CaseStatusCategories.Closed, 90, SlaBehaviours.Stopped, "slate", active: false),
        System(nameof(CaseStatus.Cancelled), "Cancelled", CaseStatusCategories.Closed, 91, SlaBehaviours.Stopped, "slate", active: false),
    ];

    public static IReadOnlyList<CaseStatusTransition> DefaultTransitions() =>
    [
        new() { FromKey = nameof(CaseStatus.InProgress), ToKey = nameof(CaseStatus.WaitingOnCustomer) },
        new() { FromKey = nameof(CaseStatus.WaitingOnCustomer), ToKey = nameof(CaseStatus.InProgress) },
        new() { FromKey = nameof(CaseStatus.Escalated), ToKey = nameof(CaseStatus.InProgress) },
        new() { FromKey = nameof(CaseStatus.Escalated), ToKey = nameof(CaseStatus.WaitingOnCustomer) },
    ];

    /// <summary>The five statuses the workflow is built on: never deleted, deactivated or given another category.</summary>
    public static readonly HashSet<string> CoreKeys = new(StringComparer.OrdinalIgnoreCase)
        { nameof(CaseStatus.Open), nameof(CaseStatus.InProgress), nameof(CaseStatus.WaitingOnCustomer), nameof(CaseStatus.Escalated), nameof(CaseStatus.Resolved) };

    private static CaseStatusDefinition System(string key, string label, string category, int level, string sla, string color, bool active = true) =>
        new() { Key = key, Label = label, Category = category, Level = level, SlaBehaviour = sla, Color = color, IsSystem = true, IsActive = active };

    /// <summary>A stable key for a new status from its name ("Pending documents" → "PendingDocuments"), unique among <paramref name="taken"/>.</summary>
    public static string KeyFor(string label, IReadOnlyCollection<string> taken)
    {
        var words = Regex.Matches(label, "[A-Za-z0-9]+").Select(m => char.ToUpperInvariant(m.Value[0]) + m.Value[1..]);
        var stem = string.Concat(words);
        if (stem.Length == 0 || char.IsDigit(stem[0])) stem = "Status" + stem;
        if (stem.Length > 44) stem = stem[..44];
        var reserved = new HashSet<string>(taken.Concat(Enum.GetNames<CaseStatus>()), StringComparer.OrdinalIgnoreCase);
        var key = stem;
        for (var n = 2; reserved.Contains(key); n++) key = $"{stem}{n}";
        return key;
    }
}
