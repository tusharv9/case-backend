namespace CaseManagement.Api.Data;

using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using OmniConnect.Outbox;

/// <summary>
/// Gives every new <see cref="CaseEvent"/> its central audit copy (<see cref="CentralCaseAudit"/>) in the same save, through the
/// outbox: the local row and the copy commit together or not at all, from any code path that records an event (a request, the
/// SLA monitor, email intake), so a new place that writes the trail can never forget the platform's.
/// </summary>
/// <remarks>
/// The actor is the signed-in person of the request that saved it (from the verified token), never the event's own user
/// field — which background work fills with the case's holder or the system user. Work with no request (the monitors) is
/// filed as the system's. A save retried by the execution strategy adds nothing twice (the copy is keyed by the event's id).
/// </remarks>
public sealed class CaseAuditInterceptor(IHttpContextAccessor http, IOptions<HostIntegrationOptions> host, OutboxSignal<AppDbContext> signal)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db) Stage(db, CaseNumbers(db, ids => db.Cases.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.CaseNumber }).ToDictionary(c => c.Id, c => c.CaseNumber)));
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is AppDbContext db)
        {
            var missing = MissingCaseIds(db);
            var fromDb = missing.Count == 0 ? new Dictionary<Guid, string>()
                : await db.Cases.AsNoTracking().Where(c => missing.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.CaseNumber, ct);
            Stage(db, CaseNumbers(db, _ => fromDb));
        }
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) { signal.Wake(); return result; }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        signal.Wake();
        return ValueTask.FromResult(result);
    }

    private static List<CaseEvent> NewEvents(AppDbContext db) =>
        db.ChangeTracker.Entries<CaseEvent>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();

    private static List<Guid> MissingCaseIds(AppDbContext db)
    {
        var local = db.ChangeTracker.Entries<Case>().Select(e => e.Entity.Id).ToHashSet();
        return NewEvents(db).Select(e => e.CaseId).OfType<Guid>().Where(id => !local.Contains(id)).Distinct().ToList();
    }

    /// <summary>Case numbers of the new events' cases: those this context tracks, the rest from the database.</summary>
    private static Dictionary<Guid, string> CaseNumbers(AppDbContext db, Func<List<Guid>, Dictionary<Guid, string>> read)
    {
        var numbers = db.ChangeTracker.Entries<Case>().GroupBy(e => e.Entity.Id).ToDictionary(g => g.Key, g => g.First().Entity.CaseNumber);
        var missing = MissingCaseIds(db);
        if (missing.Count > 0)
            foreach (var (id, number) in read(missing)) numbers[id] = number;
        return numbers;
    }

    private void Stage(AppDbContext db, Dictionary<Guid, string> caseNumbers)
    {
        var events = NewEvents(db);
        if (events.Count == 0) return;
        var already = db.ChangeTracker.Entries<OutboxMessage>().Where(e => e.Entity.Topic == OutboxTopics.Audit).Select(e => e.Entity.EventId).ToHashSet();
        var request = RequestContext();
        foreach (var e in events.Where(e => !already.Contains(e.Id)))
        {
            var number = e.CaseId is { } id && caseNumbers.TryGetValue(id, out var n) ? n : null;
            var contract = CentralCaseAudit.For(e, number, request, host.Value.AppKey);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventId = contract.EventId,
                Topic = OutboxTopics.Audit,
                Payload = System.Text.Json.JsonSerializer.Serialize(contract, Outbox<AppDbContext>.Json),
                Status = OutboxStates.Pending,
                NextAttemptAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private AuditRequestContext RequestContext()
    {
        var ctx = http.HttpContext;
        if (ctx == null) return new AuditRequestContext(null, null, null, null, null);
        HostPrincipal? principal = null;
        try { principal = ctx.RequestServices.GetService<ICurrentUserAccessor>()?.Principal; }
        catch (Exception) { /* not signed in (or not resolved yet): filed without an actor */ }
        Guid? actor = Guid.TryParse(principal?.ExternalUserId, out var g) ? g : null;
        var ua = ctx.Request.Headers.UserAgent.ToString();
        var correlation = ctx.Request.Headers["X-Correlation-Id"].ToString();
        return new AuditRequestContext(actor, actor == null ? null : principal?.Name ?? principal?.Email,
            ctx.Connection.RemoteIpAddress?.ToString(), ua.Length > 0 ? ua : null, correlation.Length > 0 ? correlation : ctx.TraceIdentifier);
    }
}
