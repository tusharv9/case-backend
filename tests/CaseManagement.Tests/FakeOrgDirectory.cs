using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace CaseManagement.Tests;

/// <summary>
/// The Host's roles for a test: each user's <see cref="User.Role"/> is the Host role they hold, <paramref name="caseRoles"/>
/// are the roles granted Case Management access, and <paramref name="supervisorRoles"/> the roles granted cases:Supervise.
/// </summary>
public sealed class FakeOrgDirectory(AppDbContext db, IEnumerable<string>? caseRoles = null, IEnumerable<string>? supervisorRoles = null,
    IEnumerable<string>? administratorRoles = null) : IOrgDirectory
{
    private readonly List<string> _caseRoles = caseRoles?.ToList() ?? new();
    private readonly HashSet<string> _supervisorRoles = new(supervisorRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> CaseRolesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_caseRoles);

    public async Task<IReadOnlyList<User>> UsersInRoleAsync(string role, CancellationToken ct = default)
    {
        var lowered = role.Trim().ToLower();
        return await db.Users.AsNoTracking().Where(u => u.IsActive && u.Role.ToLower() == lowered).OrderBy(u => u.Name).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<User>> SupervisorsAsync(CancellationToken ct = default) =>
        (await db.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(ct)).Where(u => _supervisorRoles.Contains(u.Role)).ToList();

    private readonly HashSet<string> _administratorRoles = new(administratorRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<User>> AdministratorsAsync(CancellationToken ct = default) =>
        (await db.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync(ct)).Where(u => _administratorRoles.Contains(u.Role)).ToList();
}
