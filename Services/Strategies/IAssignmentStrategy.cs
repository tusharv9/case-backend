namespace CaseManagement.Api.Services.Strategies;

using CaseManagement.Api.Models;

public sealed record AssignmentRequest(Guid DepartmentId, Case Case, Customer? Customer, IReadOnlyList<AgentCandidate> Eligible, int Capacity);

/// <summary>The chosen agent, plus anything worth telling the person reading the routing log.</summary>
public sealed record AssignmentChoice(User User, string? Note = null);

/// <summary>
/// One assignment algorithm. The pool of eligible agents (membership, availability and capacity) is decided before the
/// strategy runs, so EVERY algorithm respects capacity. A strategy only chooses among them and must not save anything:
/// state it changes (the round-robin pointer) is saved with the case, in the same transaction.
/// </summary>
public interface IAssignmentStrategy
{
    string AlgorithmName { get; }
    Task<AssignmentChoice?> SelectAsync(AssignmentRequest request, CancellationToken ct = default);
}
