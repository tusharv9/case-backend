namespace CaseManagement.Api.Services.Statuses;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public sealed class CaseStatusDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Level { get; set; }
    public string SlaBehaviour { get; set; } = string.Empty;
    public int? MaxPauseMinutes { get; set; }
    public string? OnPauseExpiry { get; set; }
    public string? PauseExpiryStatusKey { get; set; }
    public string Color { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    /// <summary>One of the five workflow statuses: never deleted, deactivated or recategorised.</summary>
    public bool IsCore { get; set; }
    public bool IsActive { get; set; }
    /// <summary>What may be changed on this status (the screen locks the rest; the server refuses it anyway).</summary>
    public List<string> Editable { get; set; } = new();
    /// <summary>Cases currently in this status (open or closed).</summary>
    public int CaseCount { get; set; }
}

public sealed class CaseStatusTransitionDto
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public sealed class CaseStatusCatalogDto
{
    public List<CaseStatusDto> Statuses { get; set; } = new();
    public List<CaseStatusTransitionDto> Transitions { get; set; } = new();
    public IReadOnlyList<string> Colors { get; set; } = CaseStatusAdminService.Colors;
}

public sealed class SaveCaseStatusDto
{
    public string? Label { get; set; }
    public string? Category { get; set; }
    public int? Level { get; set; }
    public string? SlaBehaviour { get; set; }
    public int? MaxPauseMinutes { get; set; }
    public string? OnPauseExpiry { get; set; }
    public string? PauseExpiryStatusKey { get; set; }
    public string? Color { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class SaveCaseStatusTransitionsDto
{
    public List<CaseStatusTransitionDto> Transitions { get; set; } = new();
}

public interface ICaseStatusAdminService
{
    Task<CaseStatusCatalogDto> GetAsync(bool withCounts, CancellationToken ct = default);
    Task<CaseStatusDto> CreateAsync(SaveCaseStatusDto dto, CancellationToken ct = default);
    Task<CaseStatusDto> UpdateAsync(string key, SaveCaseStatusDto dto, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<CaseStatusCatalogDto> SaveTransitionsAsync(SaveCaseStatusTransitionsDto dto, CancellationToken ct = default);
}

/// <summary>
/// Configurable Settings → Status. Administrators add their own statuses (Active or Waiting), order them, choose whether the
/// SLA runs or pauses in them, and decide which manual moves the Status menu offers.
/// </summary>
/// <remarks>
/// <para><b>What stays fixed.</b> Open, In Progress, Waiting on Customer, Escalated and Resolved carry the workflow (Handle,
/// Escalate, Resolve), so they can be renamed, recoloured and reordered but never deleted, deactivated or recategorised; only
/// Waiting on Customer's SLA behaviour and pause policy may change. Moves into Open, Escalated and Resolved are actions with their
/// own rules, never rows of the move table.</para>
/// <para><b>Nothing a case refers to disappears.</b> A status a case is in cannot be deleted — deactivating it removes it from
/// the menus while those cases keep it. Changes reach every server within the configuration cache's lifetime.</para>
/// </remarks>
public sealed class CaseStatusAdminService(AppDbContext db, IConfigCache cache, INotificationService notifications,
    ICurrentUserAccessor? currentUser = null) : ICaseStatusAdminService
{
    public static readonly IReadOnlyList<string> Colors = ["blue", "sky", "indigo", "violet", "amber", "red", "green", "slate"];
    private const int MaxPauseLimit = 60 * 24 * 365;   // business minutes; generous ceiling

    public async Task<CaseStatusCatalogDto> GetAsync(bool withCounts, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var definitions = await db.CaseStatusDefinitions.AsNoTracking().OrderBy(d => d.Level).ThenBy(d => d.Label).ToListAsync(ct);
        var transitions = await db.CaseStatusTransitions.AsNoTracking().OrderBy(t => t.FromKey).ThenBy(t => t.ToKey).ToListAsync(ct);
        var counts = withCounts ? await CountsAsync(ct) : new Dictionary<string, int>();
        return new CaseStatusCatalogDto
        {
            Statuses = definitions.Select(d => ToDto(d, counts.GetValueOrDefault(d.Key))).ToList(),
            Transitions = transitions.Select(t => new CaseStatusTransitionDto { From = t.FromKey, To = t.ToKey }).ToList(),
        };
    }

    public async Task<CaseStatusDto> CreateAsync(SaveCaseStatusDto dto, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var all = await db.CaseStatusDefinitions.ToListAsync(ct);
        var definition = new CaseStatusDefinition
        {
            Key = CaseStatusCatalog.KeyFor(dto.Label ?? "", all.Select(d => d.Key).ToList()),
            Category = dto.Category ?? CaseStatusCategories.Active,
            Level = dto.Level ?? (all.Where(d => d.IsActive).Select(d => d.Level).DefaultIfEmpty(0).Max() + 1),
            IsSystem = false,
            CreatedAt = DateTime.UtcNow,
        };
        Apply(definition, dto, all, creating: true);
        db.CaseStatusDefinitions.Add(definition);
        await SaveAsync($"Case status '{definition.Label}' was added ({definition.Category}, SLA {definition.SlaBehaviour}).", "CREATE", definition.Key, ct);
        return ToDto(definition, 0);
    }

    public async Task<CaseStatusDto> UpdateAsync(string key, SaveCaseStatusDto dto, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var all = await db.CaseStatusDefinitions.ToListAsync(ct);
        var definition = all.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))
                         ?? throw new KeyNotFoundException("Status not found.");
        var before = Describe(definition);
        Apply(definition, dto, all, creating: false);
        definition.UpdatedAt = DateTime.UtcNow;
        definition.UpdatedById = currentUser?.LocalUserId;
        await SaveAsync($"Case status '{definition.Label}' was updated.", "UPDATE", definition.Key, ct, before, Describe(definition));
        return ToDto(definition, (await CountsAsync(ct)).GetValueOrDefault(definition.Key));
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var definition = await db.CaseStatusDefinitions.FirstOrDefaultAsync(d => d.Key == key, ct) ?? throw new KeyNotFoundException("Status not found.");
        if (definition.IsSystem) throw new InvalidOperationException($"'{definition.Label}' is a system status and cannot be deleted.");
        var inUse = await db.Cases.CountAsync(c => c.StatusKey == definition.Key, ct);
        if (inUse > 0)
            throw new InvalidOperationException($"{inUse} case{(inUse == 1 ? " is" : "s are")} in '{definition.Label}'. Deactivate it instead: those cases keep it, and it is no longer offered.");
        var referencedBy = await db.CaseStatusDefinitions.Where(d => d.PauseExpiryStatusKey == definition.Key).Select(d => d.Label).ToListAsync(ct);
        if (referencedBy.Count > 0)
            throw new InvalidOperationException($"'{definition.Label}' is where {string.Join(", ", referencedBy)} move{(referencedBy.Count == 1 ? "s" : "")} when a pause runs out. Change that first.");
        db.CaseStatusDefinitions.Remove(definition);   // its moves go with it
        await SaveAsync($"Case status '{definition.Label}' was deleted.", "DELETE", definition.Key, ct);
    }

    public async Task<CaseStatusCatalogDto> SaveTransitionsAsync(SaveCaseStatusTransitionsDto dto, CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct);
        var all = (await db.CaseStatusDefinitions.ToListAsync(ct)).ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);
        var errors = new List<FieldError>();
        var rows = new List<CaseStatusTransition>();
        foreach (var t in dto.Transitions)
        {
            if (!all.TryGetValue(t.From ?? "", out var from) || !all.TryGetValue(t.To ?? "", out var to))
            {
                errors.Add(new FieldError("transitions", $"Unknown status in the move {t.From} → {t.To}.")); continue;
            }
            if (from.Key == to.Key) { errors.Add(new FieldError("transitions", $"{from.Label} cannot move to itself.")); continue; }
            if (from.Category is not (CaseStatusCategories.Active or CaseStatusCategories.Waiting or CaseStatusCategories.Escalated))
                errors.Add(new FieldError("transitions", $"A case leaves {from.Label} through its own action (Handle, Reopen), not the Status menu."));
            else if (to.Category is not (CaseStatusCategories.Active or CaseStatusCategories.Waiting))
                errors.Add(new FieldError("transitions", $"{to.Label} is reached through its own action (Handle, Escalate, Resolve), not the Status menu."));
            else if (!rows.Any(r => r.FromKey == from.Key && r.ToKey == to.Key))
                rows.Add(new CaseStatusTransition { FromKey = from.Key, ToKey = to.Key });
        }
        if (errors.Count > 0) throw new FieldValidationException(errors);

        var before = await db.CaseStatusTransitions.ToListAsync(ct);
        db.CaseStatusTransitions.RemoveRange(before);
        db.CaseStatusTransitions.AddRange(rows);
        string Text(IEnumerable<CaseStatusTransition> ts) => string.Join(", ", ts.Select(x => $"{x.FromKey}→{x.ToKey}"));
        await SaveAsync($"Allowed status moves were updated ({rows.Count}).", "UPDATE", "Transitions", ct, Text(before), Text(rows));
        return await GetAsync(withCounts: true, ct);
    }

    // ------------------------------------------------------------------------------------------------ rules

    private static void Apply(CaseStatusDefinition d, SaveCaseStatusDto dto, IReadOnlyList<CaseStatusDefinition> all, bool creating)
    {
        var errors = new List<FieldError>();
        var editable = EditableFields(d, creating);
        void Refuse(string field, string message) => errors.Add(new FieldError(field, message));
        // A field sent with a new value: applied when this status allows it, refused (with a message) when it is locked.
        bool Changes(string field, object? value, object current) =>
            value != null && !Equals(value, current) && (editable.Contains(field) || Locked(field));
        bool Locked(string field) { Refuse(field, $"{(d.Label.Length > 0 ? d.Label : "This status")}: {field} cannot be changed on a system status."); return false; }

        if (dto.Label != null || creating)
        {
            var label = (dto.Label ?? "").Trim();
            if (label.Length == 0) Refuse("label", "Give the status a name.");
            else if (label.Length > 60) Refuse("label", "Keep the name to 60 characters.");
            else if (all.Any(o => o.Key != d.Key && string.Equals(o.Label.Trim(), label, StringComparison.OrdinalIgnoreCase)))
                Refuse("label", $"A status named '{label}' already exists.");
            else d.Label = label;
        }

        if (Changes("category", dto.Category, d.Category))
        {
            if (!CaseStatusCategories.Custom.Contains(dto.Category!)) Refuse("category", "A status you add is either Active (being worked) or Waiting (on someone else).");
            else d.Category = dto.Category!;
        }
        if (Changes("level", dto.Level, d.Level))
        {
            if (dto.Level is < 1 or > 999) Refuse("level", "The level is a number from 1 to 999.");
            else d.Level = dto.Level!.Value;
        }
        if (Changes("color", dto.Color, d.Color))
        {
            if (!Colors.Contains(dto.Color!)) Refuse("color", "Choose one of the offered colours.");
            else d.Color = dto.Color!;
        }
        if (Changes("isActive", dto.IsActive, d.IsActive))
        {
            if (!dto.IsActive!.Value && all.Any(o => o.IsActive && o.Key != d.Key && string.Equals(o.PauseExpiryStatusKey, d.Key, StringComparison.OrdinalIgnoreCase)))
                Refuse("isActive", "Another status moves cases here when its pause runs out. Change that first.");
            else d.IsActive = dto.IsActive!.Value;
        }
        if (Changes("slaBehaviour", dto.SlaBehaviour, d.SlaBehaviour))
        {
            if (dto.SlaBehaviour is not (SlaBehaviours.Running or SlaBehaviours.Paused)) Refuse("slaBehaviour", "The SLA either runs or pauses in this status.");
            else d.SlaBehaviour = dto.SlaBehaviour!;
        }

        // Pause policy: only for a paused status. Sending the fields (even empty) replaces the policy.
        if (editable.Contains("pausePolicy") && (dto.MaxPauseMinutes != null || dto.OnPauseExpiry != null || dto.PauseExpiryStatusKey != null || dto.SlaBehaviour != null))
        {
            if (d.SlaBehaviour != SlaBehaviours.Paused || dto.MaxPauseMinutes is null or 0)
            {
                d.MaxPauseMinutes = null; d.OnPauseExpiry = null; d.PauseExpiryStatusKey = null;
            }
            else if (dto.MaxPauseMinutes is < 1 or > MaxPauseLimit) Refuse("maxPauseMinutes", "The longest pause is between 1 minute and a year of working time.");
            else if (!PauseExpiryActions.All.Contains(dto.OnPauseExpiry ?? "")) Refuse("onPauseExpiry", "Choose what happens when the pause runs out.");
            else
            {
                string? target = null;
                if (dto.OnPauseExpiry == PauseExpiryActions.MoveToStatus)
                {
                    var t = all.FirstOrDefault(o => string.Equals(o.Key, dto.PauseExpiryStatusKey, StringComparison.OrdinalIgnoreCase));
                    if (t == null || !t.IsActive || t.Key == d.Key || t.Category is not (CaseStatusCategories.Active or CaseStatusCategories.Waiting))
                        Refuse("pauseExpiryStatusKey", "Choose an active status (Active or Waiting) to move the case to.");
                    else target = t.Key;
                }
                d.MaxPauseMinutes = dto.MaxPauseMinutes; d.OnPauseExpiry = dto.OnPauseExpiry; d.PauseExpiryStatusKey = target;
            }
        }
        else if (d.SlaBehaviour != SlaBehaviours.Paused && d.MaxPauseMinutes != null)
        {
            d.MaxPauseMinutes = null; d.OnPauseExpiry = null; d.PauseExpiryStatusKey = null;
        }

        if (errors.Count > 0) throw new FieldValidationException(errors);
    }

    /// <summary>The fields an administrator may change on a status.</summary>
    public static HashSet<string> EditableFields(CaseStatusDefinition d, bool creating = false)
    {
        if (creating || !d.IsSystem)
            return ["label", "category", "level", "color", "isActive", "slaBehaviour", "pausePolicy"];
        if (!CaseStatusCatalog.CoreKeys.Contains(d.Key)) return ["label"];   // legacy Closed/Cancelled
        return d.Key == nameof(CaseStatus.WaitingOnCustomer)
            ? ["label", "level", "color", "slaBehaviour", "pausePolicy"]
            : ["label", "level", "color"];
    }

    private CaseStatusDto ToDto(CaseStatusDefinition d, int count) => new()
    {
        Key = d.Key, Label = d.Label, Category = d.Category, Level = d.Level, SlaBehaviour = d.SlaBehaviour,
        MaxPauseMinutes = d.MaxPauseMinutes, OnPauseExpiry = d.OnPauseExpiry, PauseExpiryStatusKey = d.PauseExpiryStatusKey,
        Color = d.Color, IsSystem = d.IsSystem, IsCore = CaseStatusCatalog.CoreKeys.Contains(d.Key), IsActive = d.IsActive,
        Editable = EditableFields(d).OrderBy(x => x).ToList(), CaseCount = count,
    };

    private async Task<Dictionary<string, int>> CountsAsync(CancellationToken ct)
    {
        var rows = await db.Cases.AsNoTracking().GroupBy(c => new { c.Status, c.StatusKey })
            .Select(g => new { g.Key.Status, g.Key.StatusKey, Count = g.Count() }).ToListAsync(ct);
        return rows.GroupBy(r => r.StatusKey ?? r.Status.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Count), StringComparer.OrdinalIgnoreCase);
    }

    private static string Describe(CaseStatusDefinition d) =>
        $"{d.Label} · {d.Category} · level {d.Level} · SLA {d.SlaBehaviour}" +
        (d.MaxPauseMinutes is { } m ? $" (max {m} min, then {d.OnPauseExpiry}{(d.PauseExpiryStatusKey != null ? " " + d.PauseExpiryStatusKey : "")})" : "") +
        $" · {d.Color} · {(d.IsActive ? "active" : "inactive")}";

    /// <summary>Writes the built-in statuses the first time anything is saved (the migration normally already has).</summary>
    private async Task EnsureSeededAsync(CancellationToken ct)
    {
        if (await db.CaseStatusDefinitions.AnyAsync(ct)) return;
        db.CaseStatusDefinitions.AddRange(CaseStatusCatalog.Defaults().Select(d => new CaseStatusDefinition
        {
            Key = d.Key, Label = d.Label, Category = d.Category, Level = d.Level, SlaBehaviour = d.SlaBehaviour, Color = d.Color,
            IsSystem = true, IsActive = d.IsActive, CreatedAt = DateTime.UtcNow,
        }));
        db.CaseStatusTransitions.AddRange(CaseStatusCatalog.DefaultTransitions().Select(t => new CaseStatusTransition { FromKey = t.FromKey, ToKey = t.ToKey }));
        await db.SaveChangesAsync(ct);
    }

    private async Task SaveAsync(string description, string action, string entity, CancellationToken ct, string? before = null, string? after = null)
    {
        if (currentUser?.LocalUserId is { } userId)
            db.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(), EventType = EventType.Other, Message = description, CreatedAt = DateTime.UtcNow, UserId = userId,
                Module = "Configurable Settings", EntityName = "CaseStatus:" + entity, ActionType = action, OldValue = before, NewValue = after,
            });
        await db.SaveChangesAsync(ct);
        cache.InvalidateAll();
        await notifications.CreateConfigChangedNotificationAsync("System Setting Updated", description, ct);
    }
}
