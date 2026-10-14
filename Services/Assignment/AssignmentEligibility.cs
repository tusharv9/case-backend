namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>How a team's lead is treated by a policy.</summary>
public enum LeadMode
{
    /// <summary>Like any other member (needs a member row; automatic routing).</summary>
    AsMember,
    /// <summary>Eligible as the team's lead, with or without a member row and whatever their member selection says.</summary>
    Include,
    /// <summary>Never eligible, even with a member row.</summary>
    Exclude,
}

/// <summary>
/// Who may hold a team's cases, decided in ONE place for every entry point: automatic routing at case creation
/// (through <see cref="AgentPoolService"/>), manual assign / reassign, and department transfer. The UI only displays
/// these verdicts; the server enforces them, so a stale screen or a direct API call cannot bypass them.
/// </summary>
public sealed record AssignmentPolicy(
    bool RequireAssignableFlag,    // TeamMember.IsAssignable must be on (it means "receives cases AUTOMATICALLY")
    bool RequireManualSelection,   // TeamMember.CanBeManuallyAssigned must be on (Assign & Reassign → team membership selection)
    LeadMode Leads,
    bool RequireOnline,            // Available or Busy (Users.Status, set by the agent); Away / Offline are refused
    bool EnforceCapacity,          // fewer open cases than the CASE team's configured capacity
    string CrossTeamMode,          // CrossTeamModes: who outside the case's team may be chosen
    IReadOnlySet<Guid> CrossTeamIds)
{
    /// <summary>Automatic routing: exactly the rules routing has always applied (never cross-team).</summary>
    public static readonly AssignmentPolicy Automatic = new(
        RequireAssignableFlag: true, RequireManualSelection: false, Leads: LeadMode.AsMember, RequireOnline: true, EnforceCapacity: true,
        CrossTeamMode: CrossTeamModes.Disabled, CrossTeamIds: new HashSet<Guid>());

    /// <summary>A person choosing the assignee: the administrator's Assign &amp; Reassign settings.</summary>
    public static AssignmentPolicy FromSettings(ManualAssignmentSettings s, IEnumerable<Guid> crossTeamIds) => new(
        RequireAssignableFlag: false,   // IsAssignable only opts a member out of AUTOMATIC routing
        RequireManualSelection: true,
        Leads: s.IncludeTeamLeads ? LeadMode.Include : LeadMode.Exclude,
        RequireOnline: s.RequireAvailable,
        EnforceCapacity: s.EnforceCapacity,
        CrossTeamMode: s.CrossTeamMode,
        CrossTeamIds: crossTeamIds.ToHashSet());

    public bool AllowsCrossTeam => CrossTeamMode != CrossTeamModes.Disabled;
}

/// <summary>Why someone cannot take a case. Serialised by name (the API's machine-readable reason code).</summary>
public enum IneligibilityReason
{
    None,
    TeamInactive,
    NotTeamMember,
    UserInactive,
    NotAssignable,
    NotSelected,
    LeadExcluded,
    Unavailable,
    AtCapacity,
    NeedsAssignPermission,
}

public sealed record AssigneeVerdict(
    User User,
    bool IsMember,
    bool IsTeamLead,
    bool IsAssignable,
    string? MemberRole,
    int OpenCases,
    int Capacity,
    IneligibilityReason Reason,
    Guid HomeTeamId,
    string HomeTeamName,
    bool IsCrossTeam = false)
{
    public bool IsEligible => Reason == IneligibilityReason.None;

    public string Explain(string teamName) => Reason switch
    {
        IneligibilityReason.None => string.Empty,
        IneligibilityReason.TeamInactive => $"'{teamName}' is inactive, so nobody can be given its cases. Transfer the case to an active team.",
        IneligibilityReason.NotTeamMember => $"{User.Name} is not a member of '{teamName}', and assigning outside the team is not allowed for them.",
        IneligibilityReason.UserInactive => $"{User.Name}'s account is deactivated.",
        IneligibilityReason.NotAssignable => $"{User.Name} does not receive cases automatically in '{HomeTeamName}'.",
        IneligibilityReason.NotSelected => $"{User.Name} is not selected for manual assignment in '{HomeTeamName}' (Assign & Reassign settings).",
        IneligibilityReason.LeadExcluded => $"Team leads cannot be chosen (Assign & Reassign settings), and {User.Name} leads '{HomeTeamName}'.",
        IneligibilityReason.Unavailable => $"{User.Name} is {User.Status} and cannot take cases right now.",
        IneligibilityReason.AtCapacity => $"{User.Name} already holds {OpenCases} open case(s), the team's capacity is {Capacity}.",
        IneligibilityReason.NeedsAssignPermission => $"{User.Name} is in '{HomeTeamName}'. Assigning outside the case's team needs the 'cases.assign' permission.",
        _ => Reason.ToString(),
    };
}

public sealed record TeamEligibility(Department Team, int Capacity, IReadOnlyList<AssigneeVerdict> Candidates)
{
    public IEnumerable<AssigneeVerdict> Eligible => Candidates.Where(c => c.IsEligible);
}

public interface IAssignmentEligibilityService
{
    /// <summary>The administrator's Assign &amp; Reassign policy (cached; cleared when the settings are saved).</summary>
    Task<AssignmentPolicy> GetManualPolicyAsync(CancellationToken ct = default);

    /// <summary>Every person of ONE team who could hold its cases under <paramref name="policy"/>, each with a verdict.</summary>
    Task<TeamEligibility> EvaluateTeamAsync(Guid departmentId, AssignmentPolicy policy, int? capacity = null, CancellationToken ct = default);

    /// <summary>Everyone who could hold a case of the given team: its own people, plus the cross-team people the policy allows.</summary>
    Task<TeamEligibility> EvaluateForCaseAsync(Guid caseDepartmentId, AssignmentPolicy policy, CancellationToken ct = default);

    /// <summary>The verdict for one person (also for someone outside the team). Throws <see cref="KeyNotFoundException"/> for an unknown user.</summary>
    Task<(TeamEligibility Team, AssigneeVerdict Verdict)> CheckAsync(Guid departmentId, Guid userId, AssignmentPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="actorId"/> may change who holds <paramref name="c"/>: the current owner, the lead or an active member of
    /// the case's team, or anyone holding <c>cases.assign</c>. Throws <see cref="AssignmentForbiddenException"/> otherwise.
    /// </summary>
    Task EnsureMayReassignAsync(Case c, Guid actorId, bool hasAssignPermission, CancellationToken ct = default);
}

public class AssignmentEligibilityService : IAssignmentEligibilityService
{
    public const string ManualPolicyCacheKey = "assignment:manual-policy";

    private readonly AppDbContext _context;
    private readonly IConfigCache? _cache;

    public AssignmentEligibilityService(AppDbContext context, IConfigCache? cache = null)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<AssignmentPolicy> GetManualPolicyAsync(CancellationToken ct = default)
    {
        async Task<AssignmentPolicy> Load()
        {
            var settings = await _context.ManualAssignmentSettings.AsNoTracking().Include(m => m.CrossTeams).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("The Assign & Reassign settings are missing. Apply the database migrations, then review them under Configurable Settings.");
            return AssignmentPolicy.FromSettings(settings, settings.CrossTeams.Select(c => c.DepartmentId));
        }
        return _cache == null ? await Load() : await _cache.GetOrCreateAsync(ManualPolicyCacheKey, Load);
    }

    public async Task<TeamEligibility> EvaluateTeamAsync(Guid departmentId, AssignmentPolicy policy, int? capacity = null, CancellationToken ct = default)
    {
        var team = await _context.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
                   ?? throw new KeyNotFoundException("Team not found.");
        var cap = capacity ?? (await AssignmentSettings.ResolveAsync(_context, _cache, departmentId, ct)).MaxConcurrentCapacity;

        var people = await PeopleOfAsync(team, policy, ct);
        var open = await OpenCaseCountsAsync(people.Select(p => p.User.Id).ToList(), ct);

        var verdicts = people
            .Select(p => Judge(team, policy, cap, p.User, p.IsMember, p.IsAssignable, p.CanManual, p.Role, open.GetValueOrDefault(p.User.Id)))
            .OrderBy(v => v.User.Id)   // stable order: the round-robin pointer relies on it
            .ToList();

        return new TeamEligibility(team, cap, verdicts);
    }

    public async Task<TeamEligibility> EvaluateForCaseAsync(Guid caseDepartmentId, AssignmentPolicy policy, CancellationToken ct = default)
    {
        var own = await EvaluateTeamAsync(caseDepartmentId, policy, null, ct);
        if (!policy.AllowsCrossTeam) return own;

        // Other active teams the policy draws on. Their people are judged against THEIR team's membership rules and the CASE team's
        // capacity (the case stays the case team's work). Someone already listed for the case's own team is not repeated.
        var others = await _context.Departments.AsNoTracking()
            .Where(d => d.IsActive && d.Id != caseDepartmentId
                        && (policy.CrossTeamMode == CrossTeamModes.AllTeams || policy.CrossTeamIds.Contains(d.Id)))
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        var seen = own.Candidates.Select(c => c.User.Id).ToHashSet();
        var extra = new List<(Department Team, Person P)>();
        var peopleByTeam = await PeopleOfAsync(others, policy, ct);   // two queries for all teams, not two per team
        foreach (var team in others)
            foreach (var p in peopleByTeam[team.Id])
                if (seen.Add(p.User.Id)) extra.Add((team, p));

        var open = await OpenCaseCountsAsync(extra.Select(e => e.P.User.Id).ToList(), ct);
        var cross = extra
            .Select(e => Judge(e.Team, policy, own.Capacity, e.P.User, e.P.IsMember, e.P.IsAssignable, e.P.CanManual, e.P.Role, open.GetValueOrDefault(e.P.User.Id)) with { IsCrossTeam = true })
            .ToList();

        return own with { Candidates = own.Candidates.Concat(cross).ToList() };
    }

    public async Task<(TeamEligibility Team, AssigneeVerdict Verdict)> CheckAsync(Guid departmentId, Guid userId, AssignmentPolicy policy, CancellationToken ct = default)
    {
        var team = await EvaluateForCaseAsync(departmentId, policy, ct);
        var verdict = team.Candidates.FirstOrDefault(c => c.User.Id == userId);
        if (verdict != null) return (team, verdict);

        // Not someone the policy offers: judged on their own merits and refused as outside the team.
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new KeyNotFoundException("Assigned user not found.");
        var openCases = (await OpenCaseCountsAsync(new List<Guid> { userId }, ct)).GetValueOrDefault(userId);
        var outsider = Judge(team.Team, policy, team.Capacity, user, isMember: false, isAssignable: false, canManual: false, role: null, openCases);
        if (outsider.IsEligible) outsider = outsider with { Reason = IneligibilityReason.NotTeamMember };
        return (team, outsider);
    }

    public async Task EnsureMayReassignAsync(Case c, Guid actorId, bool hasAssignPermission, CancellationToken ct = default)
    {
        if (hasAssignPermission || c.OwnerId == actorId) return;

        var allowed = await _context.Departments.AsNoTracking().AnyAsync(d => d.Id == c.DepartmentId && d.OwnerId == actorId, ct)
                      || await _context.TeamMembers.AsNoTracking().AnyAsync(m => m.DepartmentId == c.DepartmentId && m.UserId == actorId && m.IsActive, ct);
        if (!allowed)
            throw new AssignmentForbiddenException("Only the case owner, members or the lead of the case's team, or someone with the 'cases.assign' permission can change who holds this case.");
    }

    private sealed record Person(User User, bool IsMember, bool IsAssignable, bool CanManual, string? Role);

    /// <summary>A team's active members, plus its lead when the policy includes leads and the lead has no member row.</summary>
    private async Task<List<Person>> PeopleOfAsync(Department team, AssignmentPolicy policy, CancellationToken ct) =>
        (await PeopleOfAsync(new[] { team }, policy, ct))[team.Id];

    /// <summary>Several teams at once (one query for members, one for leads without a member row), keyed by team.</summary>
    private async Task<Dictionary<Guid, List<Person>>> PeopleOfAsync(IReadOnlyCollection<Department> teams, AssignmentPolicy policy, CancellationToken ct)
    {
        var ids = teams.Select(t => t.Id).ToList();
        var members = await _context.TeamMembers.AsNoTracking()
            .Where(m => ids.Contains(m.DepartmentId) && m.IsActive)
            .Select(m => new { m.DepartmentId, Person = new Person(m.User, true, m.IsAssignable, m.CanBeManuallyAssigned, m.MemberRole) })
            .ToListAsync(ct);
        var result = teams.ToDictionary(t => t.Id, t => members.Where(m => m.DepartmentId == t.Id).Select(m => m.Person).ToList());

        if (policy.Leads == LeadMode.Include)
        {
            var leadIds = teams.Where(t => t.OwnerId.HasValue && result[t.Id].All(p => p.User.Id != t.OwnerId!.Value)).Select(t => t.OwnerId!.Value).Distinct().ToList();
            if (leadIds.Count > 0)
            {
                var leads = await _context.Users.AsNoTracking().Where(u => leadIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
                foreach (var t in teams)
                    if (t.OwnerId.HasValue && leads.TryGetValue(t.OwnerId.Value, out var lead) && result[t.Id].All(p => p.User.Id != lead.Id))
                        result[t.Id].Add(new Person(lead, false, false, false, null));
            }
        }
        return result;
    }

    private static AssigneeVerdict Judge(Department team, AssignmentPolicy policy, int capacity, User user, bool isMember, bool isAssignable,
        bool canManual, string? role, int openCases)
    {
        var isLead = team.OwnerId == user.Id;
        var asLead = isLead && policy.Leads == LeadMode.Include;   // the lead rule decides for leads, not the member selection
        var reason =
            !team.IsActive ? IneligibilityReason.TeamInactive
            : isLead && policy.Leads == LeadMode.Exclude ? IneligibilityReason.LeadExcluded
            : !isMember && !asLead ? IneligibilityReason.NotTeamMember
            : !user.IsActive ? IneligibilityReason.UserInactive
            : policy.RequireAssignableFlag && !isAssignable ? IneligibilityReason.NotAssignable
            : policy.RequireManualSelection && !asLead && !canManual ? IneligibilityReason.NotSelected
            : policy.RequireOnline && user.Status is not (UserStatus.Available or UserStatus.Busy) ? IneligibilityReason.Unavailable
            : policy.EnforceCapacity && openCases >= capacity ? IneligibilityReason.AtCapacity
            : IneligibilityReason.None;
        return new AssigneeVerdict(user, isMember, isLead, isAssignable, role, openCases, capacity, reason, team.Id, team.Name);
    }

    private Task<Dictionary<Guid, int>> OpenCaseCountsAsync(List<Guid> userIds, CancellationToken ct) =>
        userIds.Count == 0
            ? Task.FromResult(new Dictionary<Guid, int>())
            : _context.Cases.AsNoTracking()
                .Where(c => userIds.Contains(c.OwnerId) && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
                .GroupBy(c => c.OwnerId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
}

/// <summary>The assignment settings in force for a team: its own row, else the global row. One implementation for routing and eligibility.</summary>
public static class AssignmentSettings
{
    public const string CacheKey = "routing:assignment-configs";

    public static async Task<AssignmentConfiguration> ResolveAsync(AppDbContext context, IConfigCache? cache, Guid? departmentId, CancellationToken ct = default)
    {
        // Settings change rarely and every new case reads them, so they come from the configuration cache (cleared the moment
        // any setting is saved) rather than costing a database round trip per case.
        Task<List<AssignmentConfiguration>> Load() => context.AssignmentConfigurations.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var active = cache == null ? await Load() : await cache.GetOrCreateAsync(CacheKey, Load);

        if (departmentId.HasValue)
        {
            var own = active.FirstOrDefault(c => c.DepartmentId == departmentId);
            if (own != null) return own;
        }

        return active.FirstOrDefault(c => c.DepartmentId == null)
            ?? throw new InvalidOperationException("No assignment settings are configured. Set the assignment algorithm under Cases SLA & Routing.");
    }
}

/// <summary>
/// Serialises every change of who holds a team's cases (automatic routing and manual assignment use the same key), so two
/// simultaneous assignments cannot both see an agent below capacity. Held until the surrounding transaction ends; a no-op outside one.
/// </summary>
public static class TeamAssignmentLock
{
    public static async Task AcquireAsync(AppDbContext context, Guid departmentId, CancellationToken ct = default)
    {
        if (!context.Database.IsNpgsql() || context.Database.CurrentTransaction == null) return;
        var key = departmentId.ToString();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}

/// <summary>The caller is signed in but may not change who holds this case (HTTP 403).</summary>
public sealed class AssignmentForbiddenException : Exception
{
    public AssignmentForbiddenException(string message) : base(message) { }
}

/// <summary>The chosen person cannot hold this case under the assignment policy (HTTP 422, with a reason code).</summary>
public sealed class AssignmentNotAllowedException : Exception
{
    public string ReasonCode { get; }

    public AssignmentNotAllowedException(string reasonCode, string message) : base(message) => ReasonCode = reasonCode;
}
