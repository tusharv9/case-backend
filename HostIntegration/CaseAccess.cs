namespace CaseManagement.Api.HostIntegration;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Which cases the caller may see. Being able to open Case Management is not a licence to read every team's cases.
/// </summary>
/// <remarks>
/// <para>A person sees a case when they hold it, take part in it (co-worker, participant), lead its team, or are an
/// ACTIVE member of its team. <see cref="Permissions.CasesViewAll"/> (Host capability <c>cases:ViewAll</c>) and the
/// Host administrator see every case. Team membership never grants permissions: the person still needs
/// <c>cases.read</c> to reach any case endpoint at all.</para>
///
/// <para>The rule is one SQL predicate over the live <c>TeamMembers</c> table, evaluated on every request — removing
/// someone from a team, or the case moving to another team, takes effect on their next request, with nothing cached
/// in the browser or the server to go stale.</para>
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
        var members = db.TeamMembers;
        return cases.Where(c =>
            c.OwnerId == me
            || c.Participants.Any(p => p.UserId == me)
            || c.Department.OwnerId == me
            || members.Any(m => m.UserId == me && m.IsActive && m.DepartmentId == c.DepartmentId));
    }

    public Task<bool> CanSeeAsync(Guid caseId, CancellationToken ct = default) =>
        SeesAll
            ? context.Cases.AnyAsync(c => c.Id == caseId, ct)
            : Apply(context.Cases.AsNoTracking()).AnyAsync(c => c.Id == caseId, ct);
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
