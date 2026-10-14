namespace CaseManagement.Api.Services.Strategies;

using CaseManagement.Api.Models;

public class LeastOccupancyAssignmentStrategy : IAssignmentStrategy
{
    public string AlgorithmName => "LeastOccupancy";

    public Task<AssignmentChoice?> SelectAsync(AssignmentRequest request, CancellationToken ct = default)
    {
        // Fewest open cases first; an Available agent before a Busy one; then a stable order.
        var best = request.Eligible
            .OrderBy(c => c.OpenCases)
            .ThenByDescending(c => c.User.Status == UserStatus.Available)
            .ThenBy(c => c.User.Id)
            .FirstOrDefault();

        return Task.FromResult<AssignmentChoice?>(best == null ? null : new AssignmentChoice(best.User));
    }
}
