namespace CaseManagement.Api.Services.Strategies;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public class SkillBasedAssignmentStrategy : IAssignmentStrategy
{
    private readonly AppDbContext _context;
    private readonly ISkillService _skills;

    public SkillBasedAssignmentStrategy(AppDbContext context, ISkillService skills)
    {
        _context = context;
        _skills = skills;
    }

    public string AlgorithmName => "SkillBased";

    public async Task<AssignmentChoice?> SelectAsync(AssignmentRequest request, CancellationToken ct = default)
    {
        if (request.Eligible.Count == 0) return null;

        // Which skills does THIS case need? Decided by the configured skill rules — nothing is guessed from the case's text in code.
        var required = await _skills.RequiredSkillsAsync(request.Case, request.Customer, ct);

        var ids = request.Eligible.Select(c => c.User.Id).ToList();
        var skills = await _context.AgentSkills.AsNoTracking().Where(s => ids.Contains(s.UserId)).ToListAsync(ct);
        var byUser = skills.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList());

        var scored = request.Eligible.Select(c =>
        {
            var score = 0;
            if (byUser.TryGetValue(c.User.Id, out var mine))
                score = required.Sum(req => mine.Where(s => string.Equals(s.SkillName, req, StringComparison.OrdinalIgnoreCase)).Sum(s => s.ProficiencyLevel * 10));
            return new { Candidate = c, Score = score };
        }).ToList();

        var top = scored.Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        if (top.Count > 0)
        {
            var best = top[0].Score;
            var chosen = top.Where(x => x.Score == best).Select(x => x.Candidate)
                .OrderBy(c => c.OpenCases).ThenByDescending(c => c.User.Status == UserStatus.Available).ThenBy(c => c.User.Id).First();
            return new AssignmentChoice(chosen.User, $"matched skills: {string.Join(", ", required)}");
        }

        // No required skill, or nobody holds one: say so, and share the work by load rather than pretending to match.
        var fallback = request.Eligible.OrderBy(c => c.OpenCases).ThenByDescending(c => c.User.Status == UserStatus.Available).ThenBy(c => c.User.Id).First();
        return new AssignmentChoice(fallback.User, required.Count == 0
            ? "no skills required by the configured skill rules; chosen by lowest load"
            : $"nobody available holds the required skills ({string.Join(", ", required)}); chosen by lowest load");
    }
}
