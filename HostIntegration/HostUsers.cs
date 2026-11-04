namespace CaseManagement.Api.HostIntegration;

using System.Net;
using System.Text.Json;
using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Where AuthService is, bound from <c>AuthService:BaseUrl</c> (<c>AuthService__BaseUrl</c> in .env), as in every OmniConnect service.</summary>
public class AuthServiceOptions
{
    public const string SectionName = "AuthService";
    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// This service's own key for AuthService's internal routes (<c>Internal__Services__CaseManagement__ApiKey</c> there).
    /// With it, permissions are asked of AuthService on every request (<see cref="HostCapabilitiesClient"/>).
    /// </summary>
    public string? InternalApiKey { get; set; }
}

/// <summary>Why the Host's user directory could not be read, worded for the person using the Teams page.</summary>
public sealed class HostUsersException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A Host user, as the Host's own directory lists them.</summary>
public sealed record HostDirectoryUser(string HostUserId, string Name, string Email, string? RoleName, bool IsActive);

/// <summary>
/// The OmniConnect Host's user directory (AuthService <c>GET /api/users</c>), read with the caller's own token.
/// </summary>
/// <remarks>
/// <para>The Host owns who exists. AuthService lets a person list users when they may see Settings → Users
/// (<c>host.settings.users:View</c>) or are an administrator, and refuses everyone else — so the Teams page can offer
/// every Host user, including people who have never opened Case Management, exactly to those the Host allows, with no
/// change to AuthService and no service account that could read the directory on anyone's behalf.</para>
/// </remarks>
public sealed class HostUserDirectoryClient(HttpClient http, IHttpContextAccessor httpContext, IOptions<AuthServiceOptions> options, ILogger<HostUserDirectoryClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Page(List<Item> Items, int Total);
    private sealed record Item(Guid Id, string Name, string Email, string? RoleName, bool IsActive);

    /// <summary>One page of active-or-not Host users matching <paramref name="search"/> (name, email…), and the total.</summary>
    public async Task<(IReadOnlyList<HostDirectoryUser> Users, int Total)> SearchAsync(string? search, bool activeOnly, int page, int pageSize, CancellationToken ct)
    {
        var query = $"api/users?page={Math.Max(page, 1)}&pageSize={Math.Clamp(pageSize, 1, 100)}";
        if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (activeOnly) query += "&isActive=true";

        var body = await GetAsync(query, ct) ?? throw new HostUsersException(503, "The Host's user directory did not answer.");
        var result = JsonSerializer.Deserialize<Page>(body, Json) ?? new Page(new(), 0);
        return (result.Items.Select(Map).ToList(), result.Total);
    }

    /// <summary>One Host user, or null when the Host has no such user.</summary>
    public async Task<HostDirectoryUser?> GetAsync(Guid hostUserId, CancellationToken ct)
    {
        var body = await GetAsync($"api/users/{hostUserId}", ct);
        return body == null ? null : Map(JsonSerializer.Deserialize<Item>(body, Json)!);
    }

    private static HostDirectoryUser Map(Item i) => new(i.Id.ToString(), i.Name, i.Email, i.RoleName, i.IsActive);

    private async Task<string?> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var baseUrl = options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new HostUsersException(503, "The Host's user directory is not connected (AuthService:BaseUrl is not set).");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), pathAndQuery));
        var authorization = httpContext.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(authorization)) request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds)));
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, timeout.Token); }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("The Host user directory could not be reached: {Error}", ex.Message);
            throw new HostUsersException(503, "The Host's user directory is unavailable. Please try again shortly.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("The Host user directory answered {Status} for {Path}.", (int)response.StatusCode, request.RequestUri!.AbsolutePath);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => null,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => throw new HostUsersException(403,
                    "You need access to the Host's user list (Settings → Users: View) to choose people for teams. Ask your administrator."),
                _ => throw new HostUsersException(503, "The Host's user directory is unavailable. Please try again shortly."),
            };
        }
    }
}

/// <summary>A Host user for the Teams page: who they are in the Host, and what Case Management knows of them.</summary>
public sealed class HostUserDto
{
    public string HostUserId { get; set; } = string.Empty;
    /// <summary>Case Management's reference to them, once they have signed in or been added to a team; null before.</summary>
    public Guid? LocalUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? RoleName { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Every team they are an active member of — a person can belong to several.</summary>
    public List<HostUserTeamDto> Teams { get; set; } = new();
}

public sealed record HostUserTeamDto(Guid Id, string Name, bool IsLead);

public sealed class HostUserPageDto
{
    public List<HostUserDto> Items { get; set; } = new();
    public int Total { get; set; }
}

public sealed class LinkHostUsersDto
{
    public List<Guid> HostUserIds { get; set; } = new();
}

public sealed record LinkedHostUserDto(string HostUserId, Guid LocalUserId);

/// <summary>Host users for team membership: listing them, and keeping a reference to those added to a team.</summary>
public sealed class HostUserService(HostUserDirectoryClient directory, AppDbContext context, IUserProjectionService projection)
{
    public async Task<HostUserPageDto> SearchAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var (users, total) = await directory.SearchAsync(search, activeOnly: true, page, pageSize, ct);
        var ids = users.Select(u => u.HostUserId).ToList();
        var local = await context.Users.AsNoTracking().Where(u => u.ExternalUserId != null && ids.Contains(u.ExternalUserId))
            .Select(u => new { u.Id, u.ExternalUserId }).ToListAsync(ct);
        var localIds = local.Select(l => l.Id).ToList();
        var teams = await context.TeamMembers.AsNoTracking().Where(m => m.IsActive && localIds.Contains(m.UserId))
            .Select(m => new { m.UserId, m.TeamId, m.Team.Name, IsLead = m.Team.OwnerId == m.UserId }).ToListAsync(ct);

        return new HostUserPageDto
        {
            Total = total,
            Items = users.Select(u =>
            {
                var localId = local.FirstOrDefault(l => l.ExternalUserId == u.HostUserId)?.Id;
                return new HostUserDto
                {
                    HostUserId = u.HostUserId, LocalUserId = localId, Name = u.Name, Email = u.Email, RoleName = u.RoleName, IsActive = u.IsActive,
                    Teams = teams.Where(t => t.UserId == localId).OrderBy(t => t.Name).Select(t => new HostUserTeamDto(t.TeamId, t.Name, t.IsLead)).ToList(),
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// Case Management's reference for each Host user about to join a team, created when missing. Each one is read
    /// from the Host first: nothing the browser sends about a person is trusted, and a deactivated user is refused.
    /// The reference holds only what screens need to name them (Host id, name, email) — the Host stays their owner.
    /// </summary>
    public async Task<IReadOnlyList<LinkedHostUserDto>> LinkAsync(IEnumerable<Guid> hostUserIds, CancellationToken ct)
    {
        var result = new List<LinkedHostUserDto>();
        foreach (var id in hostUserIds.Distinct().Take(100))
        {
            var user = await directory.GetAsync(id, ct)
                ?? throw new KeyNotFoundException("One of the chosen people no longer exists in the Host. Reload and choose again.");
            if (!user.IsActive) throw new InvalidOperationException($"{user.Name} is deactivated in the Host and cannot join a team.");

            var principal = new HostPrincipal(user.HostUserId, user.Name, user.Email, Array.Empty<string>(), Array.Empty<string>());
            var projected = await projection.EnsureAsync(principal, provisionIfMissing: true, ct)
                ?? throw new InvalidOperationException($"{user.Name} could not be added.");
            result.Add(new LinkedHostUserDto(user.HostUserId, projected.LocalId));
        }
        return result;
    }
}
