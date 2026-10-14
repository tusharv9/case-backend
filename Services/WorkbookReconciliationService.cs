namespace CaseManagement.Api.Services;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// One-off reconciliation of the case classification with the business workbook (RMA_OmniCX_Case_Categories_Banking): its
/// categories become the departments (= teams), its sub-categories the selectable sub-categories with their description and
/// default priority, and its SLA matrix the priorities' targets. The workbook is INPUT (its two worksheets exported as CSV) and the
/// approved choices come from a decisions file — no business data lives in code.
///
///   dotnet CaseManagement.Api.dll --reconcile-workbook &lt;folder with the worksheet CSVs&gt; --decisions &lt;file.json&gt;
///         [--apply --actor &lt;email&gt;] [--report &lt;file.json&gt;]
///
/// Matching is by business identifier — department code = category ID, sub-category code = sub-category ID — so a second run
/// finds nothing to do. Existing teams named in the decisions file are renamed in place (id, members, cases and history kept);
/// records outside the workbook are deactivated, never deleted; existing cases are not touched. Everything runs in ONE transaction;
/// a dry run performs the same changes and rolls them back.
/// </summary>
public class WorkbookReconciliationService
{
    public const string CategoriesSheet = "Case Categories";
    public const string SlaSheet = "SLA Matrix";

    private readonly AppDbContext _context;

    public WorkbookReconciliationService(AppDbContext context) => _context = context;

    // ------------------------------------------------------------------------------------------------- input

    public sealed record CategoryRow(string CategoryId, string Category, string SubCategoryId, string SubCategory, string Description, string DefaultPriority, int Order);

    public sealed record SlaRow(string Priority, decimal ResponseHours, decimal ResolutionHours);

    public sealed class Workbook
    {
        public List<CategoryRow> Rows { get; init; } = new();
        public List<SlaRow> Sla { get; init; } = new();
    }

    /// <summary>The approved choices (see WORKBOOK_RECONCILIATION_PLAN.md).</summary>
    public sealed class Decisions
    {
        /// <summary>Workbook category ID → code of the existing team that is the same business entity (renamed in place).</summary>
        public Dictionary<string, string> RenameTeams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Codes of active teams outside the workbook to deactivate (their routing rules are deactivated too).</summary>
        public List<string> DeactivateTeams { get; set; } = new();
        /// <summary>Names of routing rules to deactivate.</summary>
        public List<string> DeactivateRoutingRules { get; set; } = new();
        /// <summary>"keep" (default): the internal resolution target is left as it is; "sameAsExternal": set it to the workbook's resolution.</summary>
        public string InternalResolution { get; set; } = "keep";
    }

    /// <summary>Reads the two worksheets (Numbers / Excel CSV export: "&lt;sheet&gt;-Table 1.csv", or "&lt;sheet&gt;.csv").</summary>
    public static Workbook ReadWorkbook(string folder)
    {
        string Find(string sheet) =>
            new[] { $"{sheet}-Table 1.csv", $"{sheet}.csv" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException($"Worksheet '{sheet}' not found in {folder} (expected '{sheet}-Table 1.csv' or '{sheet}.csv').");

        var errors = new List<string>();

        // Case Categories: the table starts at the header row "Category ID, Category, Sub-category ID, Sub-category, Description, Default Priority".
        var cat = ParseCsv(File.ReadAllText(Find(CategoriesSheet)));
        var header = cat.FindIndex(r => Cell(r, 0).Equals("Category ID", StringComparison.OrdinalIgnoreCase));
        if (header < 0) throw new InvalidOperationException($"'{CategoriesSheet}': header row starting with 'Category ID' not found.");
        int Col(string name) => cat[header].FindIndex(c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0
            ? i : throw new InvalidOperationException($"'{CategoriesSheet}': column '{name}' not found.");
        var (cId, cName, sId, sName, sDesc, sPrio) = (Col("Category ID"), Col("Category"), Col("Sub-category ID"), Col("Sub-category"), Col("Description"), Col("Default Priority"));

        var rows = new List<CategoryRow>();
        foreach (var r in cat.Skip(header + 1).Where(r => r.Any(c => c.Trim().Length > 0)))
        {
            var row = new CategoryRow(Cell(r, cId), Cell(r, cName), Cell(r, sId), Cell(r, sName), Cell(r, sDesc), Cell(r, sPrio), rows.Count + 1);
            if (new[] { row.CategoryId, row.Category, row.SubCategoryId, row.SubCategory, row.DefaultPriority }.Any(v => v.Length == 0))
                errors.Add($"'{CategoriesSheet}' row {header + rows.Count + 2}: category ID, category, sub-category ID, sub-category and default priority are all required.");
            else if (!row.SubCategoryId.StartsWith(row.CategoryId + "-", StringComparison.OrdinalIgnoreCase))
                errors.Add($"'{CategoriesSheet}': {row.SubCategoryId} does not belong to category {row.CategoryId}.");
            rows.Add(row);
        }
        errors.AddRange(rows.GroupBy(r => r.SubCategoryId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"'{CategoriesSheet}': sub-category ID {g.Key} appears {g.Count()} times."));
        errors.AddRange(rows.GroupBy(r => r.CategoryId, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(r => r.Category).Distinct().Count() > 1).Select(g => $"'{CategoriesSheet}': category {g.Key} has more than one name."));
        errors.AddRange(rows.GroupBy(r => (r.CategoryId.ToUpperInvariant(), r.SubCategory.ToLowerInvariant())).Where(g => g.Count() > 1).Select(g => $"'{CategoriesSheet}': '{g.First().SubCategory}' appears twice in category {g.First().CategoryId}."));

        // SLA Matrix: header "Priority, Response SLA (hrs), Resolution SLA (hrs)", rows until the first blank priority.
        var sla = ParseCsv(File.ReadAllText(Find(SlaSheet)));
        var slaHeader = sla.FindIndex(r => Cell(r, 0).Equals("Priority", StringComparison.OrdinalIgnoreCase));
        if (slaHeader < 0) throw new InvalidOperationException($"'{SlaSheet}': header row starting with 'Priority' not found.");
        int SlaCol(string starts) => sla[slaHeader].FindIndex(c => c.Trim().StartsWith(starts, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0
            ? i : throw new InvalidOperationException($"'{SlaSheet}': column '{starts} …' not found.");
        var (resp, reso) = (SlaCol("Response"), SlaCol("Resolution"));
        if (!sla[slaHeader][resp].Contains("hrs", StringComparison.OrdinalIgnoreCase) || !sla[slaHeader][reso].Contains("hrs", StringComparison.OrdinalIgnoreCase))
            errors.Add($"'{SlaSheet}': the response and resolution columns must be in hours ('(hrs)').");

        var slaRows = new List<SlaRow>();
        foreach (var r in sla.Skip(slaHeader + 1))
        {
            if (Cell(r, 0).Length == 0) break;
            if (!decimal.TryParse(Cell(r, resp), NumberStyles.Number, CultureInfo.InvariantCulture, out var h1) || h1 <= 0
                || !decimal.TryParse(Cell(r, reso), NumberStyles.Number, CultureInfo.InvariantCulture, out var h2) || h2 <= 0)
                errors.Add($"'{SlaSheet}': '{Cell(r, 0)}' needs positive response and resolution hours.");
            else slaRows.Add(new SlaRow(Cell(r, 0), h1, h2));
        }
        var slaNames = slaRows.Select(s => s.Priority).ToHashSet(StringComparer.OrdinalIgnoreCase);
        errors.AddRange(rows.Select(r => r.DefaultPriority).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !slaNames.Contains(p))
            .Select(p => $"Default priority '{p}' is not in the '{SlaSheet}'."));

        if (errors.Count > 0) throw new InvalidOperationException("The workbook is not consistent:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        return new Workbook { Rows = rows, Sla = slaRows };
    }

    private static string Cell(List<string> row, int i) => i < row.Count ? row[i].Trim() : string.Empty;

    /// <summary>RFC 4180 CSV: quoted fields, doubled quotes, line breaks inside quotes.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = new List<string>();
            }
            else if (ch != '﻿') cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // ------------------------------------------------------------------------------------------------- report

    public sealed class Report
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public bool Applied { get; set; }
        public Counts Before { get; set; } = new();
        public Counts After { get; set; } = new();
        public List<TeamChange> Teams { get; set; } = new();
        public List<RuleChange> RoutingRules { get; set; } = new();
        public List<SubCategoryChange> SubCategories { get; set; } = new();
        public List<RetiredSubCategory> RetiredSubCategories { get; set; } = new();
        public List<PriorityChange> Priorities { get; set; } = new();
        public List<string> Notes { get; set; } = new();
    }

    public sealed class Counts
    {
        public int Departments { get; set; }
        public int ActiveDepartments { get; set; }
        public int SubCategories { get; set; }
        public int ActiveSubCategories { get; set; }
        public int ActiveMappedSubCategories { get; set; }
        public int ActivePriorities { get; set; }
    }

    /// <param name="Action">Created, Renamed, Updated, Reactivated, Deactivated or Unchanged.</param>
    public sealed record TeamChange(string Code, string Name, string Action, string? Before, int Members, int Cases, int OpenCases);

    public sealed record RuleChange(string Rule, string Action, string? Before, string? After);

    /// <param name="Action">Created, Adopted (an existing record of the same name in the same team), Updated or Unchanged.</param>
    public sealed record SubCategoryChange(string Code, string Team, string Name, string Priority, string Action, string? Before);

    public sealed record RetiredSubCategory(string Team, string Name, string Code, int Cases, int OpenCases);

    public sealed record PriorityChange(string Priority, string Action, string Before, string After, int Version);

    // ------------------------------------------------------------------------------------------------- run

    public async Task<Report> RunAsync(Workbook workbook, Decisions decisions, bool apply, Guid? actorId, CancellationToken ct = default)
    {
        if (apply && actorId == null) throw new InvalidOperationException("Applying needs --actor <email of an active user>: the audit events must name who made the change.");
        if (!new[] { "keep", "sameAsExternal" }.Contains(decisions.InternalResolution, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("decisions.internalResolution must be \"keep\" or \"sameAsExternal\".");

        var report = new Report { Applied = apply };
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            report = new Report { Applied = apply };
            _context.ChangeTracker.Clear();
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            report.Before = await CountAsync(ct);
            var audit = new List<CaseEvent>();
            var now = DateTime.UtcNow;

            var teams = await ReconcileTeamsAsync(workbook, decisions, report, audit, now, ct);
            await ReconcileSubCategoriesAsync(workbook, teams, report, audit, now, ct);
            await ReconcileSlaAsync(workbook, decisions, report, audit, now, ct);

            if (apply)
            {
                foreach (var e in audit) { e.UserId = actorId!.Value; _context.CaseEvents.Add(e); }
                await _context.SaveChangesAsync(ct);
            }
            report.After = await CountAsync(ct);
            if (apply) await tx.CommitAsync(ct);
            else await tx.RollbackAsync(ct);   // a dry run never keeps anything
        });
        _context.ChangeTracker.Clear();
        return report;
    }

    private async Task<Counts> CountAsync(CancellationToken ct) => new()
    {
        Departments = await _context.Departments.CountAsync(ct),
        ActiveDepartments = await _context.Departments.CountAsync(d => d.IsActive, ct),
        SubCategories = await _context.DepartmentSubCategories.CountAsync(ct),
        ActiveSubCategories = await _context.DepartmentSubCategories.CountAsync(s => s.IsActive, ct),
        ActiveMappedSubCategories = await _context.DepartmentSubCategories.CountAsync(s => s.IsActive && _context.PriorityCategoryMappings.Any(m => m.DepartmentSubCategoryId == s.Id), ct),
        ActivePriorities = await _context.PrioritySlaRules.CountAsync(r => r.IsActive, ct),
    };

    private static CaseEvent Audit(string module, string entity, string action, string message, string? oldValue, string? newValue, DateTime now) => new()
    {
        Id = Guid.NewGuid(), CaseId = null, EventType = EventType.Other, CreatedAt = now, IsInternal = true,
        Module = module, EntityName = entity, ActionType = action, Message = message + " (workbook reconciliation)", OldValue = oldValue, NewValue = newValue,
    };

    // Stored for the database and read back by the routing engine, never written into HTML: keep "&" etc. readable.
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Departments (= teams): one active team per workbook category; everything else active must be listed for deactivation.</summary>
    private async Task<Dictionary<string, Department>> ReconcileTeamsAsync(Workbook wb, Decisions decisions, Report report, List<CaseEvent> audit, DateTime now, CancellationToken ct)
    {
        var departments = await _context.Departments.ToListAsync(ct);
        var rules = await _context.RoutingRules.ToListAsync(ct);
        var categories = wb.Rows.GroupBy(r => r.CategoryId.ToUpperInvariant()).Select(g => (Id: g.First().CategoryId, Name: g.First().Category)).ToList();
        var workbookCodes = categories.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stats = await _context.Departments.AsNoTracking().Select(d => new
        {
            d.Id,
            Members = d.Members.Count(m => m.IsActive),
            Cases = d.Cases.Count,
            Open = d.Cases.Count(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled),
        }).ToDictionaryAsync(d => d.Id, ct);
        TeamChange Note(Department d, string action, string? before) =>
            stats.TryGetValue(d.Id, out var s) ? new(d.Code, d.Name, action, before, s.Members, s.Cases, s.Open) : new(d.Code, d.Name, action, before, 0, 0, 0);

        var errors = new List<string>();
        foreach (var (from, to) in decisions.RenameTeams)
            if (!workbookCodes.Contains(from)) errors.Add($"decisions.renameTeams: '{from}' is not a category ID of the workbook.");
        var result = new Dictionary<string, Department>(StringComparer.OrdinalIgnoreCase);

        foreach (var (code, name) in categories)
        {
            var team = departments.FirstOrDefault(d => Same(d.Code, code));
            if (team == null && decisions.RenameTeams.TryGetValue(code, out var sourceCode))
            {
                team = departments.FirstOrDefault(d => Same(d.Code, sourceCode));
                if (team == null) { errors.Add($"decisions.renameTeams: no team has the code '{sourceCode}' (for {code} {name})."); continue; }
            }
            var clash = departments.FirstOrDefault(d => d != team && Same(d.Name, name));
            if (clash != null) { errors.Add($"Another team ({clash.Code}) is already called '{name}'."); continue; }

            if (team == null)
            {
                team = new Department { Id = Guid.NewGuid(), Name = name, Code = code.ToUpperInvariant(), IsActive = true, CreatedAt = now };
                _context.Departments.Add(team);
                departments.Add(team);
                report.Teams.Add(Note(team, "Created", null));
                audit.Add(Audit("Teams", name, "CREATE", $"Created team '{name}' ({team.Code})", null, $"Name: {name}, Code: {team.Code}", now));
            }
            else
            {
                var before = $"{team.Name} ({team.Code}){(team.IsActive ? "" : ", inactive")}";
                var (oldName, oldCode) = (team.Name, team.Code);
                var renamed = !string.Equals(team.Name, name, StringComparison.Ordinal) || !string.Equals(team.Code, code.ToUpperInvariant(), StringComparison.Ordinal);
                var reactivated = !team.IsActive;
                if (renamed || reactivated)
                {
                    team.Name = name; team.Code = code.ToUpperInvariant(); team.IsActive = true; team.UpdatedAt = now;
                    report.Teams.Add(Note(team, renamed ? "Renamed" : "Reactivated", before));
                    audit.Add(Audit("Teams", name, "UPDATE", renamed ? $"Renamed team '{oldName}' ({oldCode}) to '{name}' ({team.Code})" : $"Reactivated team '{name}'",
                        $"Name: {oldName}, Code: {oldCode}, Active: {!reactivated}", $"Name: {name}, Code: {team.Code}, Active: True", now));
                    if (renamed) RenameInRoutingRules(rules, oldName, oldCode, name, report, audit, now);
                }
                else report.Teams.Add(Note(team, "Unchanged", null));
            }
            result[code] = team;
        }

        foreach (var code in decisions.DeactivateTeams)
        {
            var team = departments.FirstOrDefault(d => Same(d.Code, code));
            if (team == null) { errors.Add($"decisions.deactivateTeams: no team has the code '{code}'."); continue; }
            if (workbookCodes.Contains(team.Code) || result.Values.Contains(team)) { errors.Add($"decisions.deactivateTeams: '{code}' is a workbook category."); continue; }
            if (!team.IsActive) continue;
            team.IsActive = false; team.UpdatedAt = now;
            report.Teams.Add(Note(team, "Deactivated", $"{team.Name} ({team.Code})"));
            audit.Add(Audit("Teams", team.Name, "UPDATE", $"Deactivated team '{team.Name}' ({team.Code}): not a category of the workbook", "Active: True", "Active: False", now));
            foreach (var rule in rules.Where(r => r.TargetDepartmentId == team.Id && r.IsActive))
                DeactivateRule(rule, $"its team '{team.Name}' is deactivated", report, audit, now);
        }

        foreach (var name in decisions.DeactivateRoutingRules)
        {
            var rule = rules.FirstOrDefault(r => Same(r.Name, name));
            if (rule == null) { errors.Add($"decisions.deactivateRoutingRules: no routing rule is called '{name}'."); continue; }
            if (rule.IsActive) DeactivateRule(rule, "approved in the reconciliation decisions", report, audit, now);
        }

        // Nothing outside the workbook may stay active without a decision.
        var kept = result.Values.ToHashSet();
        foreach (var extra in departments.Where(d => d.IsActive && !kept.Contains(d)))
            errors.Add($"Team '{extra.Name}' ({extra.Code}) is active but is not a workbook category: map it in decisions.renameTeams or list it in decisions.deactivateTeams.");
        foreach (var rule in rules.Where(r => r.IsActive && departments.FirstOrDefault(d => d.Id == r.TargetDepartmentId) is { IsActive: false }))
            DeactivateRule(rule, "its team is inactive", report, audit, now);

        // Already-inactive teams outside the workbook stay archived (history), reported for completeness.
        foreach (var archived in departments.Where(d => !d.IsActive && !kept.Contains(d) && report.Teams.All(t => t.Code != d.Code)))
            report.Teams.Add(Note(archived, "Unchanged (archived)", null));

        if (errors.Count > 0) throw new InvalidOperationException("The team reconciliation cannot be completed:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        await _context.SaveChangesAsync(ct);
        return result;
    }

    private static void DeactivateRule(RoutingRule rule, string why, Report report, List<CaseEvent> audit, DateTime now)
    {
        rule.IsActive = false; rule.UpdatedAt = now;
        report.RoutingRules.Add(new RuleChange(rule.Name, "Deactivated", why, null));
        audit.Add(Audit("Routing", rule.Name, "UPDATE", $"Deactivated routing rule '{rule.Name}': {why}", "Active: True", "Active: False", now));
    }

    /// <summary>Routing rules name their team in the conditions ("Department": name or code): they follow the rename.</summary>
    private static void RenameInRoutingRules(List<RoutingRule> rules, string oldName, string oldCode, string newName, Report report, List<CaseEvent> audit, DateTime now)
    {
        foreach (var rule in rules)
        {
            if (JsonNode.Parse(rule.ConditionsJson) is not JsonObject conditions) continue;
            var key = conditions.Select(p => p.Key).FirstOrDefault(k => k.Equals("Department", StringComparison.OrdinalIgnoreCase));
            if (key == null || conditions[key] is not JsonValue v || !v.TryGetValue<string>(out var value)) continue;
            if (!Same(value, oldName) && !Same(value, oldCode)) continue;

            var before = rule.ConditionsJson;
            conditions[key] = newName;
            rule.ConditionsJson = conditions.ToJsonString(ReadableJson);
            rule.UpdatedAt = now;
            report.RoutingRules.Add(new RuleChange(rule.Name, "Condition renamed", before, rule.ConditionsJson));
            audit.Add(Audit("Routing", rule.Name, "UPDATE", $"Routing rule '{rule.Name}' follows the team rename '{oldName}' → '{newName}'", before, rule.ConditionsJson, now));
        }
    }

    /// <summary>Sub-categories: exactly the workbook rows are active, each under its category's team with its default priority.</summary>
    private async Task ReconcileSubCategoriesAsync(Workbook wb, Dictionary<string, Department> teams, Report report, List<CaseEvent> audit, DateTime now, CancellationToken ct)
    {
        var all = await _context.DepartmentSubCategories.ToListAsync(ct);
        var mappings = await _context.PriorityCategoryMappings.ToListAsync(ct);
        var priorities = await _context.PrioritySlaRules.ToListAsync(ct);
        var errors = new List<string>();
        var keep = new HashSet<Guid>();

        foreach (var group in wb.Rows.GroupBy(r => r.CategoryId, StringComparer.OrdinalIgnoreCase))
        {
            var team = teams[group.Key];
            var order = 0;
            foreach (var row in group.OrderBy(r => r.Order))
            {
                order++;
                var rule = priorities.FirstOrDefault(p => Same(p.Priority, row.DefaultPriority));
                if (rule == null) { errors.Add($"{row.SubCategoryId}: priority '{row.DefaultPriority}' is not configured."); continue; }

                var sub = all.FirstOrDefault(s => Same(s.Code, row.SubCategoryId));
                string action;
                string? before = null;
                if (sub != null && sub.DepartmentId != team.Id)
                {
                    errors.Add($"{row.SubCategoryId} exists under another team; move it by hand first (its cases refer to it by name within that team).");
                    continue;
                }
                if (sub == null)
                {
                    // A record of the same name in the same team is the same sub-category: it takes the workbook ID instead of a duplicate.
                    sub = all.FirstOrDefault(s => s.DepartmentId == team.Id && Same(s.Name, row.SubCategory) && !keep.Contains(s.Id));
                    if (sub != null) before = $"code {sub.Code}";
                }

                if (sub == null)
                {
                    sub = new DepartmentSubCategory
                    {
                        Id = Guid.NewGuid(), DepartmentId = team.Id, Name = row.SubCategory, Code = row.SubCategoryId,
                        Description = NullIfEmpty(row.Description), DisplayOrder = order, IsActive = true, CreatedAt = now,
                    };
                    _context.DepartmentSubCategories.Add(sub);
                    all.Add(sub);
                    action = "Created";
                }
                else
                {
                    var was = $"{sub.Name} [{sub.Code}] order {sub.DisplayOrder}{(sub.IsActive ? "" : ", inactive")}{(Same(sub.Description, row.Description) ? "" : ", other description")}";
                    var changed = !string.Equals(sub.Name, row.SubCategory, StringComparison.Ordinal) || !string.Equals(sub.Code, row.SubCategoryId, StringComparison.Ordinal)
                                  || !string.Equals(sub.Description ?? "", row.Description, StringComparison.Ordinal) || sub.DisplayOrder != order || !sub.IsActive;
                    if (!string.Equals(sub.Name, row.SubCategory, StringComparison.Ordinal))
                    {
                        // Cases of this team refer to the sub-category by name: they follow the rename.
                        var oldName = sub.Name;
                        await _context.Cases.Where(c => c.DepartmentId == team.Id && c.Subcategory != null && c.Subcategory.ToLower() == oldName.ToLower())
                            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Subcategory, row.SubCategory), ct);
                    }
                    sub.Name = row.SubCategory; sub.Code = row.SubCategoryId; sub.Description = NullIfEmpty(row.Description);
                    sub.DisplayOrder = order; sub.IsActive = true;
                    if (changed) sub.UpdatedAt = now;
                    action = before != null ? "Adopted" : changed ? "Updated" : "Unchanged";
                    before = changed ? (before != null ? before + "; " : "") + was : before;
                }
                keep.Add(sub.Id);

                var mapping = mappings.FirstOrDefault(m => m.DepartmentSubCategoryId == sub.Id);
                if (mapping == null)
                {
                    mapping = new PriorityCategoryMapping { Id = Guid.NewGuid(), DepartmentSubCategoryId = sub.Id, PrioritySlaRuleId = rule.Id, CreatedAt = now };
                    _context.PriorityCategoryMappings.Add(mapping);
                    mappings.Add(mapping);
                    if (action == "Unchanged") action = "Updated";
                }
                else if (mapping.PrioritySlaRuleId != rule.Id)
                {
                    before = (before != null ? before + "; " : "") + "priority " + priorities.First(p => p.Id == mapping.PrioritySlaRuleId).Priority;
                    mapping.PrioritySlaRuleId = rule.Id; mapping.UpdatedAt = now;
                    if (action == "Unchanged") action = "Updated";
                }

                report.SubCategories.Add(new SubCategoryChange(row.SubCategoryId, team.Name, row.SubCategory, rule.Priority, action, before));
                if (action != "Unchanged")
                    audit.Add(Audit("Configurable Settings", $"{team.Name} / {row.SubCategory}", action == "Created" ? "CREATE" : "UPDATE",
                        $"{action} sub-category {row.SubCategoryId} '{row.SubCategory}' in '{team.Name}', default priority {rule.Priority}", before, $"Code: {row.SubCategoryId}, Priority: {rule.Priority}", now));
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException("The sub-category reconciliation cannot be completed:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));

        // Everything else is retired: deactivated (never deleted), so cases keep their value and history stays readable.
        var teamNames = await _context.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var usage = await _context.Cases.AsNoTracking().Where(c => c.Subcategory != null)
            .GroupBy(c => new { c.DepartmentId, Name = c.Subcategory!.ToLower() })
            .Select(g => new { g.Key.DepartmentId, g.Key.Name, All = g.Count(), Open = g.Count(c => c.Status != CaseStatus.Resolved && c.Status != CaseStatus.Closed && c.Status != CaseStatus.Cancelled) })
            .ToListAsync(ct);
        foreach (var old in all.Where(s => s.IsActive && !keep.Contains(s.Id)).OrderBy(s => teamNames[s.DepartmentId]).ThenBy(s => s.DisplayOrder))
        {
            old.IsActive = false; old.UpdatedAt = now;
            var used = usage.FirstOrDefault(u => u.DepartmentId == old.DepartmentId && u.Name == old.Name.ToLower());
            report.RetiredSubCategories.Add(new RetiredSubCategory(teamNames[old.DepartmentId], old.Name, old.Code, used?.All ?? 0, used?.Open ?? 0));
            audit.Add(Audit("Configurable Settings", $"{teamNames[old.DepartmentId]} / {old.Name}", "UPDATE",
                $"Deactivated sub-category '{old.Name}' ({old.Code}) in '{teamNames[old.DepartmentId]}': not in the workbook; {used?.All ?? 0} case(s) keep it", "Active: True", "Active: False", now));
        }
        await _context.SaveChangesAsync(ct);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>SLA matrix: workbook response → first response, resolution → external (customer-facing) resolution.</summary>
    private async Task ReconcileSlaAsync(Workbook wb, Decisions decisions, Report report, List<CaseEvent> audit, DateTime now, CancellationToken ct)
    {
        var rules = await _context.PrioritySlaRules.ToListAsync(ct);
        var errors = new List<string>();
        var sameAsExternal = decisions.InternalResolution.Equals("sameAsExternal", StringComparison.OrdinalIgnoreCase);

        foreach (var row in wb.Sla)
        {
            var rule = rules.FirstOrDefault(r => Same(r.Priority, row.Priority));
            if (rule == null) { errors.Add($"Priority '{row.Priority}' is not configured: add it on the Priority SLA Matrix first (with its internal target)."); continue; }

            var (frValue, frUnit) = Target(row.ResponseHours);
            var (extValue, extUnit) = Target(row.ResolutionHours);
            var (intValue, intUnit) = sameAsExternal ? (extValue, extUnit) : (rule.InternalResolutionValue, rule.InternalResolutionUnit);
            int Minutes(int v, string u) => u == "Hours" ? v * 60 : v;

            var before = Describe(rule.FirstResponseValue, rule.FirstResponseUnit, rule.InternalResolutionValue, rule.InternalResolutionUnit, rule.ExternalResolutionValue, rule.ExternalResolutionUnit, rule.IsActive);
            var changedTargets = rule.FirstResponseMinutes != Minutes(frValue, frUnit) || rule.ExternalResolutionMinutes != Minutes(extValue, extUnit) || rule.InternalResolutionMinutes != Minutes(intValue, intUnit);
            var changedUnits = rule.FirstResponseUnit != frUnit || rule.ExternalResolutionUnit != extUnit || rule.FirstResponseValue != frValue || rule.ExternalResolutionValue != extValue;

            if (changedTargets) rule.Version += 1;   // cases snapshot the version they were created with: it moves only when a target changes
            rule.FirstResponseValue = frValue; rule.FirstResponseUnit = frUnit; rule.FirstResponseMinutes = Minutes(frValue, frUnit);
            rule.ExternalResolutionValue = extValue; rule.ExternalResolutionUnit = extUnit; rule.ExternalResolutionMinutes = Minutes(extValue, extUnit);
            rule.InternalResolutionValue = intValue; rule.InternalResolutionUnit = intUnit; rule.InternalResolutionMinutes = Minutes(intValue, intUnit);
            var reactivated = !rule.IsActive;
            rule.IsActive = true;
            if (changedTargets || changedUnits || reactivated) rule.UpdatedAt = now;

            var after = Describe(frValue, frUnit, intValue, intUnit, extValue, extUnit, true);
            var action = changedTargets || changedUnits || reactivated ? "Updated" : "Unchanged";
            report.Priorities.Add(new PriorityChange(rule.Priority, action, before, after, rule.Version));
            if (action == "Updated")
                audit.Add(Audit("SlaEscalation", $"Priority {rule.Priority}", "UPDATE_CONFIG", $"Priority '{rule.Priority}' SLA targets set from the workbook SLA matrix", before, after, now));
            if (rule.InternalResolutionMinutes > rule.ExternalResolutionMinutes)
                report.Notes.Add($"{rule.Priority}: the internal resolution target ({intValue} {intUnit}) is longer than the external one ({extValue} {extUnit}).");
        }

        foreach (var extra in rules.Where(r => r.IsActive && !wb.Sla.Any(s => Same(s.Priority, r.Priority))))
            errors.Add($"Priority '{extra.Priority}' is active but not in the workbook SLA matrix: deactivate it on the Priority SLA Matrix first.");
        if (errors.Count > 0) throw new InvalidOperationException("The SLA reconciliation cannot be completed:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        await _context.SaveChangesAsync(ct);

        report.Notes.Add("SLA targets are measured in business time (the configured business hours and holidays); the workbook states calendar hours. Open cases keep the targets they were created with.");
    }

    /// <summary>Whole hours stay hours; fractions (e.g. 0.5 h) become minutes.</summary>
    private static (int Value, string Unit) Target(decimal hours) =>
        hours == decimal.Truncate(hours) ? ((int)hours, "Hours") : ((int)Math.Round(hours * 60m), "Minutes");

    private static string Describe(int fr, string fru, int ir, string iru, int er, string eru, bool active) =>
        $"first response {fr} {fru.ToLowerInvariant()}, internal {ir} {iru.ToLowerInvariant()}, external {er} {eru.ToLowerInvariant()}{(active ? "" : ", inactive")}";

    // ------------------------------------------------------------------------------------------------- text

    public static string ToText(Report r)
    {
        var lines = new List<string>
        {
            $"WORKBOOK RECONCILIATION — {(r.Applied ? "APPLIED" : "DRY RUN (nothing was written)")} — {r.GeneratedAt:u}",
            "",
            $"Counts            before -> after",
            $"  departments     {r.Before.Departments,6} -> {r.After.Departments} (active {r.Before.ActiveDepartments} -> {r.After.ActiveDepartments})",
            $"  sub-categories  {r.Before.SubCategories,6} -> {r.After.SubCategories} (active {r.Before.ActiveSubCategories} -> {r.After.ActiveSubCategories}; active with a priority {r.Before.ActiveMappedSubCategories} -> {r.After.ActiveMappedSubCategories})",
            $"  priorities      active {r.Before.ActivePriorities} -> {r.After.ActivePriorities}",
            "",
            "1. Teams (= departments):",
        };
        lines.AddRange(r.Teams.Select(t => $"   {t.Action,-21} {t.Code,-4} {t.Name}{(t.Before != null ? $"  (was {t.Before})" : "")} — members {t.Members}, cases {t.Cases} ({t.OpenCases} open)"));
        lines.Add("");
        lines.Add($"2. Routing rules: {r.RoutingRules.Count} change(s)");
        lines.AddRange(r.RoutingRules.Select(c => $"   {c.Action}: {c.Rule}{(c.After != null ? $"  {c.Before} -> {c.After}" : $" ({c.Before})")}"));
        lines.Add("");
        lines.Add($"3. Workbook sub-categories: {r.SubCategories.Count} — " + string.Join(", ", r.SubCategories.GroupBy(s => s.Action).Select(g => $"{g.Key} {g.Count()}")));
        lines.AddRange(r.SubCategories.Select(s => $"   {s.Action,-9} {s.Code,-6} {s.Team} / {s.Name} -> {s.Priority}{(s.Before != null ? $"  (was {s.Before})" : "")}"));
        lines.Add("");
        lines.Add($"4. Retired (deactivated, kept for history): {r.RetiredSubCategories.Count}");
        lines.AddRange(r.RetiredSubCategories.Select(s => $"   {s.Team} / {s.Name} ({s.Code}) — {s.Cases} case(s) keep it, {s.OpenCases} open"));
        lines.Add("");
        lines.Add("5. SLA matrix:");
        lines.AddRange(r.Priorities.Select(p => $"   {p.Action,-9} {p.Priority,-9} {p.Before}  ->  {p.After} (version {p.Version})"));
        if (r.Notes.Count > 0) { lines.Add(""); lines.Add("Notes:"); lines.AddRange(r.Notes.Select(n => "   " + n)); }
        return string.Join(Environment.NewLine, lines);
    }

    public static Decisions ReadDecisions(string path)
    {
        var d = JsonSerializer.Deserialize<Decisions>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? throw new InvalidOperationException($"{path} is empty.");
        d.RenameTeams = new Dictionary<string, string>(d.RenameTeams, StringComparer.OrdinalIgnoreCase);   // the serializer drops the comparer
        return d;
    }
}
