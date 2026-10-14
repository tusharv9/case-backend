namespace CaseManagement.Api.Services.Strategies;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public class RoundRobinAssignmentStrategy : IAssignmentStrategy
{
    private readonly AppDbContext _context;

    public RoundRobinAssignmentStrategy(AppDbContext context) => _context = context;

    public string AlgorithmName => "RoundRobin";

    public async Task<AssignmentChoice?> SelectAsync(AssignmentRequest request, CancellationToken ct = default)
    {
        var candidates = request.Eligible.Select(c => c.User).OrderBy(u => u.Id).ToList();
        if (candidates.Count == 0) return null;

        var pointer = await _context.TeamAssignmentPointers.FirstOrDefaultAsync(p => p.DepartmentId == request.DepartmentId, ct);

        // The next eligible person after whoever got the previous case (wrapping around), by stable id order.
        User selected;
        if (pointer == null)
        {
            selected = candidates[0];
            _context.TeamAssignmentPointers.Add(new TeamAssignmentPointer
            {
                DepartmentId = request.DepartmentId,
                LastAssignedUserId = selected.Id,
                LastAssignedAt = DateTime.UtcNow,
            });
        }
        else
        {
            selected = candidates.FirstOrDefault(u => u.Id.CompareTo(pointer.LastAssignedUserId) > 0) ?? candidates[0];
            pointer.LastAssignedUserId = selected.Id;
            pointer.LastAssignedAt = DateTime.UtcNow;
        }

        return new AssignmentChoice(selected);   // the pointer is saved together with the case
    }
}
