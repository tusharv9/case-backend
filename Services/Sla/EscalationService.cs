namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The escalation trigger the engine executes: SLA consumption reaching a percentage. Older matrices also stored breach,
/// post-breach and first-response triggers; a level still carrying one of those is incomplete (it never fires) until an
/// administrator gives it a consumption threshold.
/// </summary>
public static class EscalationTriggers
{
    public const string SlaPercentage = "SlaPercentage";

    public sealed record Definition(string Type, string Label, bool NeedsValue, string? Unit, decimal Min, decimal Max);

    public static readonly IReadOnlyList<Definition> All = new[]
    {
        new Definition(SlaPercentage, "SLA consumption reaches", true, "%", 1, 100),
    };

    public static Definition? Find(string? type) =>
        All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when valid, otherwise what is wrong.</summary>
    public static string? Validate(string? type, decimal? value)
    {
        var def = Find(type);
        if (def == null) return "Automatic escalation runs on SLA consumption: enter the percentage at which this level is reached.";
        if (!value.HasValue) return $"'{def.Label}' needs a value ({def.Unit}).";
        if (value.Value < def.Min || value.Value > def.Max) return $"The SLA consumption threshold must be between {def.Min:0} and {def.Max:0}%.";
        return null;
    }

    /// <summary>The human-readable form, always derived from the structured trigger so the text can never disagree with what runs.</summary>
    public static string Describe(string? type, decimal? value)
    {
        var def = Find(type);
        if (def == null || !value.HasValue) return string.Empty;
        return $"{def.Label} {value:0.##}%";
    }

    /// <summary>Canonical spelling of a trigger type (or null if unknown).</summary>
    public static string? Canonical(string? type) => Find(type)?.Type;
}

/// <summary>How urgent the escalation notification is.</summary>
public static class EscalationNotificationPriorities
{
    public static readonly IReadOnlyList<string> All = new[] { "Medium", "High", "Critical" };

    public static string? Canonical(string? value) =>
        All.FirstOrDefault(p => string.Equals(p, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What makes an escalation level complete. A level is used — by the SLA monitor and by manual escalation — only when it
/// is complete; an incomplete one is shown with what is missing and cannot be saved.
/// </summary>
public static class EscalationLevelRules
{
    public const string RoleField = "Assigned role";
    public const string ThresholdField = "SLA consumption threshold";
    public const string DescriptionField = "Escalation reason / description";
    public const string NotificationField = "Notification";

    /// <summary>The fields this level is still missing (empty when complete).</summary>
    public static IReadOnlyList<string> MissingFields(string? targetRole, string? triggerType, decimal? triggerValue, string? description,
        bool notifyRoleHolders, bool notifyCaseOwner, bool notifyTeamLead, string? notificationPriority)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(targetRole)) missing.Add(RoleField);
        if (EscalationTriggers.Validate(triggerType, triggerValue) != null) missing.Add(ThresholdField);
        if (string.IsNullOrWhiteSpace(description)) missing.Add(DescriptionField);
        if (!(notifyRoleHolders || notifyCaseOwner || notifyTeamLead) || EscalationNotificationPriorities.Canonical(notificationPriority) == null)
            missing.Add(NotificationField);
        return missing;
    }

    public static IReadOnlyList<string> MissingFields(EscalationLevelConfig l) =>
        MissingFields(l.TargetRole, l.TriggerType, l.TriggerValue, l.ActionDescription, l.NotifyRoleHolders, l.NotifyCaseOwner, l.NotifyTeamLead, l.NotificationPriority);

    public static bool IsComplete(EscalationLevelConfig l) => MissingFields(l).Count == 0;

    /// <summary>One level as the matrix is validated: its number and the editable fields.</summary>
    public sealed record Input(int LevelNumber, string? TargetRole, string? TriggerType, decimal? TriggerValue, string? Description,
        bool NotifyRoleHolders, bool NotifyCaseOwner, bool NotifyTeamLead, string? NotificationPriority);

    /// <summary>
    /// Every problem with a matrix about to be saved, keyed by level ("Level 2"): incomplete levels, a role used twice,
    /// a role without Case Management access, and thresholds that do not rise with the level.
    /// </summary>
    /// <param name="allowedRoles">The Host roles granted Case Management access; null skips that check.</param>
    /// <param name="unchangedRoles">Level number → role already stored there, which stays allowed even if no longer offered.</param>
    public static IReadOnlyList<FieldError> Validate(IReadOnlyList<Input> levels, IReadOnlyCollection<string>? allowedRoles,
        IReadOnlyDictionary<int, string>? unchangedRoles = null)
    {
        var errors = new List<FieldError>();
        if (levels.GroupBy(l => l.LevelNumber).Any(g => g.Count() > 1))
            errors.Add(new FieldError("escalationLevels", "Escalation level numbers must be unique."));
        if (levels.Any(l => l.LevelNumber < 1))
            errors.Add(new FieldError("escalationLevels", "Escalation level numbers start at 1."));

        var allowed = allowedRoles == null ? null : new HashSet<string>(allowedRoles, StringComparer.OrdinalIgnoreCase);
        var seenRoles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        decimal? previousThreshold = null;
        int? previousLevel = null;

        foreach (var l in levels.OrderBy(l => l.LevelNumber))
        {
            var key = $"Level {l.LevelNumber}";
            var missing = MissingFields(l.TargetRole, l.TriggerType, l.TriggerValue, l.Description,
                l.NotifyRoleHolders, l.NotifyCaseOwner, l.NotifyTeamLead, l.NotificationPriority);
            if (missing.Count > 0)
                errors.Add(new FieldError(key, $"{key} is incomplete — missing: {string.Join(", ", missing)}."));

            if (EscalationTriggers.Validate(l.TriggerType, l.TriggerValue) is { } triggerError && l.TriggerValue.HasValue)
                errors.Add(new FieldError(key, $"{key}: {triggerError}"));

            var role = l.TargetRole?.Trim();
            if (!string.IsNullOrEmpty(role))
            {
                if (seenRoles.TryGetValue(role, out var other))
                    errors.Add(new FieldError(key, $"{key}: the role '{role}' is already assigned to Level {other}. A role can be used by only one level."));
                else
                    seenRoles[role] = l.LevelNumber;

                var keptAsIs = unchangedRoles != null && unchangedRoles.TryGetValue(l.LevelNumber, out var stored)
                               && string.Equals(stored?.Trim(), role, StringComparison.OrdinalIgnoreCase);
                if (allowed != null && !allowed.Contains(role) && !keptAsIs)
                    errors.Add(new FieldError(key, $"{key}: the role '{role}' does not have access to Case Management in OmniConnect."));
            }

            if (l.TriggerValue.HasValue && previousThreshold.HasValue && l.TriggerValue.Value <= previousThreshold.Value)
                errors.Add(new FieldError(key,
                    $"{key}: its SLA consumption threshold ({l.TriggerValue:0.##}%) must be higher than Level {previousLevel}'s ({previousThreshold:0.##}%)."));
            if (l.TriggerValue.HasValue) { previousThreshold = l.TriggerValue; previousLevel = l.LevelNumber; }
        }

        return errors;
    }
}

/// <summary>The escalation configuration the engine works from: the active, complete levels in order.</summary>
public sealed record EscalationPolicy(IReadOnlyList<EscalationLevelConfig> ActiveLevels)
{
    public int MaxLevel => ActiveLevels.Count == 0 ? 0 : ActiveLevels.Max(l => l.LevelNumber);

    /// <summary>The first level's threshold: from here on a case counts as approaching its SLA.</summary>
    public decimal? ReminderPercent => ActiveLevels.OrderBy(l => l.LevelNumber).FirstOrDefault()?.TriggerValue;
}

public sealed record EscalationDecision(EscalationLevelConfig Level, string Reason);

/// <summary>Someone a case can be escalated to at one level, and whether they belong to the case's team.</summary>
public sealed record EscalationRecipient(User User, bool IsCaseTeam, string Scope);

public interface IEscalationService
{
    Task<EscalationPolicy> GetPolicyAsync(CancellationToken ct = default);

    /// <summary>The highest level above the case's current one whose SLA threshold is met right now, or null.</summary>
    EscalationDecision? FindDueLevel(Case c, SlaSnapshot snapshot, EscalationPolicy policy, DateTime nowUtc);

    /// <summary>The active levels above the case's current one, lowest first (what manual escalation may choose).</summary>
    IReadOnlyList<EscalationLevelConfig> LevelsAbove(Case c, EscalationPolicy policy);

    /// <summary>
    /// Who may receive the case at <paramref name="level"/>: active people holding the level's role, narrowed to the case's
    /// own team when any of them belong to it, else to the teams serving the case's department, else everyone in the role.
    /// The caller and the current holder are never offered.
    /// </summary>
    Task<IReadOnlyList<EscalationRecipient>> RecipientsAsync(Case c, EscalationLevelConfig level, Guid? callerId, CancellationToken ct = default);

    /// <summary>Who is told when the case reaches <paramref name="level"/> automatically (the level's notification settings).</summary>
    Task<IReadOnlyList<Guid>> NotificationRecipientsAsync(Case c, EscalationLevelConfig level, CancellationToken ct = default);
}

public class EscalationService : IEscalationService
{
    public const string AssignmentRole = "Role";

    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;
    private readonly IOrgDirectory? _directory;

    public EscalationService(AppDbContext context, IConfigCache cache, IOrgDirectory? directory = null)
    {
        _context = context;
        _cache = cache;
        _directory = directory;
    }

    public Task<EscalationPolicy> GetPolicyAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync("escalation-policy", async () =>
        {
            var levels = await _context.EscalationLevelConfigs.AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.LevelNumber)
                .ToListAsync(ct);

            // An incomplete level is never executed, by the monitor or by a person.
            return new EscalationPolicy(levels.Where(EscalationLevelRules.IsComplete).ToList());
        });

    public IReadOnlyList<EscalationLevelConfig> LevelsAbove(Case c, EscalationPolicy policy) =>
        policy.ActiveLevels.Where(l => l.LevelNumber > Math.Max(0, c.EscalationLevel)).OrderBy(l => l.LevelNumber).ToList();

    public EscalationDecision? FindDueLevel(Case c, SlaSnapshot s, EscalationPolicy policy, DateTime now)
    {
        // A paused clock (Waiting on Customer) cannot escalate: the customer, not the team, is holding the case.
        if (s.IsPaused || s.IsStopped) return null;

        // Levels are reached in order of their (rising) thresholds; a cycle that finds several met reaches the highest.
        var due = LevelsAbove(c, policy)
            .Where(l => l.TriggerValue.HasValue && s.Internal.ConsumedPercent >= (double)l.TriggerValue.Value)
            .LastOrDefault();
        return due == null
            ? null
            : new EscalationDecision(due, $"SLA consumption reached {s.Internal.ConsumedPercent:0.#}% (threshold {due.TriggerValue:0.##}%)");
    }

    public async Task<IReadOnlyList<EscalationRecipient>> RecipientsAsync(Case c, EscalationLevelConfig level, Guid? callerId, CancellationToken ct = default)
    {
        if (_directory == null || string.IsNullOrWhiteSpace(level.TargetRole)) return Array.Empty<EscalationRecipient>();

        var holders = (await _directory.UsersInRoleAsync(level.TargetRole, ct))
            .Where(u => u.IsActive && u.Id != callerId && u.Id != c.OwnerId)
            .ToList();
        if (holders.Count == 0) return Array.Empty<EscalationRecipient>();

        var ids = holders.Select(u => u.Id).ToList();
        var caseTeam = await PeopleOfTeamsAsync(new[] { c.DepartmentId }, ids, ct);
        if (caseTeam.Count > 0)
            return holders.Where(u => caseTeam.Contains(u.Id)).Select(u => new EscalationRecipient(u, true, "CaseTeam")).ToList();

        // A team can serve several departments, and a department several teams: the case's own department decides here.
        if (c.OrgDepartmentId.HasValue)
        {
            var deptTeams = await _context.TeamDepartments.AsNoTracking()
                .Where(t => t.OrgDepartmentId == c.OrgDepartmentId.Value && t.Team.IsActive).Select(t => t.TeamId).ToListAsync(ct);
            var deptPeople = await PeopleOfTeamsAsync(deptTeams, ids, ct);
            if (deptPeople.Count > 0)
                return holders.Where(u => deptPeople.Contains(u.Id)).Select(u => new EscalationRecipient(u, false, "Department")).ToList();
        }

        return holders.Select(u => new EscalationRecipient(u, false, "Role")).ToList();
    }

    public async Task<IReadOnlyList<Guid>> NotificationRecipientsAsync(Case c, EscalationLevelConfig level, CancellationToken ct = default)
    {
        var result = new List<Guid>();
        if (level.NotifyRoleHolders)
        {
            // Everyone the level could be escalated to; the holder is told separately when NotifyCaseOwner is set.
            result.AddRange((await RecipientsAsync(c, level, null, ct)).Select(r => r.User.Id));
        }
        if (level.NotifyCaseOwner) result.Add(c.OwnerId);
        if (level.NotifyTeamLead)
        {
            var lead = await _context.Departments.AsNoTracking().Where(d => d.Id == c.DepartmentId).Select(d => d.OwnerId).FirstOrDefaultAsync(ct);
            if (lead.HasValue) result.Add(lead.Value);
        }
        return result.Distinct().ToList();
    }

    /// <summary>Which of <paramref name="userIds"/> lead or are active members of any of <paramref name="teamIds"/>.</summary>
    private async Task<HashSet<Guid>> PeopleOfTeamsAsync(IReadOnlyCollection<Guid> teamIds, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (teamIds.Count == 0) return new HashSet<Guid>();
        var members = await _context.TeamMembers.AsNoTracking()
            .Where(m => m.IsActive && teamIds.Contains(m.DepartmentId) && userIds.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct);
        var leads = await _context.Departments.AsNoTracking()
            .Where(d => teamIds.Contains(d.Id) && d.OwnerId != null && userIds.Contains(d.OwnerId.Value)).Select(d => d.OwnerId!.Value).ToListAsync(ct);
        return members.Concat(leads).ToHashSet();
    }
}
