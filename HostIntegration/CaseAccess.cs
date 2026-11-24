namespace CaseManagement.Api.HostIntegration;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Which cases the caller may see. Being able to open Case Management is not a licence to read every team's cases.
/// </summary>
/// <remarks>
/// <para>A person sees a case when they hold it (the assigned agent), were added to it as a collaborator (or watcher), or
/// lead its team AND the Host grants them <see cref="Permissions.CasesViewTeam"/> (<c>cases:ViewTeam</c>).
/// <see cref="Permissions.CasesViewAll"/> (<c>cases:ViewAll</c>) and the Host administrator see every case. Being a member
/// of a team or department does NOT show its other agents' cases, and leading a team does not either without the Host
/// grant: who sees whose cases is decided in the Host, never by a role name or the team structure alone. The person still
/// needs <c>cases.read</c> to reach any case endpoint.</para>
///
/// <para>The rule is one SQL predicate, evaluated on every request — removing a collaborator, a new team lead, or the case
/// moving to another holder takes effect on their next request, with nothing cached to go stale.</para>
///
/// <para>Work with no caller (the SLA monitor, reconciliation, background jobs) is not a person and is not scoped.</para>
/// </remarks>
public interface ICaseAccessScope
{
    bool SeesAll { get; }
    IQueryable<Case> Apply(IQueryable<Case> cases);

    /// <summary>The same rule, for a query on another context instance (queries run side by side, each on its own).</summary>
    IQueryable<Case> Apply(IQueryable<Case> cases, AppDbContext db);
    Task<bool> CanSeeAsync(Guid caseId, CancellationToken ct = default);
}

public sealed class CaseAccessScope(AppDbContext context, ICurrentUserAccessor? currentUser = null) : ICaseAccessScope
{
    public bool SeesAll => currentUser?.LocalUserId == null || currentUser.Has(Permissions.CasesViewAll);

    public IQueryable<Case> Apply(IQueryable<Case> cases) => Apply(cases, context);

    public IQueryable<Case> Apply(IQueryable<Case> cases, AppDbContext db)
    {
        if (SeesAll) return cases;
        var me = currentUser!.LocalUserId!.Value;
        if (currentUser.Has(Permissions.CasesViewTeam))
            return cases.Where(c =>
                c.OwnerId == me
                || c.Participants.Any(p => p.UserId == me)
                || c.Team.OwnerId == me);
        return cases.Where(c => c.OwnerId == me || c.Participants.Any(p => p.UserId == me));
    }

    public Task<bool> CanSeeAsync(Guid caseId, CancellationToken ct = default) =>
        SeesAll
            ? context.Cases.AnyAsync(c => c.Id == caseId, ct)
            : Apply(context.Cases.AsNoTracking()).AnyAsync(c => c.Id == caseId, ct);
}

/// <summary>
/// The same visibility rule as <see cref="ICaseAccessScope"/>, asked about OTHER people: which of them may open a case. Used
/// where the system tells someone about a case (automatic escalation, a team lead's alert), so nobody is sent a case they
/// would get "not found" for.
/// </summary>
public interface ICaseAudience
{
    /// <summary>Which of <paramref name="userIds"/> may see the case (the holder always may). Anyone whose permissions cannot be read is left out.</summary>
    Task<IReadOnlyList<Guid>> WhoCanSeeAsync(Guid caseId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}

/// <remarks>
/// Each person's permissions come from the same place as on their own requests: AuthService's live answer (reused briefly by
/// <see cref="HostCapabilitiesClient"/>) translated by <see cref="IPermissionProvider"/>. Without AuthService (standalone
/// demo) the provider answers from what it has.
/// </remarks>
public sealed class CaseAudience(AppDbContext db, IPermissionProvider permissions, ILogger<CaseAudience> logger,
    HostCapabilitiesClient? capabilities = null) : ICaseAudience
{
    public async Task<IReadOnlyList<Guid>> WhoCanSeeAsync(Guid caseId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return Array.Empty<Guid>();
        var c = await db.Cases.AsNoTracking().Where(x => x.Id == caseId)
            .Select(x => new { x.OwnerId, LeadId = x.Team.OwnerId, Participants = x.Participants.Select(p => p.UserId).ToList() })
            .FirstOrDefaultAsync(ct);
        if (c == null) return Array.Empty<Guid>();

        var people = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id) && u.IsActive)
            .Select(u => new { u.Id, u.ExternalUserId, u.Name, u.Email, u.Role }).ToListAsync(ct);

        var result = new List<Guid>();
        foreach (var person in people)
        {
            if (person.Id == c.OwnerId) { result.Add(person.Id); continue; }

            var granted = await PermissionsOfAsync(person.ExternalUserId, person.Name, person.Email, person.Role, ct);
            bool Has(string p) => granted.Contains(Permissions.Everything) || granted.Contains(p);
            if (!Has(Permissions.CasesRead)) continue;

            if (c.Participants.Contains(person.Id)
                || Has(Permissions.CasesViewAll)
                || (c.LeadId == person.Id && Has(Permissions.CasesViewTeam)))
                result.Add(person.Id);
        }
        return result;
    }

    private async Task<IReadOnlySet<string>> PermissionsOfAsync(string? externalId, string? name, string? email, string? role, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return new HashSet<string>();
        IReadOnlyList<string> roles = string.IsNullOrWhiteSpace(role) ? [] : [role];
        if (capabilities is not { IsConfigured: true })
            return permissions.Resolve(new HostPrincipal(externalId, name, email, roles, []));
        try
        {
            var answer = await capabilities.ForUserAsync(externalId, ct);
            return permissions.Resolve(new HostPrincipal(externalId, name, email, roles, answer.Permissions, answer.IsAdministrator));
        }
        catch (HostAccessException ex)
        {
            // Unknown, inactive, or AuthService unreachable: not told about the case (fails closed).
            logger.LogInformation("Could not read the permissions of {User} ({Reason}); left out.", externalId, ex.Message);
            return new HashSet<string>();
        }
    }
}

/// <summary>
/// Refuses any request about a single case the caller may not see — reads and changes alike (status, assign, transfer,
/// notes, attachments…). It runs after the permission check and before the request body is read, and answers exactly as
/// for a case that does not exist (404, the API's usual error shape), so a case id cannot be used to learn that another
/// team's case exists.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CaseAccessAttribute(string routeParameter = "id") : Attribute, IAsyncResourceFilter
{
    public string RouteParameter { get; } = routeParameter;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (context.RouteData.Values.TryGetValue(RouteParameter, out var raw) && Guid.TryParse(raw?.ToString(), out var caseId))
        {
            var scope = context.HttpContext.RequestServices.GetRequiredService<ICaseAccessScope>();
            if (!await scope.CanSeeAsync(caseId, context.HttpContext.RequestAborted))
                throw new KeyNotFoundException("Case not found");
        }
        await next();
    }
}
