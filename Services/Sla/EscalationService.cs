namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The kinds of escalation trigger the engine can EXECUTE. An administrator picks one and (where needed) a value; nothing
/// else is offered or accepted, so a trigger that is shown in the matrix is always one the worker actually evaluates.
/// </summary>
public static class EscalationTriggers
{
    public const string SlaPercentage = "SlaPercentage";
    public const string SlaBreached = "SlaBreached";
    public const string SlaPostBreachHours = "SlaPostBreachHours";
    public const string FirstResponseBreached = "FirstResponseBreached";
    public const string ManualOnly = "ManualOnly";

    public sealed record Definition(string Type, string Label, bool NeedsValue, string? Unit, decimal Min, decimal Max);

    public static readonly IReadOnlyList<Definition> All = new[]
    {
        new Definition(SlaPercentage, "SLA consumption reaches", true, "%", 1, 1000),
        new Definition(SlaBreached, "SLA is breached", false, null, 0, 0),
        new Definition(SlaPostBreachHours, "SLA has been breached for", true, "hours", 1, 2000),
        new Definition(FirstResponseBreached, "First response is overdue", false, null, 0, 0),
        new Definition(ManualOnly, "Manual escalation only", false, null, 0, 0),
    };

    public static Definition? Find(string? type) =>
        All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when valid, otherwise what is wrong.</summary>
    public static string? Validate(string? type, decimal? value)
    {
        var def = Find(type);
        if (def == null) return $"'{type}' is not an escalation trigger. Choose one of: {string.Join(", ", All.Select(d => d.Type))}.";
        if (!def.NeedsValue) return null;
        if (!value.HasValue) return $"'{def.Label}' needs a value ({def.Unit}).";
        if (value.Value < def.Min || value.Value > def.Max) return $"The value for '{def.Label}' must be between {def.Min} and {def.Max} {def.Unit}.";
        return null;
    }

    /// <summary>The human-readable form, always derived from the structured trigger so the text can never disagree with what runs.</summary>
    public static string Describe(string? type, decimal? value)
    {
        var def = Find(type);
        if (def == null) return string.Empty;
        return def.NeedsValue ? $"{def.Label} {value:0.##}{(def.Unit == "%" ? "%" : " " + def.Unit)}" : def.Label;
    }

    /// <summary>Canonical spelling of a trigger type (or null if unknown).</summary>
    public static string? Canonical(string? type) => Find(type)?.Type;
}

/// <summary>The escalation configuration the engine works from.</summary>
public sealed record EscalationPolicy(IReadOnlyList<EscalationLevelConfig> ActiveLevels, decimal? ReminderPercent)
{
    public int MaxLevel => ActiveLevels.Count == 0 ? 1 : ActiveLevels.Max(l => l.LevelNumber);
}

public sealed record EscalationDecision(EscalationLevelConfig Level, string Reason);

public interface IEscalationService
{
    Task<EscalationPolicy> GetPolicyAsync(CancellationToken ct = default);

    /// <summary>The first level above the case's current one whose trigger is met right now, or null.</summary>
    EscalationDecision? FindDueLevel(Case c, SlaSnapshot snapshot, EscalationPolicy policy, DateTime nowUtc);

    /// <summary>The next active level above the case's current one (used by manual escalation), or null at the top.</summary>
    EscalationLevelConfig? NextLevel(Case c, EscalationPolicy policy);

    /// <summary>Who an escalation to <paramref name="level"/> goes to. Null when no active person matches.</summary>
    Task<User?> ResolveTargetAsync(Case c, EscalationLevelConfig level, CancellationToken ct = default);
}

public class EscalationService : IEscalationService
{
    public const string AssignmentRole = "Role";
    public const string AssignmentUser = "User";
    public const string AssignmentDepartmentOwner = "DepartmentOwner";
    public const string AssignmentOwner = "Owner";

    public static readonly IReadOnlyList<string> AssignmentTypes = new[] { AssignmentRole, AssignmentUser, AssignmentDepartmentOwner, AssignmentOwner };

    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;

    public EscalationService(AppDbContext context, IConfigCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public Task<EscalationPolicy> GetPolicyAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync("escalation-policy", async () =>
        {
            var levels = await _context.EscalationLevelConfigs.AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.LevelNumber)
                .ToListAsync(ct);

            // Level 1 is where every case starts, so its trigger is the early "approaching" reminder to the case owner.
            var first = levels.FirstOrDefault(l => l.LevelNumber == 1);
            decimal? reminder = first != null && string.Equals(first.TriggerType, EscalationTriggers.SlaPercentage, StringComparison.OrdinalIgnoreCase)
                ? first.TriggerValue
                : null;

            return new EscalationPolicy(levels, reminder);
        });

    public EscalationLevelConfig? NextLevel(Case c, EscalationPolicy policy)
    {
        var current = c.EscalationLevel > 0 ? c.EscalationLevel : 1;
        return policy.ActiveLevels.Where(l => l.LevelNumber > current).OrderBy(l => l.LevelNumber).FirstOrDefault();
    }

    public EscalationDecision? FindDueLevel(Case c, SlaSnapshot s, EscalationPolicy policy, DateTime now)
    {
        // A paused clock (Waiting on Customer) cannot escalate: the customer, not the team, is holding the case.
        if (s.IsPaused || s.IsStopped) return null;

        var current = c.EscalationLevel > 0 ? c.EscalationLevel : 1;

        foreach (var level in policy.ActiveLevels.Where(l => l.LevelNumber > current).OrderBy(l => l.LevelNumber))
        {
            var type = EscalationTriggers.Canonical(level.TriggerType);
            var value = level.TriggerValue;

            // A manual-only level is reached by a person, never by the worker: it neither fires nor blocks the levels after it.
            if (type == EscalationTriggers.ManualOnly) continue;

            switch (type)
            {
                case EscalationTriggers.SlaPercentage when value.HasValue && s.Internal.ConsumedPercent >= (double)value.Value:
                    return new EscalationDecision(level, $"SLA consumption reached {s.Internal.ConsumedPercent:0.#}% (threshold {value:0.##}%)");

                case EscalationTriggers.SlaBreached when s.Internal.IsBreached:
                    return new EscalationDecision(level, "Resolution SLA breached");

                case EscalationTriggers.SlaPostBreachHours when value.HasValue && c.SlaBreachedAt.HasValue && now >= c.SlaBreachedAt.Value.AddHours((double)value.Value):
                    return new EscalationDecision(level, $"Resolution SLA breached for more than {value:0.##} hours");

                case EscalationTriggers.FirstResponseBreached when s.FirstResponse.IsBreached:
                    return new EscalationDecision(level, "First response target missed");
            }

            // This level's trigger is not met. Levels are strictly sequential: a later level never fires before it.
            break;
        }

        return null;
    }

    public async Task<User?> ResolveTargetAsync(Case c, EscalationLevelConfig level, CancellationToken ct = default)
    {
        var assignment = string.IsNullOrWhiteSpace(level.AssignmentType) ? AssignmentRole : level.AssignmentType;

        if (string.Equals(assignment, AssignmentOwner, StringComparison.OrdinalIgnoreCase))
            return await ActiveUser(c.OwnerId, ct);

        if (string.Equals(assignment, AssignmentUser, StringComparison.OrdinalIgnoreCase))
            return level.TargetUserId.HasValue ? await ActiveUser(level.TargetUserId.Value, ct) : null;

        if (string.Equals(assignment, AssignmentDepartmentOwner, StringComparison.OrdinalIgnoreCase))
            return await DepartmentOwner(c, ct);

        // Role: an exact match on the role the Host gave the user — never "contains", which would turn "Head Teller" into "Head of CX".
        var role = level.TargetRole?.Trim();
        if (!string.IsNullOrEmpty(role))
        {
            var roleLower = role.ToLower();
            var candidates = await _context.Users.AsNoTracking()
                .Where(u => u.IsActive && u.Role.ToLower() == roleLower && u.Id != c.OwnerId)
                .OrderBy(u => u.Name)
                .ToListAsync(ct);

            // The case's own team first, then anyone holding the role.
            var match = candidates.FirstOrDefault(u => u.DepartmentId == c.DepartmentId) ?? candidates.FirstOrDefault();
            if (match != null) return match;
        }

        // Nobody holds the role: the department's owner is the configured, explicit last resort. No "anyone" fallback.
        return await DepartmentOwner(c, ct);
    }

    private Task<User?> ActiveUser(Guid id, CancellationToken ct) =>
        _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);

    private async Task<User?> DepartmentOwner(Case c, CancellationToken ct)
    {
        var ownerId = await _context.Departments.AsNoTracking()
            .Where(d => d.Id == c.DepartmentId).Select(d => d.OwnerId).FirstOrDefaultAsync(ct);
        return ownerId.HasValue && ownerId.Value != c.OwnerId ? await ActiveUser(ownerId.Value, ct) : null;
    }
}
