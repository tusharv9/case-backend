namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using Microsoft.EntityFrameworkCore;

public interface IAuditLogService
{
    Task<PagedResponseDto<CaseAuditEventDto>> ListAsync(AuditFilter filter, int page, int pageSize, CancellationToken ct = default);
    Task<AuditFiltersDto> GetFiltersAsync(CancellationToken ct = default);
    Task<int> CountAsync(AuditFilter filter, CancellationToken ct = default);
    /// <summary>The newest <paramref name="limit"/> (at most <see cref="AuditLogService.MaxExportRows"/>) events matching the filter, as CSV.</summary>
    Task<ReportExport> ExportAsync(AuditFilter filter, int limit, CancellationToken ct = default);
}

/// <summary>
/// The Case Audit Trail: its list, its filter choices and its export. The list, the count and the export share one query
/// (<see cref="ICaseRepository"/>), so an export holds exactly the rows the screen would show for the same filters — limited to
/// the cases the caller may see, with customer names masked by the same rules as on screen.
/// </summary>
public class AuditLogService : IAuditLogService
{
    /// <summary>The most rows one export holds: enough for any review, small enough never to exhaust the server.</summary>
    public const int MaxExportRows = 10_000;

    private readonly AppDbContext _context;
    private readonly ICaseRepository _cases;
    private readonly ISlaClockProvider _clock;
    private readonly IPiiMaskingService? _masking;
    private readonly ICaseAccessScope? _access;
    private readonly ICurrentUserAccessor? _currentUser;
    private readonly IDbContextFactory<AppDbContext>? _contexts;

    public AuditLogService(AppDbContext context, ICaseRepository cases, ISlaClockProvider clock, IPiiMaskingService? masking = null,
        ICaseAccessScope? access = null, ICurrentUserAccessor? currentUser = null, IDbContextFactory<AppDbContext>? contexts = null)
    {
        _context = context;
        _cases = cases;
        _clock = clock;
        _masking = masking;
        _access = access;
        _currentUser = currentUser;
        _contexts = contexts;
    }

    private bool SeesAll => _access?.SeesAll ?? true;

    public async Task<PagedResponseDto<CaseAuditEventDto>> ListAsync(AuditFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        var events = await _cases.GetCaseAuditEventsAsync(filter, page, pageSize, ct);
        if (_masking != null) await _masking.MaskCasesAsync(events.Items, ct);   // audit rows show the customer's name
        return events;
    }

    public Task<int> CountAsync(AuditFilter filter, CancellationToken ct = default) => _cases.CountCaseAuditEventsAsync(filter, ct);

    public async Task<AuditFiltersDto> GetFiltersAsync(CancellationToken ct = default)
    {
        var result = new AuditFiltersDto
        {
            SeesAllTeams = SeesAll,
            MaxExportRows = MaxExportRows,
            ActionTypes = AuditLabels.Filters
                .Where(a => SeesAll || (a.Value != "config" && a.Value != "team"))   // configuration history is only for those who see everything
                .Select(a => new FilterOptionDto { Value = a.Value, Label = a.Label }).ToList(),
        };

        var me = _currentUser?.LocalUserId;
        var seesAll = SeesAll || me == null;
        await ParallelReads.RunAsync(_contexts, _context, ct,
            async db =>
            {
                // People: everyone for someone who sees all; otherwise the people of the teams the caller leads or belongs to.
                var teamIds = seesAll ? null : MyTeams(db, me!.Value);
                result.Actors = await db.Users.AsNoTracking()
                    .Where(u => seesAll ? true : db.TeamMembers.Any(m => m.UserId == u.Id && m.IsActive && teamIds!.Contains(m.DepartmentId)))
                    .OrderBy(u => u.Name).Select(u => new NamedOptionDto { Id = u.Id, Name = u.Name }).ToListAsync(ct);
            },
            async db =>
            {
                var teamIds = seesAll ? null : MyTeams(db, me!.Value);
                result.Teams = await db.Departments.AsNoTracking()
                    .Where(d => seesAll || teamIds!.Contains(d.Id))
                    .OrderBy(d => d.Name).Select(d => new NamedOptionDto { Id = d.Id, Name = d.Name }).ToListAsync(ct);
            },
            async db =>
            {
                var teamIds = seesAll ? null : MyTeams(db, me!.Value);
                result.Departments = await db.OrgDepartments.AsNoTracking()
                    .Where(o => seesAll || o.Teams.Any(l => teamIds!.Contains(l.TeamId)))
                    .OrderBy(o => o.DisplayOrder).ThenBy(o => o.Name).Select(o => new NamedOptionDto { Id = o.Id, Name = o.Name }).ToListAsync(ct);
            });
        return result;
    }

    /// <summary>The teams a person leads or is an active member of.</summary>
    private static IQueryable<Guid> MyTeams(AppDbContext db, Guid me) =>
        db.Departments.Where(d => d.OwnerId == me || db.TeamMembers.Any(m => m.DepartmentId == d.Id && m.UserId == me && m.IsActive)).Select(d => d.Id);

    public async Task<ReportExport> ExportAsync(AuditFilter filter, int limit, CancellationToken ct = default)
    {
        if (limit < 1) throw new ArgumentException("Choose how many records to export (at least 1).");
        var take = Math.Min(limit, MaxExportRows);

        var rows = await _cases.GetCaseAuditEventsForExportAsync(filter, take + 1, ct);
        var truncated = rows.Count > take;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        if (_masking != null) await _masking.MaskCasesAsync(rows, ct);

        var zone = (await _clock.GetAsync(ct)).Calendar.TimeZone;
        string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm:ss");

        var header = new[] { "Timestamp", "Action", "Performed By", "Role", "Case Number", "Case Title", "Team", "Customer", "Module", "Description" };
        var csv = ReportCsv.Build(header, rows.Select(r => (IReadOnlyList<string?>)new[]
        {
            Local(r.Timestamp),
            r.ActionLabel,
            r.ActorName,
            r.ActorRole,
            r.CaseId.HasValue ? r.CaseNumber : string.Empty,
            r.CaseId.HasValue ? r.CaseTitle : string.Empty,
            r.DepartmentName,
            r.CustomerName,
            r.Module,
            MentionService.ToPlainText(r.Description),
        }));

        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        // Truncated = more events match than the file holds (the chosen number, or the system cap).
        return new ReportExport(csv, rows.Count, truncated, today);
    }
}
