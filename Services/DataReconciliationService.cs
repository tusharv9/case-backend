namespace CaseManagement.Api.Services;

using System.Text.Json.Serialization;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// One-off data reconciliation (Phase 7 of the Case Management transformation). Finds data that the rules now enforced by the
/// application would not allow, and — only when asked to apply — fixes what can be fixed safely, in ONE transaction, with an
/// audit event for every case it touches. A dry run simulates the same changes in a transaction that is always rolled back. Anything that needs a business decision is reported, never guessed.
///
///   dotnet CaseManagement.Api.dll --reconcile-data                                   (dry run: report only, nothing is written)
///   dotnet CaseManagement.Api.dll --reconcile-data --apply --actor admin@bank.com    (apply; actor = who the audit events name)
///   add  --report reconciliation.json  to also write the report as JSON
/// </summary>
public class DataReconciliationService
{

    private readonly AppDbContext _context;
    private readonly IRoutingEngineService _routing;

    public DataReconciliationService(AppDbContext context, IRoutingEngineService routing)
    {
        _context = context;
        _routing = routing;
    }

    public sealed class Report
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public bool Applied { get; set; }
        public List<StatusFix> UserStatuses { get; set; } = new();
        public List<CaseHolderFix> CasesHeldOutsideTheirTeam { get; set; } = new();
        public List<CaseHolderFix> CasesNeedingADecision { get; set; } = new();
        public int BlankSubcategoriesToClear { get; set; }
        public List<UnknownSubcategory> UnknownSubcategories { get; set; } = new();
        public List<TeamNote> Teams { get; set; } = new();
        public int UsersWithLegacyTeamLabels { get; set; }
        public List<string> UsersInSeveralTeams { get; set; } = new();
    }

    public sealed record StatusFix(Guid UserId, string User, string StoredValue, string NewValue);

    /// <param name="How">For a redistributed case: the algorithm that chose the agent, or why the team lead holds it.</param>
    public sealed record CaseHolderFix(Guid CaseId, string CaseNumber, string Team, bool TeamIsActive, string Holder, bool HolderIsActive, string? NewHolder, string Reason, string? How = null);

    public sealed record UnknownSubcategory(string Team, string Subcategory, int Cases, int OpenCases);

    public sealed record TeamNote(string Team, string Code, bool IsActive, string? Lead, int ActiveMembers, int OpenCases, int AllCases, int RoutingRules, int CrossTeamSelections, string Assessment);

    /// <summary>Builds the report; with <paramref name="apply"/> also applies the safe fixes (one transaction).</summary>
    public async Task<Report> RunAsync(bool apply, Guid? actorId, CancellationToken ct = default)
    {
        if (apply && actorId == null) throw new InvalidOperationException("Applying needs --actor <email of an active user>: the audit events must name who made the change.");

        var report = new Report { Applied = apply };
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            await AnalyseAsync(report, ct);
            // The redistribution runs in both modes: in a dry run it is simulated inside this transaction (then rolled back), so the
            // report shows exactly who each case would go to.
            await RedistributeAsync(report, ct);
            if (apply)
            {
                await ApplyAsync(report, actorId!.Value, ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                await tx.RollbackAsync(ct);   // a dry run never keeps anything
            }
        });
        return report;
    }

    private async Task AnalyseAsync(Report report, CancellationToken ct)
    {
        // 1. User statuses stored as something other than a status name (e.g. "0" from an old integer column).
        var names = Enum.GetNames<UserStatus>();
        var rawStatuses = await _context.Database
            .SqlQuery<RawStatus>($@"SELECT ""Id"", ""Name"", ""Status"" FROM ""Users""")
            .ToListAsync(ct);
        foreach (var u in rawStatuses.Where(u => !names.Contains(u.Status)))
        {
            // A number is the old integer encoding of the same enum; anything else is unknown, so the safe choice is Offline
            // (never offered for assignment) rather than inventing availability.
            var newValue = int.TryParse(u.Status, out var n) && Enum.IsDefined(typeof(UserStatus), n) ? ((UserStatus)n).ToString() : nameof(UserStatus.Offline);
            report.UserStatuses.Add(new StatusFix(u.Id, u.Name, u.Status ?? "(null)", newValue));
        }

        // 2. Open cases whose holder is not a valid holder for the case's team (an active member or the team's lead, active account).
        var open = await _context.Cases.AsNoTracking()
            .Where(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled)
            .Select(c => new
            {
                c.Id, c.CaseNumber, c.DepartmentId, c.OwnerId,
                Team = c.Department.Name, TeamActive = c.Department.IsActive, LeadId = c.Department.OwnerId,
                Holder = c.Owner.Name, HolderActive = c.Owner.IsActive,
                HolderIsMember = _context.TeamMembers.Any(m => m.DepartmentId == c.DepartmentId && m.UserId == c.OwnerId && m.IsActive),
            })
            .ToListAsync(ct);
        var activeLeads = await _context.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Name }).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        foreach (var c in open)
        {
            var validHolder = c.HolderActive && (c.HolderIsMember || c.LeadId == c.OwnerId);
            if (validHolder) continue;

            var reason = !c.HolderActive ? $"{c.Holder}'s account is deactivated" : $"{c.Holder} is not a member or the lead of '{c.Team}'";
            string? lead = c.LeadId.HasValue && activeLeads.TryGetValue(c.LeadId.Value, out var leadName) ? leadName : null;
            var fix = new CaseHolderFix(c.Id, c.CaseNumber, c.Team, c.TeamActive, c.Holder, c.HolderActive, lead, reason);

            // The team's active lead holds the case until they reassign it — the same fallback routing uses. An inactive team or a team
            // without an active lead needs a person to decide.
            if (c.TeamActive && lead != null && c.LeadId != c.OwnerId) report.CasesHeldOutsideTheirTeam.Add(fix);
            else report.CasesNeedingADecision.Add(fix with { NewHolder = null, Reason = reason + (c.TeamActive ? "; the team has no active lead" : "; the team is inactive") });
        }

        // 3. Sub-category text on cases that is not a sub-category of the case's team.
        report.BlankSubcategoriesToClear = await _context.Cases.CountAsync(c => c.Subcategory != null && c.Subcategory.Trim() == "", ct);
        var known = await _context.DepartmentSubCategories.AsNoTracking().Select(s => new { s.DepartmentId, Name = s.Name.ToLower() }).ToListAsync(ct);
        var knownSet = known.Select(k => (k.DepartmentId, k.Name)).ToHashSet();
        var used = await _context.Cases.AsNoTracking()
            .Where(c => c.Subcategory != null && c.Subcategory.Trim() != "")
            .GroupBy(c => new { c.DepartmentId, Team = c.Department.Name, c.Subcategory })
            .Select(g => new { g.Key.DepartmentId, g.Key.Team, g.Key.Subcategory, Cases = g.Count(), Open = g.Count(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled) })
            .ToListAsync(ct);
        report.UnknownSubcategories = used
            .Where(u => !knownSet.Contains((u.DepartmentId, u.Subcategory!.ToLower())))
            .Select(u => new UnknownSubcategory(u.Team, u.Subcategory!, u.Cases, u.Open))
            .OrderBy(u => u.Team).ThenBy(u => u.Subcategory)
            .ToList();

        // 4. Teams (= departments): what each is used by, so retiring one never loses a reference.
        var teams = await _context.Departments.AsNoTracking()
            .Select(d => new
            {
                d.Name, d.Code, d.IsActive,
                Lead = d.Owner != null ? d.Owner.Name : null,
                Members = d.Members.Count(m => m.IsActive),
                Open = d.Cases.Count(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled),
                All = d.Cases.Count,
                Rules = _context.RoutingRules.Count(r => r.TargetDepartmentId == d.Id),
                Cross = _context.ManualAssignmentCrossTeams.Count(x => x.DepartmentId == d.Id),
            })
            .OrderBy(d => d.Name)
            .ToListAsync(ct);
        report.Teams = teams.Select(t => new TeamNote(t.Name, t.Code, t.IsActive, t.Lead, t.Members, t.Open, t.All, t.Rules, t.Cross,
            t.IsActive
                ? (t.Lead == null ? "Active, but has no lead: cases nobody can take stay with their creator." : t.Members == 0 ? "Active, but has no members." : "Active.")
                : t.All == 0 && t.Rules == 0 && t.Cross == 0
                    ? "Inactive and unused: safe to keep archived (no cases, rules or settings refer to it)."
                    : "Inactive but still referenced: keep it (deleting would lose history).")).ToList();

        // 5. Legacy free-text labels the application no longer reads (team membership is the source).
        report.UsersWithLegacyTeamLabels = await _context.Users.CountAsync(u => (u.Team != null && u.Team != "") || (u.Queue != null && u.Queue != ""), ct);

        // 6. People in several teams (allowed; listed for awareness).
        report.UsersInSeveralTeams = await _context.TeamMembers.AsNoTracking()
            .Where(m => m.IsActive)
            .GroupBy(m => m.User.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key + " (" + g.Count() + " teams)")
            .ToListAsync(ct);
    }

    /// <summary>
    /// Gives each case held outside its team to the team's own assignment: the team's algorithm picks among its available agents
    /// below capacity, exactly as for a new case; when nobody can take it the team's lead holds it. Oldest cases first, and each
    /// assignment is saved (inside the transaction) before the next, so every choice sees the workload the previous ones created.
    /// </summary>
    private async Task RedistributeAsync(Report report, CancellationToken ct)
    {
        if (report.CasesHeldOutsideTheirTeam.Count == 0) return;
        var ids = report.CasesHeldOutsideTheirTeam.Select(f => f.CaseId).ToList();
        var cases = await _context.Cases.Include(c => c.Customer).Where(c => ids.Contains(c.Id)).OrderBy(c => c.CreatedAt).ToListAsync(ct);

        var planned = new List<CaseHolderFix>();
        foreach (var c in cases)
        {
            var fix = report.CasesHeldOutsideTheirTeam.First(f => f.CaseId == c.Id);
            var decision = await _routing.AssignWithinTeamAsync(c, c.Customer, c.DepartmentId, "data reconciliation", ct);
            c.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            var how = decision.HeldByUserId != null ? $"held by the team lead: {decision.HeldReason}" : $"{decision.AlgorithmUsed}";
            planned.Add(fix with { NewHolder = decision.AssignedUserName, How = how });
        }
        report.CasesHeldOutsideTheirTeam = planned;
    }

    private async Task ApplyAsync(Report report, Guid actorId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        foreach (var fix in report.UserStatuses)
            await _context.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""Users"" SET ""Status"" = {fix.NewValue}, ""UpdatedAt"" = {now} WHERE ""Id"" = {fix.UserId}", ct);

        foreach (var fix in report.CasesHeldOutsideTheirTeam)
        {
            _context.CaseEvents.Add(new CaseEvent
            {
                Id = Guid.NewGuid(), CaseId = fix.CaseId, EventType = EventType.Assign, CreatedAt = now, UserId = actorId,
                Message = $"Reassigned to {fix.NewHolder} by data reconciliation ({fix.How}): {fix.Reason}. The SLA clock is unchanged.",
            });
        }

        if (report.BlankSubcategoriesToClear > 0)
            await _context.Cases.Where(c => c.Subcategory != null && c.Subcategory.Trim() == "")
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Subcategory, (string?)null), ct);

        await _context.SaveChangesAsync(ct);
    }

    private sealed class RawStatus
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
    }

    public static string ToText(Report r)
    {
        var lines = new List<string>
        {
            $"DATA RECONCILIATION — {(r.Applied ? "APPLIED" : "DRY RUN (nothing was written)")} — {r.GeneratedAt:u}",
            "",
            $"1. User statuses that are not a status name: {r.UserStatuses.Count}",
        };
        lines.AddRange(r.UserStatuses.Select(s => $"   {s.User}: \"{s.StoredValue}\" -> {s.NewValue}"));
        lines.Add("");
        lines.Add($"2a. Open cases held outside their team -> the team's algorithm (lead holds the rest): {r.CasesHeldOutsideTheirTeam.Count}");
        lines.AddRange(r.CasesHeldOutsideTheirTeam.GroupBy(f => (f.Team, f.NewHolder, Lead: f.How?.StartsWith("held by") == true))
            .OrderBy(g => g.Key.Team).ThenByDescending(g => g.Count())
            .Select(g => $"   {g.Count(),3} x {g.Key.Team} -> {g.Key.NewHolder}{(g.Key.Lead ? " (team lead holds: nobody else can take more)" : "")}"));
        lines.Add($"2b. Open cases that need a person to decide (inactive team or no active lead): {r.CasesNeedingADecision.Count}");
        lines.AddRange(r.CasesNeedingADecision.Select(f => $"   {f.CaseNumber} ({f.Team}): {f.Reason}"));
        lines.Add("");
        lines.Add($"3a. Blank sub-category values -> cleared (no sub-category): {r.BlankSubcategoriesToClear}");
        lines.Add($"3b. Sub-category values that are not a sub-category of the case's team (report only): {r.UnknownSubcategories.Count}");
        lines.AddRange(r.UnknownSubcategories.Select(u => $"   {u.Team}: \"{u.Subcategory}\" on {u.Cases} case(s), {u.OpenCases} open"));
        lines.Add("");
        lines.Add("4. Teams (= departments):");
        lines.AddRange(r.Teams.Select(t => $"   {t.Team} ({t.Code}) lead={t.Lead ?? "—"} members={t.ActiveMembers} open={t.OpenCases} all={t.AllCases} rules={t.RoutingRules}: {t.Assessment}"));
        lines.Add("");
        lines.Add($"5. Users with legacy Team/Queue labels (no longer read by the application): {r.UsersWithLegacyTeamLabels}");
        lines.Add($"6. People in several teams (allowed): {(r.UsersInSeveralTeams.Count == 0 ? "none" : string.Join(", ", r.UsersInSeveralTeams))}");
        return string.Join(Environment.NewLine, lines);
    }
}
