namespace CaseManagement.Api.HostIntegration;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

public sealed record ProjectedUser(Guid LocalId, bool IsActive);

public interface IUserProjectionService
{
    /// <summary>
    /// Finds the local record for a Host user. With <paramref name="provisionIfMissing"/> a first-time
    /// Host user is created (just-in-time) and an existing one is refreshed from the claims;
    /// without it an unknown user yields null (standalone mode never invents users).
    /// </summary>
    Task<ProjectedUser?> EnsureAsync(HostPrincipal principal, bool provisionIfMissing, CancellationToken ct);
}

/// <summary>
/// Keeps the local <c>Users</c> table as a projection of Host users. The Host owns who exists; this
/// only mirrors what Case Management needs to reference a person (display data, status, team).
/// Nothing here creates credentials or manages accounts.
/// </summary>
public sealed class UserProjectionService : IUserProjectionService
{
    private readonly AppDbContext _db;
    private readonly IHostUserDirectory _directory;
    private readonly ILogger<UserProjectionService> _logger;

    public UserProjectionService(AppDbContext db, IHostUserDirectory directory, ILogger<UserProjectionService> logger)
    {
        _db = db;
        _directory = directory;
        _logger = logger;
    }

    public async Task<ProjectedUser?> EnsureAsync(HostPrincipal principal, bool provisionIfMissing, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.ExternalUserId == principal.ExternalUserId, ct);

        if (user == null)
        {
            if (!provisionIfMissing) return null;

            HostUserInfo? fromDirectory = null;
            try { fromDirectory = await _directory.GetAsync(principal.ExternalUserId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The token already proves who the user is; a directory outage must not lock them out.
                _logger.LogWarning(ex, "Host directory lookup failed for {ExternalUserId}; provisioning from token claims.", principal.ExternalUserId);
            }

            user = await CreateAsync(principal, fromDirectory, ct);
            return new ProjectedUser(user.Id, user.IsActive);
        }

        if (provisionIfMissing && Refresh(user, principal))
            await _db.SaveChangesAsync(ct);

        return new ProjectedUser(user.Id, user.IsActive);
    }

    private async Task<User> CreateAsync(HostPrincipal principal, HostUserInfo? info, CancellationToken ct)
    {
        var email = principal.Email ?? info?.Email;
        // Email is unique locally. NEVER link to an existing local user by email (that would let a
        // matching claim inherit someone else's history); fall back to a synthetic address instead.
        if (string.IsNullOrWhiteSpace(email) || await _db.Users.AnyAsync(u => u.Email == email, ct))
            email = $"{principal.ExternalUserId}@host-user.invalid";

        var user = new User
        {
            Id = Guid.NewGuid(),
            ExternalUserId = principal.ExternalUserId,
            Name = principal.Name ?? info?.Name ?? principal.ExternalUserId,
            Email = email,
            Role = principal.Roles.FirstOrDefault() ?? info?.Role ?? string.Empty,
            Status = UserStatus.Available,
            IsActive = info?.IsActive ?? true,
            DepartmentId = null,
            CreatedAt = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Provisioned local user {UserId} for Host user {ExternalUserId}.", user.Id, principal.ExternalUserId);
            return user;
        }
        catch (DbUpdateException)
        {
            // Two first requests raced; the other one won. Use its row.
            _db.Entry(user).State = EntityState.Detached;
            var existing = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.ExternalUserId == principal.ExternalUserId, ct);
            if (existing != null) return existing;
            throw;
        }
    }

    /// <summary>Applies claim values that changed. Returns true when something was modified.</summary>
    private static bool Refresh(User user, HostPrincipal principal)
    {
        var changed = false;
        if (!string.IsNullOrWhiteSpace(principal.Name) && user.Name != principal.Name) { user.Name = principal.Name; changed = true; }
        var role = principal.Roles.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(role) && user.Role != role) { user.Role = role; changed = true; }
        if (changed)
        {
            user.UpdatedAt = DateTime.UtcNow;
            user.LastSyncedAt = DateTime.UtcNow;
        }
        return changed;
    }
}

public interface IHostUserSynchronizer
{
    /// <summary>Reconciles local users with the Host directory. Returns (created, updated, deactivated).</summary>
    Task<(int Created, int Updated, int Deactivated)> SyncAllAsync(CancellationToken ct);
}

/// <summary>Bulk refresh from the Host directory: create missing, update changed, optionally deactivate absent.</summary>
public sealed class HostUserSynchronizer : IHostUserSynchronizer
{
    private readonly AppDbContext _db;
    private readonly IHostUserDirectory _directory;
    private readonly Microsoft.Extensions.Options.IOptions<HostIntegrationOptions> _options;

    public HostUserSynchronizer(AppDbContext db, IHostUserDirectory directory, Microsoft.Extensions.Options.IOptions<HostIntegrationOptions> options)
    {
        _db = db;
        _directory = directory;
        _options = options;
    }

    public async Task<(int Created, int Updated, int Deactivated)> SyncAllAsync(CancellationToken ct)
    {
        var listed = await _directory.ListAsync(ct);
        var existing = await _db.Users.Where(u => u.ExternalUserId != null).ToDictionaryAsync(u => u.ExternalUserId!, ct);
        var takenEmails = (await _db.Users.Select(u => u.Email).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0, deactivated = 0;
        var now = DateTime.UtcNow;

        foreach (var info in listed)
        {
            if (existing.TryGetValue(info.ExternalUserId, out var user))
            {
                var changed = false;
                if (user.Name != info.Name) { user.Name = info.Name; changed = true; }
                if (!string.IsNullOrWhiteSpace(info.Role) && user.Role != info.Role) { user.Role = info.Role!; changed = true; }
                if (user.IsActive != info.IsActive) { user.IsActive = info.IsActive; changed = true; }
                user.LastSyncedAt = now;
                if (changed) { user.UpdatedAt = now; updated++; }
                continue;
            }

            var email = info.Email;
            if (string.IsNullOrWhiteSpace(email) || takenEmails.Contains(email))
                email = $"{info.ExternalUserId}@host-user.invalid";
            takenEmails.Add(email);

            _db.Users.Add(new Models.User
            {
                Id = Guid.NewGuid(),
                ExternalUserId = info.ExternalUserId,
                Name = info.Name,
                Email = email,
                Role = info.Role ?? string.Empty,
                IsActive = info.IsActive,
                CreatedAt = now,
                LastSyncedAt = now
            });
            created++;
        }

        if (_options.Value.Directory.DeactivateMissing)
        {
            var listedIds = listed.Select(l => l.ExternalUserId).ToHashSet();
            foreach (var user in existing.Values.Where(u => u.IsActive && !listedIds.Contains(u.ExternalUserId!)))
            {
                user.IsActive = false;
                user.UpdatedAt = now;
                deactivated++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return (created, updated, deactivated);
    }
}

/// <summary>Periodically runs <see cref="IHostUserSynchronizer"/>; only registered when a directory and interval are configured.</summary>
public sealed class HostUserSyncWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly DatabaseInitializationState _dbState;
    private readonly Microsoft.Extensions.Options.IOptions<HostIntegrationOptions> _options;
    private readonly ILogger<HostUserSyncWorker> _logger;

    public HostUserSyncWorker(IServiceScopeFactory scopes, DatabaseInitializationState dbState,
        Microsoft.Extensions.Options.IOptions<HostIntegrationOptions> options, ILogger<HostUserSyncWorker> logger)
    {
        _scopes = scopes;
        _dbState = dbState;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _dbState.WhenReady.WaitAsync(stoppingToken);
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.Value.Directory.SyncIntervalMinutes));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var (created, updated, deactivated) = await scope.ServiceProvider
                        .GetRequiredService<IHostUserSynchronizer>().SyncAllAsync(stoppingToken);
                    _logger.LogInformation("Host user sync: {Created} created, {Updated} updated, {Deactivated} deactivated.", created, updated, deactivated);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Host user sync failed; will retry in {Interval}.", interval);
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}
