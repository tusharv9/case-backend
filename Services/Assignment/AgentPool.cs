namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;

public sealed record AgentCandidate(User User, int OpenCases);

public enum NoAgentReason { None, NoMembers, NoneOnline, AllAtCapacity }

/// <summary>
/// Who can take a team's next case, and why nobody can when nobody can.
/// Eligible = an ACTIVE member of the team who is marked assignable, whose Host account is active, who is Available or
/// Busy right now, and who has fewer open cases than the team's capacity. Membership (TeamMembers) is the only source —
/// a user's "home department" no longer makes them eligible, so removing someone from a team really removes them.
/// </summary>
public sealed record AgentPool(IReadOnlyList<AgentCandidate> Eligible, NoAgentReason Reason, int Members, int Online)
{
    public string Describe(string teamName) => Reason switch
    {
        NoAgentReason.NoMembers => $"'{teamName}' has no active members who receive cases.",
        NoAgentReason.NoneOnline => $"None of the {Members} agent(s) in '{teamName}' is available right now.",
        NoAgentReason.AllAtCapacity => $"All {Online} available agent(s) in '{teamName}' are at capacity.",
        _ => string.Empty,
    };
}

public interface IAgentPoolService
{
    Task<AgentPool> GetAsync(Guid departmentId, int capacity, CancellationToken ct = default);
}

public class AgentPoolService : IAgentPoolService
{
    private readonly IAssignmentEligibilityService _eligibility;

    public AgentPoolService(AppDbContext context) : this(new AssignmentEligibilityService(context)) { }

    public AgentPoolService(IAssignmentEligibilityService eligibility) => _eligibility = eligibility;

    /// <summary>The automatic-routing view of <see cref="IAssignmentEligibilityService"/>: same rules, one implementation.</summary>
    public async Task<AgentPool> GetAsync(Guid departmentId, int capacity, CancellationToken ct = default)
    {
        var team = await _eligibility.EvaluateTeamAsync(departmentId, AssignmentPolicy.Automatic, capacity, ct);

        var members = team.Candidates.Where(c => c.IsMember && c.IsAssignable && c.User.IsActive).ToList();
        if (members.Count == 0) return new AgentPool(Array.Empty<AgentCandidate>(), NoAgentReason.NoMembers, 0, 0);

        var online = members.Count(c => c.User.Status is UserStatus.Available or UserStatus.Busy);
        if (online == 0) return new AgentPool(Array.Empty<AgentCandidate>(), NoAgentReason.NoneOnline, members.Count, 0);

        var eligible = team.Eligible.Select(c => new AgentCandidate(c.User, c.OpenCases)).ToList();
        return new AgentPool(eligible, eligible.Count == 0 ? NoAgentReason.AllAtCapacity : NoAgentReason.None, members.Count, online);
    }
}
