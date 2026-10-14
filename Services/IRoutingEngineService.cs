namespace CaseManagement.Api.Services;

using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;

public interface IRoutingEngineService
{
    Task<IEnumerable<RoutingRuleDto>> GetRulesAsync(CancellationToken ct = default);
    Task<RoutingRuleDto?> GetRuleByIdAsync(Guid id, CancellationToken ct = default);
    Task<RoutingRuleDto> CreateRuleAsync(CreateRoutingRuleDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<RoutingRuleDto> UpdateRuleAsync(Guid id, UpdateRoutingRuleDto dto, Guid actingUserId, CancellationToken ct = default);
    Task<RoutingRuleDto> ToggleRuleAsync(Guid id, Guid actingUserId, CancellationToken ct = default);
    Task DeleteRuleAsync(Guid id, Guid actingUserId, CancellationToken ct = default);
    Task ReorderRulesAsync(ReorderRulesDto dto, Guid actingUserId, CancellationToken ct = default);
    
    /// <summary>The settings in force for a team (its own, or the global default), or the global default when no team is given.</summary>
    Task<AssignmentConfigDto> GetAssignmentConfigAsync(Guid? departmentId = null, CancellationToken ct = default);
    Task<AssignmentConfigDto> UpdateAssignmentConfigAsync(UpdateAssignmentConfigDto dto, Guid actingUserId, Guid? departmentId = null, CancellationToken ct = default);
    /// <summary>Removes a team's own settings so it follows the global default again.</summary>
    Task ClearTeamAssignmentConfigAsync(Guid departmentId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>What routing rules can look at, and the valid values for each.</summary>
    Task<RoutingVocabularyDto> GetVocabularyAsync(CancellationToken ct = default);

    Task<RoutingDecisionResult> RouteAndAssignCaseAsync(Case newCase, Customer? customer, CancellationToken ct = default);

    /// <summary>
    /// Assigns <paramref name="c"/> within one team with that team's algorithm and eligibility rules (stage 2 of routing, without the
    /// rules). <paramref name="via"/> describes the trigger for the routing log (e.g. "a transfer"). Must run inside the transaction that
    /// stores the case. <see cref="RoutingDecisionResult.AssignedUserId"/> is null when nobody — not even a lead — can hold it.
    /// </summary>
    Task<RoutingDecisionResult> AssignWithinTeamAsync(Case c, Customer? customer, Guid departmentId, string via, CancellationToken ct = default);
}
