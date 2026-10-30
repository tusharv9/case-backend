using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// Gives a test database what the CaseDepartmentsAndTeamForm migration gives a real one: every team serves a business department
/// of its own name (as its default team), and sub-categories and cases belong to that department. Tests that seed teams and
/// sub-categories the old way call this once after seeding, so case creation and priority lookup work by department.
/// </summary>
public static class TestDepartments
{
    public static async Task LinkTeamsAsync(AppDbContext ctx)
    {
        var teams = await ctx.Departments.ToListAsync();
        var linked = (await ctx.TeamDepartments.Select(l => l.TeamId).ToListAsync()).ToHashSet();
        foreach (var team in teams.Where(t => !linked.Contains(t.Id)))
        {
            var department = new OrgDepartment
            {
                Id = Guid.NewGuid(), Name = team.Name, Code = string.IsNullOrWhiteSpace(team.Code) ? team.Name : team.Code,
                IsActive = true, CreatedAt = DateTime.UtcNow,
            };
            ctx.OrgDepartments.Add(department);
            ctx.TeamDepartments.Add(new TeamDepartment { TeamId = team.Id, OrgDepartmentId = department.Id, IsDefault = true });
        }
        await ctx.SaveChangesAsync();

        var departmentOf = (await ctx.TeamDepartments.ToListAsync())
            .GroupBy(l => l.TeamId).ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.IsDefault).First().OrgDepartmentId);
        foreach (var s in await ctx.DepartmentSubCategories.Where(s => s.OrgDepartmentId == null && s.DepartmentId != null).ToListAsync())
            s.OrgDepartmentId = departmentOf.GetValueOrDefault(s.DepartmentId!.Value);
        foreach (var c in await ctx.Cases.Where(c => c.OrgDepartmentId == null).ToListAsync())
            c.OrgDepartmentId = departmentOf.TryGetValue(c.DepartmentId, out var d) ? d : null;
        await ctx.SaveChangesAsync();
    }
}
