namespace CaseManagement.Api.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services.Strategies;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

public class RoutingEngineService : IRoutingEngineService
{
    private readonly AppDbContext _context;
    private readonly IEnumerable<IAssignmentStrategy> _strategies;
    private readonly IAgentPoolService _pool;
    private readonly ILogger<RoutingEngineService> _logger;
    private readonly IConfigCache? _cache;

    public RoutingEngineService(AppDbContext context, IEnumerable<IAssignmentStrategy> strategies, IAgentPoolService pool, ILogger<RoutingEngineService>? logger = null, IConfigCache? cache = null)
    {
        _cache = cache;
        _context = context;
        _logger = logger ?? NullLogger<RoutingEngineService>.Instance;
        _strategies = strategies;
        _pool = pool;
    }

    public async Task<IEnumerable<RoutingRuleDto>> GetRulesAsync(CancellationToken ct = default)
    {
        var rules = await _context.RoutingRules
            .AsNoTracking()
            .Include(r => r.TargetDepartment)
            .OrderBy(r => r.EvaluationOrder)
            .ToListAsync(ct);

        return rules.Select(r =>
        {
            RuleConditionsDto conds;
            try
            {
                conds = JsonSerializer.Deserialize<RuleConditionsDto>(r.ConditionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RuleConditionsDto();
            }
            catch
            {
                conds = new RuleConditionsDto();
            }

            return new RoutingRuleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                EvaluationOrder = r.EvaluationOrder,
                IsActive = r.IsActive,
                ConditionsJson = r.ConditionsJson,
                Conditions = conds,
                TargetDepartmentId = r.TargetDepartmentId,
                TargetDepartmentName = r.TargetDepartment?.Name ?? "Unassigned Team",
                ActionDescription = r.ActionDescription,
                CreatedAt = r.CreatedAt
            };
        });
    }

    public async Task<RoutingRuleDto?> GetRuleByIdAsync(Guid id, CancellationToken ct = default)
    {
        var rules = await GetRulesAsync(ct);
        return rules.FirstOrDefault(r => r.Id == id);
    }

    public async Task<RoutingRuleDto> CreateRuleAsync(CreateRoutingRuleDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new InvalidOperationException("Rule name is required.");

        await ValidateRuleAsync(dto.Conditions, dto.TargetDepartmentId, ct);
        var count = await _context.RoutingRules.CountAsync(ct);
        var order = dto.EvaluationOrder.HasValue && dto.EvaluationOrder.Value > 0 ? dto.EvaluationOrder.Value : count + 1;

        var condsJson = dto.Conditions != null ? JsonSerializer.Serialize(dto.Conditions) : "{}";

        var rule = new RoutingRule
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim() ?? "",
            EvaluationOrder = order,
            IsActive = dto.IsActive,
            ConditionsJson = condsJson,
            TargetDepartmentId = dto.TargetDepartmentId,
            ActionDescription = dto.ActionDescription?.Trim() ?? $"Route to team",
            CreatedAt = DateTime.UtcNow
        };

        _context.RoutingRules.Add(rule);
        await RecordAuditLogAsync("RULE_CREATED", rule.Name, $"Created routing rule '{rule.Name}'", null, condsJson, actingUserId);

        await _context.SaveChangesAsync(ct);
        return (await GetRuleByIdAsync(rule.Id, ct))!;
    }

    public async Task<RoutingRuleDto> UpdateRuleAsync(Guid id, UpdateRoutingRuleDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        var rule = await _context.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) throw new KeyNotFoundException("Routing rule not found.");

        var oldState = JsonSerializer.Serialize(new { rule.Name, rule.IsActive, rule.EvaluationOrder, rule.ConditionsJson });
        await ValidateRuleAsync(dto.Conditions, dto.TargetDepartmentId, ct);

        if (!string.IsNullOrWhiteSpace(dto.Name)) rule.Name = dto.Name.Trim();
        if (dto.Description != null) rule.Description = dto.Description.Trim();
        if (dto.EvaluationOrder.HasValue) rule.EvaluationOrder = dto.EvaluationOrder.Value;
        if (dto.IsActive.HasValue) rule.IsActive = dto.IsActive.Value;
        if (dto.TargetDepartmentId.HasValue) rule.TargetDepartmentId = dto.TargetDepartmentId.Value;
        if (dto.ActionDescription != null) rule.ActionDescription = dto.ActionDescription.Trim();
        if (dto.Conditions != null) rule.ConditionsJson = JsonSerializer.Serialize(dto.Conditions);

        rule.UpdatedAt = DateTime.UtcNow;

        await RecordAuditLogAsync("RULE_UPDATED", rule.Name, $"Updated routing rule '{rule.Name}'", oldState, rule.ConditionsJson, actingUserId);
        await _context.SaveChangesAsync(ct);

        return (await GetRuleByIdAsync(rule.Id, ct))!;
    }

    public async Task<RoutingRuleDto> ToggleRuleAsync(Guid id, Guid actingUserId, CancellationToken ct = default)
    {
        var rule = await _context.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) throw new KeyNotFoundException("Routing rule not found.");

        rule.IsActive = !rule.IsActive;
        rule.UpdatedAt = DateTime.UtcNow;

        await RecordAuditLogAsync("RULE_TOGGLED", rule.Name, $"Toggled routing rule '{rule.Name}' to {(rule.IsActive ? "ACTIVE" : "INACTIVE")}", (!rule.IsActive).ToString(), rule.IsActive.ToString(), actingUserId);
        await _context.SaveChangesAsync(ct);

        return (await GetRuleByIdAsync(rule.Id, ct))!;
    }

    public async Task DeleteRuleAsync(Guid id, Guid actingUserId, CancellationToken ct = default)
    {
        var rule = await _context.RoutingRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null) throw new KeyNotFoundException("Routing rule not found.");

        rule.IsActive = false;
        rule.UpdatedAt = DateTime.UtcNow;

        await RecordAuditLogAsync("RULE_DEACTIVATED", rule.Name, $"Deactivated routing rule '{rule.Name}' (soft-delete to preserve history)", "Active", "Inactive", actingUserId);
        await _context.SaveChangesAsync(ct);
    }

    public async Task ReorderRulesAsync(ReorderRulesDto dto, Guid actingUserId, CancellationToken ct = default)
    {
        if (dto.RuleIds == null || !dto.RuleIds.Any()) return;

        var allRules = await _context.RoutingRules.ToListAsync(ct);
        int currentOrder = 1;

        foreach (var ruleId in dto.RuleIds)
        {
            var match = allRules.FirstOrDefault(r => r.Id == ruleId);
            if (match != null)
            {
                match.EvaluationOrder = currentOrder++;
                match.UpdatedAt = DateTime.UtcNow;
            }
        }

        await RecordAuditLogAsync("RULES_REORDERED", "RoutingRules", "Reordered top-down evaluation sequence", null, null, actingUserId);
        await _context.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------------------------ assignment settings

    private static readonly string[] Algorithms = { "RoundRobin", "SkillBased", "LeastOccupancy" };

    private static AssignmentConfigDto ToDto(AssignmentConfiguration c, bool isOverride) => new()
    {
        DepartmentId = c.DepartmentId,
        Algorithm = c.Algorithm,
        MaxConcurrentCapacity = c.MaxConcurrentCapacity,
        IsTeamOverride = isOverride,
        UpdatedAt = c.UpdatedAt ?? c.CreatedAt
    };

    private Task<AssignmentConfiguration> ResolveConfigAsync(Guid? departmentId, CancellationToken ct) =>
        AssignmentSettings.ResolveAsync(_context, _cache, departmentId, ct);

    private Task<T> Cached<T>(string key, Func<Task<T>> load) => _cache == null ? load() : _cache.GetOrCreateAsync(key, load);

    public async Task<AssignmentConfigDto> GetAssignmentConfigAsync(Guid? departmentId = null, CancellationToken ct = default)
    {
        var config = await ResolveConfigAsync(departmentId, ct);
        return ToDto(config, departmentId.HasValue && config.DepartmentId == departmentId);
    }

    public async Task<AssignmentConfigDto> UpdateAssignmentConfigAsync(UpdateAssignmentConfigDto dto, Guid actingUserId, Guid? departmentId = null, CancellationToken ct = default)
    {
        var algo = Algorithms.FirstOrDefault(a => string.Equals(a, dto.Algorithm, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"'{dto.Algorithm}' is not an assignment algorithm. Choose one of: {string.Join(", ", Algorithms)}.");
        if (dto.MaxConcurrentCapacity is < 1 or > 500)
            throw new InvalidOperationException("Capacity must be between 1 and 500 open cases per agent.");

        string scope = "all teams";
        if (departmentId.HasValue)
        {
            scope = (await _context.Departments.AsNoTracking().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct))
                    ?? throw new KeyNotFoundException("Team not found.");
        }

        var config = await _context.AssignmentConfigurations.FirstOrDefaultAsync(c => c.DepartmentId == departmentId, ct);
        if (config == null)
        {
            // A team's first override starts from the current global settings; the global row is never created here.
            var basis = departmentId.HasValue ? await ResolveConfigAsync(null, ct) : null;
            config = new AssignmentConfiguration
            {
                Id = Guid.NewGuid(),
                DepartmentId = departmentId,
                Algorithm = algo,
                MaxConcurrentCapacity = dto.MaxConcurrentCapacity ?? basis?.MaxConcurrentCapacity
                    ?? throw new InvalidOperationException("Capacity is required."),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _context.AssignmentConfigurations.Add(config);
        }
        else
        {
            config.Algorithm = algo;
            if (dto.MaxConcurrentCapacity.HasValue) config.MaxConcurrentCapacity = dto.MaxConcurrentCapacity.Value;
            config.IsActive = true;
            config.UpdatedAt = DateTime.UtcNow;
        }

        await RecordAuditLogAsync("CONFIG_UPDATED", "AssignmentConfiguration", $"Assignment for {scope}: '{algo}', capacity {config.MaxConcurrentCapacity}.", null, algo, actingUserId);
        await _context.SaveChangesAsync(ct);

        return ToDto(config, departmentId.HasValue);
    }

    public async Task ClearTeamAssignmentConfigAsync(Guid departmentId, Guid actingUserId, CancellationToken ct = default)
    {
        var own = await _context.AssignmentConfigurations.FirstOrDefaultAsync(c => c.DepartmentId == departmentId, ct);
        if (own == null) return;
        _context.AssignmentConfigurations.Remove(own);
        await RecordAuditLogAsync("CONFIG_UPDATED", "AssignmentConfiguration", "A team now follows the global assignment settings.", own.Algorithm, null, actingUserId);
        await _context.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------------------------ vocabulary & validation

    public async Task<RoutingVocabularyDto> GetVocabularyAsync(CancellationToken ct = default)
    {
        var channels = await _context.LookupValues.AsNoTracking()
            .Where(v => v.LookupType.Code == "SOURCE_CHANNEL" && v.IsActive).OrderBy(v => v.DisplayOrder).Select(v => v.Value).ToListAsync(ct);

        return new RoutingVocabularyDto
        {
            Departments = await _context.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
                .Select(d => new NamedOptionDto { Id = d.Id, Name = d.Name }).ToListAsync(ct),
            SubCategories = await _context.DepartmentSubCategories.AsNoTracking().Where(s => s.IsActive).Select(s => s.Name).Distinct().OrderBy(n => n).ToListAsync(ct),
            CaseTypes = await _context.CaseTypeConfigs.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(ct),
            Priorities = await _context.PrioritySlaRules.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.DisplayOrder).Select(r => r.Priority).ToListAsync(ct),
            Channels = channels,
            CustomerSegments = await _context.Customers.AsNoTracking().Where(c => c.CustomerSegment != null && c.CustomerSegment != "")
                .Select(c => c.CustomerSegment!).Distinct().OrderBy(n => n).ToListAsync(ct),
            Algorithms = Algorithms.ToList(),
        };
    }

    /// <summary>A rule may only mention things that exist, and only route to an active team.</summary>
    private async Task ValidateRuleAsync(RuleConditionsDto? conds, Guid? targetDepartmentId, CancellationToken ct)
    {
        if (targetDepartmentId.HasValue)
        {
            var target = await _context.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == targetDepartmentId.Value, ct)
                         ?? throw new InvalidOperationException("The destination team does not exist.");
            if (!target.IsActive) throw new InvalidOperationException($"'{target.Name}' is inactive, so a rule cannot route to it.");
        }
        if (conds == null) return;

        if (!string.IsNullOrWhiteSpace(conds.MatchType) && !new[] { "ALL", "ANY" }.Contains(conds.MatchType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Match type must be ALL or ANY.");

        var vocab = await GetVocabularyAsync(ct);
        void Check(string label, string? value, IEnumerable<string> valid)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!valid.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{value}' is not a valid {label}. Choose one of: {string.Join(", ", valid)}.");
        }
        Check("team", conds.Department, vocab.Departments.Select(d => d.Name));
        Check("sub-category", conds.Category, vocab.SubCategories);
        Check("case type", conds.CaseType, vocab.CaseTypes);
        Check("priority", conds.Priority, vocab.Priorities);
        Check("channel", conds.Channel, vocab.Channels);
    }

    // ------------------------------------------------------------------------------------ routing

    public async Task<RoutingDecisionResult> RouteAndAssignCaseAsync(Case newCase, Customer? customer, CancellationToken ct = default)
    {
        var result = new RoutingDecisionResult();

        // STAGE 1 — the first active rule (in order) that matches decides the team.
        var departments = await Cached("routing:departments", () => _context.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, ct));
        var activeRules = await Cached("routing:active-rules", () => _context.RoutingRules.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.EvaluationOrder).ToListAsync(ct));

        RoutingRule? matchedRule = activeRules.FirstOrDefault(r => EvaluateRuleMatches(r, newCase, customer, departments));

        Department target;
        if (matchedRule != null && departments.TryGetValue(matchedRule.TargetDepartmentId, out var ruleTarget) && ruleTarget.IsActive)
        {
            target = ruleTarget;
            result.MatchedRuleId = matchedRule.Id;
            result.MatchedRuleName = matchedRule.Name;
        }
        else
        {
            // No (usable) rule: the case stays with the team it was opened for. There is no hidden "default team".
            target = departments.TryGetValue(newCase.DepartmentId, out var own) && own.IsActive
                ? own
                : throw new InvalidOperationException("The team this case was opened for is missing or inactive, and no routing rule applies. Activate the team or add a routing rule.");
            result.MatchedRuleName = matchedRule != null ? $"{matchedRule.Name} (destination team inactive — kept with the original team)" : "No rule matched — kept with the original team";
        }

        result.TargetDepartmentId = target.Id;
        result.TargetDepartmentName = target.Name;
        newCase.DepartmentId = target.Id;

        var ruleText = matchedRule != null && result.MatchedRuleId != null ? $"rule '{matchedRule.Name}'" : "no matching rule";
        await AssignWithinTeamAsync(newCase, customer, target, result, ruleText, ct);
        return result;
    }

    public async Task<RoutingDecisionResult> AssignWithinTeamAsync(Case c, Customer? customer, Guid departmentId, string via, CancellationToken ct = default)
    {
        var target = await _context.Departments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == departmentId, ct)
                     ?? throw new KeyNotFoundException("Target department not found.");
        if (!target.IsActive)
            throw new AssignmentNotAllowedException(nameof(IneligibilityReason.TeamInactive), $"'{target.Name}' is inactive and cannot receive cases.");

        var result = new RoutingDecisionResult { TargetDepartmentId = target.Id, TargetDepartmentName = target.Name };
        await AssignWithinTeamAsync(c, customer, target, result, via, ct);
        return result;
    }

    /// <summary>
    /// STAGE 2 — pick an agent of <paramref name="target"/> with its configured algorithm, from the eligible pool; if nobody can take
    /// the case its lead holds it (when the team has an active lead), otherwise the owner is left unchanged and <see cref="RoutingDecisionResult.AssignedUserId"/> stays null.
    /// </summary>
    private async Task AssignWithinTeamAsync(Case newCase, Customer? customer, Department target, RoutingDecisionResult result, string ruleText, CancellationToken ct)
    {
        // Assignments to one team are serialised so two simultaneous cases cannot both see the same
        // "least loaded" agent or the same round-robin position; the lock lasts until the surrounding transaction commits.
        await LockTeamAsync(target.Id, ct);

        var config = await ResolveConfigAsync(target.Id, ct);
        result.AlgorithmUsed = config.Algorithm;

        var pool = await _pool.GetAsync(target.Id, config.MaxConcurrentCapacity, ct);
        var strategy = _strategies.FirstOrDefault(s => string.Equals(s.AlgorithmName, config.Algorithm, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"The assignment algorithm '{config.Algorithm}' is not available.");

        var choice = pool.Eligible.Count > 0
            ? await strategy.SelectAsync(new AssignmentRequest(target.Id, newCase, customer, pool.Eligible, config.MaxConcurrentCapacity), ct)
            : null;

        if (choice != null)
        {
            newCase.OwnerId = choice.User.Id;
            result.AssignedUserId = choice.User.Id;
            result.AssignedUserName = choice.User.Name;
            result.RoutingLogMessage = $"Case routed to '{target.Name}' via {ruleText} and assigned to '{choice.User.Name}' via {config.Algorithm}" +
                                       (string.IsNullOrWhiteSpace(choice.Note) ? "." : $" ({choice.Note}).");
            return;
        }

        // Nobody can take it: say why, and let the team lead hold it so it is not lost. The lead is told by the caller.
        result.HeldReason = pool.Describe(target.Name);
        var lead = target.OwnerId.HasValue
            ? await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == target.OwnerId.Value && u.IsActive, ct)
            : null;
        if (lead != null)
        {
            newCase.OwnerId = lead.Id;
            result.HeldByUserId = lead.Id;
            result.AssignedUserId = lead.Id;
            result.AssignedUserName = lead.Name;
        }
        result.RoutingLogMessage = $"Case routed to '{target.Name}' via {ruleText}. Pending agent assignment: {result.HeldReason}" +
                                   (lead != null ? $" Held by the team lead, {lead.Name}." : " No team lead is set, so it stays with its creator.");
    }

    private Task LockTeamAsync(Guid departmentId, CancellationToken ct) => TeamAssignmentLock.AcquireAsync(_context, departmentId, ct);

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool EvaluateRuleMatches(RoutingRule rule, Case c, Customer? cust, IReadOnlyDictionary<Guid, Department> departments)
    {
        RuleConditionsDto conds;
        try
        {
            conds = JsonSerializer.Deserialize<RuleConditionsDto>(rule.ConditionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RuleConditionsDto();
        }
        catch
        {
            return false;   // an unreadable rule never matches (it must not silently become "match everything")
        }

        // Every condition is an EXACT (case-insensitive) comparison with a configured value — never "contains".
        var matches = new List<bool>();

        if (!string.IsNullOrWhiteSpace(conds.Department))
        {
            departments.TryGetValue(c.DepartmentId, out var dept);
            matches.Add(dept != null && (Same(dept.Name, conds.Department) || Same(dept.Code, conds.Department)));
        }
        if (!string.IsNullOrWhiteSpace(conds.Category)) matches.Add(Same(c.Subcategory, conds.Category));
        if (!string.IsNullOrWhiteSpace(conds.CaseType)) matches.Add(Same(c.CaseType, conds.CaseType));
        if (!string.IsNullOrWhiteSpace(conds.Priority)) matches.Add(Same(c.Severity, conds.Priority));
        if (!string.IsNullOrWhiteSpace(conds.CustomerSegment)) matches.Add(Same(cust?.CustomerSegment, conds.CustomerSegment));
        if (!string.IsNullOrWhiteSpace(conds.Channel)) matches.Add(Same(string.IsNullOrWhiteSpace(c.SourceChannel) ? c.CommunicationChannel : c.SourceChannel, conds.Channel));

        if (matches.Count == 0) return true;   // a rule with no conditions is a deliberate catch-all

        return string.Equals(conds.MatchType, "ANY", StringComparison.OrdinalIgnoreCase) ? matches.Any(m => m) : matches.All(m => m);
    }

    private async Task RecordAuditLogAsync(string actionType, string entityName, string description, string? oldValue, string? newValue, Guid userId)
    {
        try
        {
            var audit = new CaseEvent
            {
                Id = Guid.NewGuid(),
                CaseId = null,
                EventType = EventType.Other,
                Message = description,
                CreatedAt = DateTime.UtcNow,
                UserId = userId != Guid.Empty ? userId : Guid.Empty,
                Module = "RoutingEngine",
                EntityName = entityName,
                ActionType = actionType,
                OldValue = oldValue,
                NewValue = newValue
            };

            _context.CaseEvents.Add(audit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RoutingEngineAuditLog Error");
        }
    }
}
