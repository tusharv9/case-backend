namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// What makes a level escalate automatically. Each trigger is evaluated from data the SLA engine already keeps, so there is one
/// SLA calculation (business hours, pauses) and escalation only reads it.
/// </summary>
public static class EscalationTriggers
{
    public const string SlaPercentage = "SlaPercentage";
    public const string SlaBreached = "SlaBreached";
    public const string SlaPostBreachHours = "SlaPostBreachHours";
    public const string StatusHours = "StatusHours";
    public const string ManualOnly = "ManualOnly";

    public sealed record Definition(string Type, string Label, bool NeedsValue, string? Unit, decimal Min, decimal Max);

    public static readonly IReadOnlyList<Definition> All = new[]
    {
        new Definition(SlaPercentage, "SLA consumption reaches", true, "%", 1, 100),
        new Definition(SlaBreached, "SLA is breached", false, null, 0, 0),
        new Definition(SlaPostBreachHours, "Hours after the SLA breach", true, "hours", 1, 720),
        new Definition(StatusHours, "Hours in the same status", true, "hours", 1, 720),
        new Definition(ManualOnly, "Manual escalation only", false, null, 0, 0),
    };

    public static Definition? Find(string? type) =>
        All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when valid, otherwise what is wrong.</summary>
    public static string? Validate(string? type, decimal? value)
    {
        var def = Find(type);
        if (def == null) return "Choose when this level is reached automatically (or Manual escalation only).";
        if (!def.NeedsValue) return null;
        if (!value.HasValue) return $"'{def.Label}' needs a value ({def.Unit}).";
        if (value.Value < def.Min || value.Value > def.Max) return $"'{def.Label}' must be between {def.Min:0} and {def.Max:0} {def.Unit}.";
        return null;
    }

    /// <summary>The human-readable form, always derived from the structured trigger so the text can never disagree with what runs.</summary>
    public static string Describe(string? type, decimal? value)
    {
        var def = Find(type);
        if (def == null) return string.Empty;
        if (!def.NeedsValue) return def.Label;
        if (!value.HasValue) return string.Empty;
        return def.Unit == "%" ? $"{def.Label} {value:0.##}%" : $"{def.Label}: {value:0.##}";
    }

    /// <summary>Canonical spelling of a trigger type (or null if unknown).</summary>
    public static string? Canonical(string? type) => Find(type)?.Type;
}

/// <summary>Kept for older clients: the escalation notification's urgency is no longer configured (it never reached the Host).</summary>
public static class EscalationNotificationPriorities
{
    public static readonly IReadOnlyList<string> All = new[] { "Medium", "High", "Critical" };

    public static string? Canonical(string? value) =>
        All.FirstOrDefault(p => string.Equals(p, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What makes an escalation level complete, and what makes a matrix valid. A level is used — by the SLA monitor and by manual
/// escalation — only when it is complete; an incomplete one is shown with what is missing and cannot be saved.
/// </summary>
public static class EscalationLevelRules
{
    public const string RoleField = "Host role";
    public const string ThresholdField = "Trigger value";
    public const string DescriptionField = "Escalation reason / description";
    public const string UserField = "Specific person";

    /// <summary>One level as the matrix is validated.</summary>
    public sealed record Input(int LevelNumber, IReadOnlyList<string> Roles, bool IsInitial, string? TriggerType, decimal? TriggerValue,
        string? Description, string? RoutingStrategy, Guid? TargetUserId, string? FallbackPolicy, bool ManualAllowed)
    {
        public static Input Of(EscalationLevelConfig l) => new(l.LevelNumber, RoleNames(l), l.IsInitial, l.TriggerType, l.TriggerValue,
            l.ActionDescription, l.RoutingStrategy, l.TargetUserId, l.FallbackPolicy, l.ManualAllowed);
    }

    /// <summary>The level's Host role names: its role rows, or (a matrix saved before them) its single <see cref="EscalationLevelConfig.TargetRole"/>.</summary>
    public static IReadOnlyList<string> RoleNames(EscalationLevelConfig l) =>
        l.Roles.Count > 0 ? l.Roles.Select(r => r.RoleName).ToList()
            : string.IsNullOrWhiteSpace(l.TargetRole) ? Array.Empty<string>() : [l.TargetRole.Trim()];

    /// <summary>The fields this level is still missing (empty when complete).</summary>
    public static IReadOnlyList<string> MissingFields(Input l)
    {
        var missing = new List<string>();
        if (l.Roles.Count == 0 || l.Roles.All(string.IsNullOrWhiteSpace)) missing.Add(RoleField);
        if (l.IsInitial) return missing;   // where cases start: no trigger, no routing
        var trigger = EscalationTriggers.Find(l.TriggerType);
        if (trigger == null || (trigger.NeedsValue && !l.TriggerValue.HasValue)) missing.Add(ThresholdField);
        if (string.IsNullOrWhiteSpace(l.Description)) missing.Add(DescriptionField);
        if (EscalationRouting.Canonical(l.RoutingStrategy) == EscalationRouting.SpecificUser && l.TargetUserId is null) missing.Add(UserField);
        return missing;
    }

    public static IReadOnlyList<string> MissingFields(EscalationLevelConfig l) => MissingFields(Input.Of(l));

    public static bool IsComplete(EscalationLevelConfig l) => MissingFields(l).Count == 0;

    /// <summary>
    /// Every problem with a matrix about to be saved, keyed by level ("Level 2"): numbering, incomplete levels, a role on two
    /// levels, a role without Case Management access, an initial level that is not the lowest, thresholds that do not rise
    /// with the level, and a highest level that would send its cases to a level that does not exist.
    /// </summary>
    /// <param name="allowedRoles">The Host roles granted Case Management access; null skips that check.</param>
    public static IReadOnlyList<FieldError> Validate(IReadOnlyList<Input> levels, IReadOnlyCollection<string>? allowedRoles)
    {
        var errors = new List<FieldError>();
        var ordered = levels.OrderBy(l => l.LevelNumber).ToList();
        if (levels.GroupBy(l => l.LevelNumber).Any(g => g.Count() > 1))
            errors.Add(new FieldError("escalationLevels", "Escalation level numbers must be unique."));
        if (ordered.Select((l, i) => l.LevelNumber != i + 1).Any(gap => gap))
            errors.Add(new FieldError("escalationLevels", "Escalation levels are numbered 1, 2, 3… without gaps."));

        var allowed = allowedRoles == null ? null : new HashSet<string>(allowedRoles, StringComparer.OrdinalIgnoreCase);
        var seenRoles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, (decimal Value, int Level)>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < ordered.Count; i++)
        {
            var l = ordered[i];
            var key = $"Level {l.LevelNumber}";
            var missing = MissingFields(l);
            if (missing.Count > 0)
                errors.Add(new FieldError(key, $"{key} is incomplete — missing: {string.Join(", ", missing)}."));

            if (l.IsInitial && i > 0)
                errors.Add(new FieldError(key, $"{key}: only the lowest level can be the initial handlers' level."));

            if (!l.IsInitial)
            {
                if (EscalationTriggers.Validate(l.TriggerType, l.TriggerValue) is { } triggerError && EscalationTriggers.Find(l.TriggerType) != null)
                    errors.Add(new FieldError(key, $"{key}: {triggerError}"));
                if (EscalationRouting.Canonical(l.RoutingStrategy) == null)
                    errors.Add(new FieldError(key, $"{key}: choose who receives the case (routing)."));
                if (EscalationFallbacks.Canonical(l.FallbackPolicy) == null)
                    errors.Add(new FieldError(key, $"{key}: choose what happens when nobody can receive the case."));
                if (EscalationTriggers.Canonical(l.TriggerType) == EscalationTriggers.ManualOnly && !l.ManualAllowed)
                    errors.Add(new FieldError(key, $"{key} can never be reached: it is manual only, and manual escalation to it is switched off."));
                if (i == ordered.Count - 1 && EscalationFallbacks.Canonical(l.FallbackPolicy) == EscalationFallbacks.NextLevel)
                    errors.Add(new FieldError(key, $"{key} is the highest level, so there is no level above to fall back to. Choose Administrators or Hold."));
            }

            foreach (var raw in l.Roles)
            {
                var role = raw?.Trim();
                if (string.IsNullOrEmpty(role)) continue;
                if (seenRoles.TryGetValue(role, out var other) && other != l.LevelNumber)
                    errors.Add(new FieldError(key, $"{key}: the role '{role}' is already at Level {other}. A role belongs to one level only."));
                else
                    seenRoles[role] = l.LevelNumber;

                if (allowed != null && !allowed.Contains(role))
                    errors.Add(new FieldError(key, $"{key}: {role} is not a Host role with Case Management access. Select an eligible role."));
            }

            // Thresholds of the same kind rise with the level: a higher level is never reached before a lower one.
            if (!l.IsInitial && l.TriggerValue.HasValue && EscalationTriggers.Canonical(l.TriggerType) is { } type
                && EscalationTriggers.Find(type)!.NeedsValue)
            {
                if (previous.TryGetValue(type, out var p) && l.TriggerValue.Value <= p.Value)
                    errors.Add(new FieldError(key,
                        $"{key}: its threshold ({EscalationTriggers.Describe(type, l.TriggerValue)}) must be higher than Level {p.Level}'s ({EscalationTriggers.Describe(type, p.Value)})."));
                previous[type] = (l.TriggerValue.Value, l.LevelNumber);
            }
        }

        return errors;
    }
}

/// <summary>The escalation configuration the engine works from: the active, complete levels in order.</summary>
public sealed record EscalationPolicy(IReadOnlyList<EscalationLevelConfig> ActiveLevels)
{
    public int MaxLevel => ActiveLevels.Count == 0 ? 0 : ActiveLevels.Max(l => l.LevelNumber);

    /// <summary>The initial handlers' level, when the matrix has one.</summary>
    public EscalationLevelConfig? InitialLevel => ActiveLevels.OrderBy(l => l.LevelNumber).FirstOrDefault(l => l.IsInitial);

    /// <summary>The lowest escalation threshold (SLA %): from here on a case counts as approaching its SLA.</summary>
    public decimal? ReminderPercent => ActiveLevels.Where(l => !l.IsInitial && EscalationTriggers.Canonical(l.TriggerType) == EscalationTriggers.SlaPercentage)
        .OrderBy(l => l.LevelNumber).FirstOrDefault()?.TriggerValue;

    /// <summary>Where the case stands: the level it reached, or the initial level when it has not been escalated.</summary>
    public int EffectiveLevel(Case c) => Math.Max(Math.Max(0, c.EscalationLevel), InitialLevel?.LevelNumber ?? 0);

    /// <summary>The escalation level directly above <paramref name="from"/>, or null at the top.</summary>
    public EscalationLevelConfig? Above(int from) =>
        ActiveLevels.Where(l => !l.IsInitial && l.LevelNumber > from).OrderBy(l => l.LevelNumber).FirstOrDefault();

    /// <summary>The level a Host role sits at, or null when no level holds it.</summary>
    public EscalationLevelConfig? LevelOfRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? null
            : ActiveLevels.FirstOrDefault(l => EscalationLevelRules.RoleNames(l).Any(r => string.Equals(r, role.Trim(), StringComparison.OrdinalIgnoreCase)));

    public string NameOf(int level) =>
        ActiveLevels.FirstOrDefault(l => l.LevelNumber == level)?.Name ?? (level > 0 ? $"Level {level}" : "Not escalated");
}

/// <summary>The automatic escalation due now: the next level and why.</summary>
public sealed record EscalationDecision(EscalationLevelConfig Level, string Reason, string Trigger, decimal? SlaConsumedPercent);

/// <summary>Someone a case can be escalated to at one level, and how they were found (CaseTeam, Department, Role, User, Administrators).</summary>
public sealed record EscalationRecipient(User User, bool IsCaseTeam, string Scope);

/// <summary>
/// Where an escalation lands: the level actually reached (higher than asked when a fallback went up), the people it reaches, and
/// what the fallback did when the asked level had nobody.
/// </summary>
public sealed record EscalationTarget(EscalationLevelConfig Level, IReadOnlyList<EscalationRecipient> Recipients, string? FallbackNote)
{
    public bool HasRecipients => Recipients.Count > 0;
}

public interface IEscalationService
{
    Task<EscalationPolicy> GetPolicyAsync(CancellationToken ct = default);

    /// <summary>The next level, when its automatic trigger is met right now; null otherwise. One level at a time, never skipping.</summary>
    EscalationDecision? FindDueLevel(Case c, SlaSnapshot s, EscalationPolicy policy, DateTime now);

    /// <summary>
    /// The level a person may escalate this case to by hand: the one directly above both the case's level and the person's own
    /// (from their Host role), or null when there is none. The server decides it; a level sent by a client is never trusted.
    /// </summary>
    EscalationLevelConfig? ManualTarget(Case c, EscalationPolicy policy, string? callerRole);

    /// <summary>
    /// Who receives the case at <paramref name="level"/> under its routing strategy, applying its fallback when nobody is found
    /// (the level above, the administrators, or nobody). Never the caller or the current holder; never an unrelated person.
    /// </summary>
    Task<EscalationTarget> ResolveAsync(Case c, EscalationLevelConfig level, EscalationPolicy policy, Guid? callerId, CancellationToken ct = default);
}

public class EscalationService : IEscalationService
{
    public const string AssignmentRole = "Role";

    private static readonly CaseStatus[] Finished = [CaseStatus.Resolved, CaseStatus.Closed, CaseStatus.Cancelled];

    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;
    private readonly IOrgDirectory? _directory;

    public EscalationService(AppDbContext context, IConfigCache cache, IOrgDirectory? directory = null, ICaseAudience? audience = null)
    {
        _context = context;
        _cache = cache;
        _directory = directory;
    }

    public Task<EscalationPolicy> GetPolicyAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync("escalation-policy", async () =>
        {
            var levels = await _context.EscalationLevelConfigs.AsNoTracking()
                .Include(l => l.Roles)
                .Where(l => l.IsActive)
                .OrderBy(l => l.LevelNumber)
                .ToListAsync(ct);

            // An incomplete level is never executed, by the monitor or by a person.
            return new EscalationPolicy(levels.Where(EscalationLevelRules.IsComplete).ToList());
        });

    public EscalationDecision? FindDueLevel(Case c, SlaSnapshot s, EscalationPolicy policy, DateTime now)
    {
        // A paused clock (Waiting on Customer) cannot escalate: the customer, not the team, is holding the case.
        if (s.IsPaused || s.IsStopped) return null;

        var next = policy.Above(policy.EffectiveLevel(c));
        if (next == null || !AppliesTo(next, c)) return null;

        var consumed = (decimal)Math.Round(s.Internal.ConsumedPercent, 1);
        var type = EscalationTriggers.Canonical(next.TriggerType);
        var value = next.TriggerValue;
        string? reason = type switch
        {
            EscalationTriggers.SlaPercentage when value.HasValue && s.Internal.ConsumedPercent >= (double)value.Value =>
                $"{consumed:0.#}% of the SLA was consumed (threshold {value:0.##}%)",
            EscalationTriggers.SlaBreached when s.Internal.IsBreached =>
                "the SLA was breached",
            EscalationTriggers.SlaPostBreachHours when value.HasValue && s.Internal.IsBreached && c.SlaBreachedAt is { } breachedAt
                && (now - breachedAt).TotalHours >= (double)value.Value =>
                $"the SLA has been breached for {(now - breachedAt).TotalHours:0.#} hours (threshold {value:0.##} hours)",
            EscalationTriggers.StatusHours when value.HasValue && c.StatusEnteredAt is { } entered && (now - entered).TotalHours >= (double)value.Value =>
                $"the case has been {CaseWorkflow.Label(c.Status)} for {(now - entered).TotalHours:0.#} hours (threshold {value:0.##} hours)",
            _ => null,
        };
        return reason == null ? null : new EscalationDecision(next, reason, EscalationTriggers.Describe(type, value), consumed);
    }

    /// <summary>The level's priority filter: empty = every priority.</summary>
    private static bool AppliesTo(EscalationLevelConfig level, Case c)
    {
        if (string.IsNullOrWhiteSpace(level.TriggerPriorities)) return true;
        return level.TriggerPriorities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => string.Equals(p, c.Severity, StringComparison.OrdinalIgnoreCase));
    }

    public EscalationLevelConfig? ManualTarget(Case c, EscalationPolicy policy, string? callerRole)
    {
        var from = Math.Max(policy.EffectiveLevel(c), policy.LevelOfRole(callerRole)?.LevelNumber ?? 0);
        var next = policy.Above(from);
        return next is { ManualAllowed: true } ? next : null;
    }

    public async Task<EscalationTarget> ResolveAsync(Case c, EscalationLevelConfig level, EscalationPolicy policy, Guid? callerId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var current = level;
        while (true)
        {
            var found = await RouteAsync(c, current, callerId, ct);
            if (found.Count > 0)
                return new EscalationTarget(current, found, notes.Count == 0 ? null : string.Join(" ", notes));

            var why = $"{current.Name} had nobody to receive the case ({DescribeRouting(current)}).";
            switch (EscalationFallbacks.Canonical(current.FallbackPolicy) ?? EscalationFallbacks.Hold)
            {
                case EscalationFallbacks.NextLevel when policy.Above(current.LevelNumber) is { } up:
                    notes.Add($"{why} It went up to {up.Name}.");
                    current = up;
                    continue;
                case EscalationFallbacks.Administrators when _directory != null:
                    var admins = (await _directory.AdministratorsAsync(ct))
                        .Where(u => u.IsActive && u.Id != callerId && u.Id != c.OwnerId)
                        .Select(u => new EscalationRecipient(u, false, "Administrators")).ToList();
                    notes.Add(admins.Count > 0 ? $"{why} It went to the administrators." : $"{why} No administrator could receive it either.");
                    return new EscalationTarget(current, admins, string.Join(" ", notes));
                default:
                    notes.Add($"{why} The case stays with its holder.");
                    return new EscalationTarget(current, Array.Empty<EscalationRecipient>(), string.Join(" ", notes));
            }
        }
    }

    private static string DescribeRouting(EscalationLevelConfig l) => (EscalationRouting.Canonical(l.RoutingStrategy) ?? EscalationRouting.Team) switch
    {
        EscalationRouting.AllHolders => $"no active person holds {string.Join(" or ", EscalationLevelRules.RoleNames(l))}",
        EscalationRouting.SpecificUser => "the chosen person is not active or no longer holds the level's role",
        _ => $"no active {string.Join(" or ", EscalationLevelRules.RoleNames(l))} in the case's team or department",
    };

    /// <summary>The level's own routing, without any fallback.</summary>
    private async Task<IReadOnlyList<EscalationRecipient>> RouteAsync(Case c, EscalationLevelConfig level, Guid? callerId, CancellationToken ct)
    {
        var holders = (await HoldersAsync(level, ct)).Where(u => u.IsActive && u.Id != callerId && u.Id != c.OwnerId).ToList();
        if (holders.Count == 0) return Array.Empty<EscalationRecipient>();

        switch (EscalationRouting.Canonical(level.RoutingStrategy) ?? EscalationRouting.Team)
        {
            case EscalationRouting.AllHolders:
                return holders.Select(u => new EscalationRecipient(u, false, "Role")).ToList();

            case EscalationRouting.SpecificUser:
                return holders.Where(u => u.Id == level.TargetUserId).Select(u => new EscalationRecipient(u, false, "User")).ToList();

            case EscalationRouting.OneHolder:
            {
                var pool = await TeamScopedAsync(c, holders, ct);
                var candidates = pool.Count > 0 ? pool : holders.Select(u => new EscalationRecipient(u, false, "Role")).ToList();
                var ids = candidates.Select(r => r.User.Id).ToList();
                var load = await _context.Cases.AsNoTracking()
                    .Where(x => ids.Contains(x.OwnerId) && !Finished.Contains(x.Status))
                    .GroupBy(x => x.OwnerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
                var chosen = candidates.OrderBy(r => load.GetValueOrDefault(r.User.Id)).ThenBy(r => r.User.Name, StringComparer.OrdinalIgnoreCase).First();
                return [chosen];
            }

            default:
                return await TeamScopedAsync(c, holders, ct);
        }
    }

    /// <summary>Holders who lead or belong to the case's team; else to the teams of the case's department; else nobody.</summary>
    private async Task<IReadOnlyList<EscalationRecipient>> TeamScopedAsync(Case c, IReadOnlyList<User> holders, CancellationToken ct)
    {
        var ids = holders.Select(u => u.Id).ToList();
        var caseTeam = await PeopleOfTeamsAsync(new[] { c.TeamId }, ids, ct);
        if (caseTeam.Count > 0)
            return holders.Where(u => caseTeam.Contains(u.Id)).Select(u => new EscalationRecipient(u, true, "CaseTeam")).ToList();

        // A team can serve several departments, and a department several teams: the case's own department decides here.
        if (c.DepartmentId.HasValue)
        {
            var deptTeams = await _context.TeamDepartments.AsNoTracking()
                .Where(t => t.DepartmentId == c.DepartmentId.Value && t.Team.IsActive).Select(t => t.TeamId).ToListAsync(ct);
            var deptPeople = await PeopleOfTeamsAsync(deptTeams, ids, ct);
            if (deptPeople.Count > 0)
                return holders.Where(u => deptPeople.Contains(u.Id)).Select(u => new EscalationRecipient(u, false, "Department")).ToList();
        }
        return Array.Empty<EscalationRecipient>();
    }

    /// <summary>Active people holding any of the level's roles (by Host role id when the level has role rows).</summary>
    private async Task<IReadOnlyList<User>> HoldersAsync(EscalationLevelConfig level, CancellationToken ct)
    {
        if (_directory == null) return Array.Empty<User>();
        var refs = level.Roles.Count > 0
            ? level.Roles.Select(r => new HostRoleRef(r.RoleKey, r.RoleName)).ToList()
            : EscalationLevelRules.RoleNames(level).Select(n => new HostRoleRef(n, n)).ToList();
        var people = new List<User>();
        foreach (var role in refs) people.AddRange(await _directory.UsersInRoleRefAsync(role, ct));
        return people.DistinctBy(u => u.Id).ToList();
    }

    /// <summary>Which of <paramref name="userIds"/> lead or are active members of any of <paramref name="teamIds"/>.</summary>
    private async Task<HashSet<Guid>> PeopleOfTeamsAsync(IReadOnlyCollection<Guid> teamIds, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (teamIds.Count == 0) return new HashSet<Guid>();
        var members = await _context.TeamMembers.AsNoTracking()
            .Where(m => m.IsActive && teamIds.Contains(m.TeamId) && userIds.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct);
        var leads = await _context.Teams.AsNoTracking()
            .Where(d => teamIds.Contains(d.Id) && d.OwnerId != null && userIds.Contains(d.OwnerId.Value)).Select(d => d.OwnerId!.Value).ToListAsync(ct);
        return members.Concat(leads).ToHashSet();
    }
}
