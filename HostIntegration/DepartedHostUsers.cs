namespace CaseManagement.Api.HostIntegration;

using System.Net.Http.Json;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Asks AuthService where each Host user stands (<c>POST internal/users/status</c>, behind this service's internal key).</summary>
public interface IHostUserStatusClient
{
    /// <summary>Host user id → <c>Active</c>, <c>Inactive</c> or <c>Deleted</c>. An id the Host does not know is absent. Null when AuthService is not configured.</summary>
    Task<IReadOnlyDictionary<string, string>?> StatusesAsync(IReadOnlyCollection<string> hostUserIds, CancellationToken ct);
}

public sealed class HostUserStatusClient(HttpClient http, IOptions<AuthServiceOptions> options) : IHostUserStatusClient
{
    private sealed record Row(Guid Id, string Status);

    public async Task<IReadOnlyDictionary<string, string>?> StatusesAsync(IReadOnlyCollection<string> hostUserIds, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BaseUrl) || string.IsNullOrWhiteSpace(o.InternalApiKey)) return null;

        var ids = hostUserIds.Select(i => Guid.TryParse(i, out var g) ? g : (Guid?)null).Where(g => g.HasValue).Select(g => g!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<string, string>();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl!.TrimEnd('/')}/internal/users/status") { Content = JsonContent.Create(ids) };
        request.Headers.Add("X-Internal-Api-Key", o.InternalApiKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        using var response = await http.SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<List<Row>>(cancellationToken: deadline.Token) ?? new();
        return rows.ToDictionary(r => r.Id.ToString(), r => r.Status, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// What happens in Case Management when a person is deleted in the Host: they leave every team (a team they led is left
/// without a lead), they are marked inactive, and each open case they own is handed to the lead of the case's team — or,
/// when the team has no lead, to a Supervisor, and when there is none, to an OmniConnect administrator — to be given to an agent. The team lead and every Supervisor are told
/// which cases need assigning. Their history (events, comments, closed cases) is kept.
/// </summary>
/// <remarks>Only a Host answer of <c>Deleted</c> triggers this. A person the Host does not know at all (a row from another
/// Host database in development) is left alone: "unknown" is never read as "deleted".</remarks>
public sealed class DepartedUserService(AppDbContext db, IOrgDirectory org, INotificationService notifications, ILogger<DepartedUserService> logger)
{
    public static bool IsOpen(CaseStatus s) => s != CaseStatus.Resolved && s != CaseStatus.Closed && s != CaseStatus.Cancelled;

    /// <summary>Releases everything held by the given local users. Returns how many open cases were handed on.</summary>
    public async Task<int> ReleaseAsync(IReadOnlyCollection<Guid> localUserIds, CancellationToken ct)
    {
        if (localUserIds.Count == 0) return 0;
        var departed = await db.Users.Where(u => localUserIds.Contains(u.Id)).ToListAsync(ct);
        if (departed.Count == 0) return 0;
        var departedIds = departed.Select(u => u.Id).ToHashSet();
        var now = DateTime.UtcNow;

        IReadOnlyList<User> supervisors;
        try { supervisors = await org.SupervisorsAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Supervisors could not be read; only team leads will be told."); supervisors = Array.Empty<User>(); }
        var activeSupervisors = supervisors.Where(s => !departedIds.Contains(s.Id)).ToList();

        var memberships = await db.TeamMembers.Where(m => departedIds.Contains(m.UserId) && m.IsActive).ToListAsync(ct);
        foreach (var m in memberships) { m.IsActive = false; m.UpdatedAt = now; }

        var ledTeams = await db.Teams.Where(t => t.OwnerId != null && departedIds.Contains(t.OwnerId.Value)).ToListAsync(ct);
        foreach (var team in ledTeams)
        {
            var leadName = departed.First(u => u.Id == team.OwnerId).Name;
            team.OwnerId = null;
            team.UpdatedAt = now;
            db.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(), EventType = EventType.Other, CreatedAt = now, Module = "Teams", EntityName = team.Name, ActionType = "REMOVE_LEAD",
                Message = $"{leadName} was deleted in OmniConnect and is no longer the lead of '{team.Name}'.", OldValue = leadName, NewValue = null,
            });
        }

        var cases = await db.Cases.Where(c => departedIds.Contains(c.OwnerId)).ToListAsync(ct);
        var open = cases.Where(c => IsOpen(c.Status)).ToList();
        var teamIds = open.Select(c => c.TeamId).Distinct().ToList();
        var leads = await db.Teams.AsNoTracking().Where(t => teamIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.OwnerId, ct);
        var activeUsers = await db.Users.AsNoTracking().Where(u => u.IsActive && !departedIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        var active = activeUsers.ToHashSet();

        // Last resort, read only when needed: OmniConnect administrators, so a case is never left with someone who can no
        // longer open it just because its team has no lead and nobody holds the Supervisor permission.
        IReadOnlyList<User>? admins = null;
        async Task<IReadOnlyList<User>> Administrators()
        {
            if (admins != null) return admins;
            try { admins = (await org.AdministratorsAsync(ct)).Where(a => !departedIds.Contains(a.Id) && active.Contains(a.Id)).ToList(); }
            catch (Exception ex) { logger.LogWarning(ex, "Administrators could not be read."); admins = Array.Empty<User>(); }
            return admins;
        }

        var toNotify = new List<(Guid Recipient, Case Case, string FormerOwner)>();
        foreach (var c in open)
        {
            var former = departed.First(u => u.Id == c.OwnerId).Name;
            var lead = leads.GetValueOrDefault(c.TeamId);
            var heldBy = "the team lead";
            Guid? holder = lead.HasValue && active.Contains(lead.Value) ? lead : null;
            if (holder == null && activeSupervisors.Count > 0) { holder = activeSupervisors[0].Id; heldBy = "a Supervisor"; }
            var adminFallback = false;
            if (holder == null && (await Administrators()).FirstOrDefault() is { } admin) { holder = admin.Id; heldBy = "an administrator"; adminFallback = true; }
            if (holder == null)
            {
                logger.LogWarning("Case {Case} was owned by deleted user {User}, but there is no team lead, Supervisor or administrator to hold it.", c.CaseNumber, former);
                continue;
            }

            c.OwnerId = holder.Value;
            c.UpdatedAt = now;
            db.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(), CaseId = c.Id, EventType = EventType.Assign, CreatedAt = now,
                Message = $"{former} was deleted in OmniConnect. The case is no longer assigned to them and is held by {heldBy} until it is assigned to an agent.",
                OldValue = former, NewValue = "Needs an agent",
            });

            var recipients = new HashSet<Guid> { holder.Value };
            if (lead.HasValue && active.Contains(lead.Value)) recipients.Add(lead.Value);
            foreach (var s in activeSupervisors) recipients.Add(s.Id);
            if (adminFallback) foreach (var a in await Administrators()) recipients.Add(a.Id);   // every administrator is told
            foreach (var r in recipients) toNotify.Add((r, c, former));
        }

        foreach (var u in departed)
        {
            u.IsActive = false;
            u.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        foreach (var (recipient, c, former) in toNotify)
        {
            try
            {
                await notifications.CreateNotificationAsync(recipient, "CASE_UNASSIGNED", "Case needs an agent",
                    $"{former} was deleted in OmniConnect. Case {c.CaseNumber} ({c.Title}) is no longer assigned to them: assign it to someone.",
                    c.Id, c.CaseNumber, "High", 0, ct, $"departed:{c.Id}:{former}");
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not notify {Recipient} about case {Case}.", recipient, c.CaseNumber); }
        }

        logger.LogInformation("Released {Users} deleted Host user(s): {Cases} open case(s) handed to team leads, {Teams} team(s) left without a lead.",
            departed.Count, open.Count, ledTeams.Count);
        return open.Count;
    }
}

/// <summary>Finds the people deleted in the Host since the last look and releases what they held (<see cref="DepartedUserService"/>).</summary>
public sealed class DepartedUserMonitor(AppDbContext db, IHostUserStatusClient statuses, DepartedUserService release)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        // Everyone who could still hold work: active, or inactive but still owning open cases or sitting on a team.
        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.ExternalUserId != null && (u.IsActive
                || db.Cases.Any(c => c.OwnerId == u.Id && c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
                || db.TeamMembers.Any(m => m.UserId == u.Id && m.IsActive)
                || db.Teams.Any(t => t.OwnerId == u.Id)))
            .Select(u => new { u.Id, u.ExternalUserId })
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var answer = await statuses.StatusesAsync(candidates.Select(c => c.ExternalUserId!).ToList(), ct);
        if (answer == null) return 0;

        var deleted = candidates.Where(c => answer.TryGetValue(c.ExternalUserId!, out var s) && string.Equals(s, "Deleted", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Id).ToList();
        return deleted.Count == 0 ? 0 : await release.ReleaseAsync(deleted, ct);
    }
}

/// <summary>Runs <see cref="DepartedUserMonitor"/> every <c>AuthService:DepartedUserCheckSeconds</c> (default 60) while AuthService is configured.</summary>
public sealed class DepartedUserWorker(IServiceScopeFactory scopes, DatabaseInitializationState dbState, IOptions<AuthServiceOptions> options, ILogger<DepartedUserWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BaseUrl) || string.IsNullOrWhiteSpace(o.InternalApiKey)) return;
        var interval = TimeSpan.FromSeconds(Math.Max(15, o.DepartedUserCheckSeconds));
        try
        {
            await dbState.WhenReady.WaitAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<DepartedUserMonitor>().RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Checking for users deleted in OmniConnect failed; will try again in {Interval}.", interval);
                }
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
