namespace CaseManagement.Api.HostIntegration;

using System.Net.Http.Json;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
/// The Host's roles and who holds them, as Case Management needs them: the roles granted access to Case Management (the
/// escalation matrix offers only these), the people holding one of them (escalation recipients) and the Supervisors
/// (roles granted <c>cases:Supervise</c>).
/// </summary>
/// <remarks>
/// <para><b>The Host is the source of truth.</b> Nothing here keeps its own list of roles: every answer is read from
/// AuthService service-to-service (<c>internal/roles…</c>, <c>internal/users</c>) behind this service's own internal key,
/// so a role whose Case Management access is revoked disappears on the next read. Answers are kept for a minute, which
/// is what keeps the SLA monitor from asking AuthService once per case.</para>
/// <para><b>People are returned as local users.</b> Someone who holds the role but has never opened Case Management is
/// given their local reference (the same projection a first sign-in creates), so a case can be escalated to them.</para>
/// <para><b>Without AuthService</b> (standalone demo) the local users table is the directory, as everywhere else.</para>
/// </remarks>
public interface IOrgDirectory
{
    /// <summary>Roles that may use Case Management (administrator roles included), by name.</summary>
    Task<IReadOnlyList<string>> CaseRolesAsync(CancellationToken ct = default);

    /// <summary>Active people holding <paramref name="role"/>, as local users (role name exact, any case).</summary>
    Task<IReadOnlyList<User>> UsersInRoleAsync(string role, CancellationToken ct = default);

    /// <summary>Active people whose role is granted <c>cases:Supervise</c>.</summary>
    Task<IReadOnlyList<User>> SupervisorsAsync(CancellationToken ct = default);
}

public sealed class HostOrgDirectory(
    HttpClient http,
    IOptions<AuthServiceOptions> authOptions,
    IOptions<HostIntegrationOptions> hostOptions,
    IMemoryCache cache,
    AppDbContext db,
    IUserProjectionService projection,
    IPermissionProvider permissions,
    ILogger<HostOrgDirectory> logger) : IOrgDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private sealed record RoleRow(Guid Id, string Name);
    private sealed record UserRow(Guid Id, string Name, string Email);

    private bool HostConnected =>
        !string.IsNullOrWhiteSpace(authOptions.Value.BaseUrl) && !string.IsNullOrWhiteSpace(authOptions.Value.InternalApiKey);

    private string AppFeature => $"remote.{hostOptions.Value.AppKey}";

    public async Task<IReadOnlyList<string>> CaseRolesAsync(CancellationToken ct = default)
    {
        if (!HostConnected)
        {
            return await db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.ExternalUserId != null && u.Role != "")
                .Select(u => u.Role).Distinct().OrderBy(r => r).ToListAsync(ct);
        }

        var roles = await GetCachedAsync<List<RoleRow>>($"internal/roles/with-app-access?app={Uri.EscapeDataString(AppFeature)}&includeAdministrators=true", ct);
        return roles.Select(r => r.Name).ToList();
    }

    public async Task<IReadOnlyList<User>> UsersInRoleAsync(string role, CancellationToken ct = default)
    {
        var name = role?.Trim() ?? string.Empty;
        if (name.Length == 0) return Array.Empty<User>();

        if (!HostConnected)
        {
            var lowered = name.ToLower();
            return await db.Users.AsNoTracking()
                .Where(u => u.IsActive && u.ExternalUserId != null && u.Role.ToLower() == lowered)
                .OrderBy(u => u.Name).ToListAsync(ct);
        }

        var people = await GetCachedAsync<List<UserRow>>($"internal/users?role={Uri.EscapeDataString(name)}", ct);
        return await LocalUsersAsync(people, name, ct);
    }

    public async Task<IReadOnlyList<User>> SupervisorsAsync(CancellationToken ct = default)
    {
        if (!HostConnected)
        {
            // Standalone: the configured role map decides (null = every demo user holds the same permissions).
            var roles = permissions.RolesGranting(Permissions.CasesSupervise);
            var query = db.Users.AsNoTracking().Where(u => u.IsActive && u.ExternalUserId != null);
            var all = await query.OrderBy(u => u.Name).ToListAsync(ct);
            return roles == null ? all : all.Where(u => roles.Contains(u.Role)).ToList();
        }

        var supervisorRoles = await GetCachedAsync<List<RoleRow>>(
            $"internal/roles?feature={Uri.EscapeDataString(AppFeature + ".cases")}&capability=Supervise", ct);
        var result = new List<User>();
        foreach (var role in supervisorRoles)
        {
            var people = await GetCachedAsync<List<UserRow>>($"internal/users?roleId={role.Id}", ct);
            result.AddRange(await LocalUsersAsync(people, role.Name, ct));
        }
        return result.DistinctBy(u => u.Id).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The local reference of each Host user, created when missing, with the Host role name kept on it.</summary>
    private async Task<IReadOnlyList<User>> LocalUsersAsync(IReadOnlyList<UserRow> people, string roleName, CancellationToken ct)
    {
        var ids = people.Select(p => p.Id.ToString()).ToList();
        var known = await db.Users.Where(u => u.ExternalUserId != null && ids.Contains(u.ExternalUserId)).ToListAsync(ct);

        var changed = false;
        foreach (var person in people)
        {
            var external = person.Id.ToString();
            var local = known.FirstOrDefault(u => u.ExternalUserId == external);
            if (local == null)
            {
                var principal = new HostPrincipal(external, person.Name, person.Email, [roleName], []);
                var projected = await projection.EnsureAsync(principal, provisionIfMissing: true, ct);
                if (projected != null && await db.Users.FindAsync([projected.LocalId], ct) is { } created) known.Add(created);
            }
            else if (!string.Equals(local.Role, roleName, StringComparison.Ordinal))
            {
                local.Role = roleName;
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);

        return known.Where(u => u.IsActive).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<T> GetCachedAsync<T>(string pathAndQuery, CancellationToken ct) where T : class, new()
    {
        if (cache.TryGetValue("org-directory:" + pathAndQuery, out T? hit) && hit != null) return hit;

        var options = authOptions.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl!.TrimEnd('/')}/{pathAndQuery}");
        request.Headers.Add("X-Internal-Api-Key", options.InternalApiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));

        T? body;
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("AuthService answered {Status} for {Path}.", (int)response.StatusCode, request.RequestUri!.AbsolutePath);
                throw Unavailable();
            }
            body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: deadline.Token);
        }
        catch (HostUsersException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read {Path} from AuthService.", pathAndQuery);
            throw Unavailable();
        }

        body ??= new T();
        cache.Set("org-directory:" + pathAndQuery, body, CacheFor);
        return body;
    }

    private static HostUsersException Unavailable() =>
        new(503, "Case Management could not read roles and people from OmniConnect. Try again in a moment.");
}
