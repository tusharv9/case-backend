namespace CaseManagement.Api.Repositories;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.DTOs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

public class CaseRepository : ICaseRepository
{
    private readonly AppDbContext _context;

    public CaseRepository(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Loads a case for mutation by <see cref="Services.CaseService"/> — the only caller.
    ///
    /// The graph is deliberately limited to what those write paths read: Owner (named in the
    /// "already handling this case" message) and the three collections whose membership is
    /// checked before adding a coworker or a link. Read-only screens use the projected
    /// <see cref="GetCaseDetailAsync"/> instead.
    ///
    /// The wider graph this replaced pulled in Events (unbounded per case) with their Users,
    /// Subcases, ParentCase, Customer and Department, none of which the write paths touch. That
    /// cost twice: every row of the Events collection multiplied against every Participant,
    /// LinkedCase and ChildRelation in one un-split query, and DbSet.Update then marked the whole
    /// reachable graph Modified, so a single status change re-wrote every related row.
    /// </summary>
    public async Task<Case?> GetByIdAsync(Guid id)
    {
        return await _context.Cases
            .Include(c => c.Owner)
            .Include(c => c.Participants)
            .Include(c => c.LinkedCases)
            .Include(c => c.ChildRelations)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<IEnumerable<Case>> GetAllAsync(Guid? departmentId = null)
    {
        var query = _context.Cases
            .Include(c => c.Department)
            .Include(c => c.Owner)
            .AsQueryable();

        if (departmentId.HasValue)
        {
            query = query.Where(c => c.DepartmentId == departmentId.Value);
        }

        return await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
    }

    public async Task<Case> AddAsync(Case newCase)
    {
        _context.Cases.Add(newCase);
        await _context.SaveChangesAsync();
        return newCase;
    }

    public async Task UpdateAsync(Case caseToUpdate)
    {
        // Cases loaded through this context are already tracked, so change detection writes only
        // the columns that changed. DbSet.Update would mark the whole loaded graph (owner,
        // participants, links) as modified and rewrite every one of those rows.
        if (_context.Entry(caseToUpdate).State == EntityState.Detached)
        {
            _context.Cases.Update(caseToUpdate);
        }
        await _context.SaveChangesAsync();
    }

    public async Task AddEventAsync(CaseEvent caseEvent)
    {
        _context.CaseEvents.Add(caseEvent);
        await _context.SaveChangesAsync();
    }

    public async Task AddParticipantAsync(CaseParticipant participant)
    {
        _context.CaseParticipants.Add(participant);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveParticipantAsync(Guid caseId, Guid userId)
    {
        var participant = await _context.CaseParticipants
            .FirstOrDefaultAsync(p => p.CaseId == caseId && p.UserId == userId);
        
        if (participant != null)
        {
            _context.CaseParticipants.Remove(participant);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<Case?> GetByCaseNumberAsync(string caseNumber)
    {
        if (string.IsNullOrWhiteSpace(caseNumber)) return null;
        string cleaned = caseNumber.Trim().ToLower();

        // Resolves a link/unlink target. Callers read only Id and CaseNumber, so no related
        // entities are loaded; the ChildRelations subquery below is a filter, not a fetch.
        return await _context.Cases
            .FirstOrDefaultAsync(c =>
                c.CaseNumber.ToLower() == cleaned ||
                c.ChildRelations.Any(cr => cr.ChildId.ToLower() == cleaned)
            );
    }

    public async Task AddLinkedCaseAsync(LinkedCase linkedCase)
    {
        _context.Set<LinkedCase>().Add(linkedCase);
        await _context.SaveChangesAsync();
    }

    public async Task RemoveLinkedCaseAsync(Guid caseId, Guid targetCaseId)
    {
        var linkedCases = await _context.Set<LinkedCase>()
            .Where(lc => (lc.CaseId == caseId && lc.TargetCaseId == targetCaseId) ||
                         (lc.CaseId == targetCaseId && lc.TargetCaseId == caseId))
            .ToListAsync();
            
        if (linkedCases.Any())
        {
            _context.Set<LinkedCase>().RemoveRange(linkedCases);
        }

        var childRelations = await _context.Set<CaseChildRelation>()
            .Where(cr => cr.RelationType == ChildRelationType.Link &&
                         ((cr.ParentCaseId == caseId && cr.LinkedCaseId == targetCaseId) ||
                          (cr.ParentCaseId == targetCaseId && cr.LinkedCaseId == caseId)))
            .ToListAsync();

        if (childRelations.Any())
        {
            _context.Set<CaseChildRelation>().RemoveRange(childRelations);
        }

        await _context.SaveChangesAsync();
    }

    public async Task<Guid?> GetDepartmentOwnerAsync(Guid departmentId)
    {
        var department = await _context.Departments.FirstOrDefaultAsync(d => d.Id == departmentId);
        if (department == null) throw new KeyNotFoundException("Department not found.");
        return department.OwnerId;
    }

    public async Task<PagedResponseDto<CaseSummaryDto>> GetPaginatedBoardCasesAsync(
        string? status = null,
        int page = 1,
        int pageSize = 30,
        Guid? departmentId = null,
        string? caseType = null,
        string? search = null,
        string? priority = null,
        string? channel = null,
        CancellationToken ct = default)
    {
        var query = _context.Cases.AsNoTracking();

        if (departmentId.HasValue)
        {
            query = query.Where(c => c.DepartmentId == departmentId.Value);
        }

        if (!string.IsNullOrWhiteSpace(caseType) && !caseType.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string norm = caseType.Trim();
            if (norm.Equals("Enquiry", StringComparison.OrdinalIgnoreCase)) norm = "Inquiry";

            bool isInquiry = norm.Equals("Inquiry", StringComparison.OrdinalIgnoreCase);
            bool isService = norm.Equals("Service", StringComparison.OrdinalIgnoreCase);
            bool isComplaint = norm.Equals("Complaint", StringComparison.OrdinalIgnoreCase);

            query = query.Where(c => 
                c.CaseType == norm || 
                (isInquiry && (c.CaseNumber.StartsWith("I-") || c.CaseNumber.StartsWith("E-"))) ||
                (isService && c.CaseNumber.StartsWith("S-")) ||
                (isComplaint && c.CaseNumber.StartsWith("C-"))
            );
        }

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string normStatus = status.Trim().Replace(" ", "").Replace("_", "");
            if (Enum.TryParse<CaseStatus>(normStatus, true, out var parsedStatus))
            {
                query = query.Where(c => c.Status == parsedStatus);
            }
        }

        if (!string.IsNullOrWhiteSpace(priority) && !priority.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string normPriority = priority.Trim().ToLower();
            if (normPriority == "critical")
            {
                query = query.Where(c => c.Severity == "Critical" || c.Severity == "Bad");
            }
            else if (normPriority == "high")
            {
                query = query.Where(c => c.Severity == "High" || c.Severity == "Warn");
            }
            else if (normPriority == "medium")
            {
                query = query.Where(c => c.Severity == "Medium" || c.Severity == "Info");
            }
            else if (normPriority == "low")
            {
                query = query.Where(c => c.Severity == "Low" || c.Severity == "Ok");
            }
            else
            {
                query = query.Where(c => c.Severity.ToLower() == normPriority);
            }
        }

        if (!string.IsNullOrWhiteSpace(channel) && !channel.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            string normChannel = channel.Trim().ToLower();
            if (normChannel == "voice" || normChannel == "phone")
            {
                query = query.Where(c => 
                    (c.SourceChannel != null && (c.SourceChannel.ToLower() == "voice" || c.SourceChannel.ToLower() == "phone")) ||
                    (c.SourceChannel == null && (c.CommunicationChannel.ToLower() == "voice" || c.CommunicationChannel.ToLower() == "phone")));
            }
            else
            {
                query = query.Where(c => 
                    (c.SourceChannel != null && c.SourceChannel.ToLower() == normChannel) ||
                    (c.SourceChannel == null && c.CommunicationChannel.ToLower() == normChannel));
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string pattern = SqlSearchPattern.Contains(search.Trim());
            query = query.Where(c => EF.Functions.ILike(c.CaseNumber, pattern)
                || EF.Functions.ILike(c.Title, pattern)
                || (c.Customer != null && EF.Functions.ILike(c.Customer.FullName, pattern))
                || (c.Owner != null && EF.Functions.ILike(c.Owner.Name, pattern))
                || c.ChildRelations.Any(cr => EF.Functions.ILike(cr.ChildId, pattern)));
        }

        int totalCount = await query.CountAsync(ct);

        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 30;
        if (pageSize > 100) pageSize = 100;

        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .MapToCaseSummary()
            .ToListAsync(ct);

        return new PagedResponseDto<CaseSummaryDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Header smart-search over cases. Matches the same fields the old client-side filter did
    /// (case number, title, and sub-case child ids), but in the database and capped to
    /// <paramref name="limit"/> narrow rows.
    /// </summary>
    public async Task<IEnumerable<SearchCaseHitDto>> SearchCasesAsync(string query, int limit, CancellationToken ct = default)
    {
        var pattern = SqlSearchPattern.Contains(query);

        return await _context.Cases
            .AsNoTracking()
            .Where(c => EF.Functions.ILike(c.CaseNumber, pattern)
                     || EF.Functions.ILike(c.Title, pattern)
                     || c.ChildRelations.Any(cr => EF.Functions.ILike(cr.ChildId, pattern)))
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit)
            .Select(c => new SearchCaseHitDto
            {
                Id = c.Id,
                CaseNumber = c.CaseNumber,
                Title = c.Title
            })
            .ToListAsync(ct);
    }

    public async Task<CaseDetailDto?> GetCaseDetailAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Cases
            .AsNoTracking()
            .AsSplitQuery()
            .Where(c => c.Id == id)
            .MapToCaseDetail()
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Atomically allocates the next numeric part of a top-level case number.
    /// Backed by a PostgreSQL sequence (created and seeded from existing data at startup),
    /// so concurrent case creation can never hand out the same number twice.
    /// </summary>
    public async Task<int> GetNextCaseSequenceAsync(CancellationToken ct = default)
    {
        try
        {
            var next = await _context.Database
                .SqlQueryRaw<long>($"SELECT nextval('{SchemaConstants.CaseNumberSequence}') AS \"Value\"")
                .SingleAsync(ct);

            return checked((int)next);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Sequence is missing (startup bootstrap could not run, e.g. restricted DDL rights).
            // Fall back to a database-side max so we still never materialise the case table,
            // then let the unique index on CaseNumber guard against a concurrent duplicate.
            return await GetNextCaseSequenceFromDataAsync(ct);
        }
    }

    private async Task<int> GetNextCaseSequenceFromDataAsync(CancellationToken ct)
    {
        var max = await _context.Database
            .SqlQueryRaw<long>(
                @"SELECT COALESCE(MAX(split_part(""CaseNumber"", '-', 2)::bigint), 0) AS ""Value""
                  FROM ""Cases""
                  WHERE split_part(""CaseNumber"", '-', 2) ~ '^[0-9]+$'")
            .SingleAsync(ct);

        return checked((int)max) + 1;
    }

    /// <summary>
    /// Next per-parent suffix for a sub-case (L01/L02, R01/R02...).
    /// Derived from the highest suffix already issued rather than from a row count, so a
    /// removed relation can never cause a suffix to be handed out twice.
    /// </summary>
    public async Task<int> GetNextChildSequenceAsync(Guid parentCaseId, ChildRelationType relationType, CancellationToken ct = default)
    {
        var existingChildIds = await _context.CaseChildRelations
            .AsNoTracking()
            .Where(cr => cr.ParentCaseId == parentCaseId && cr.RelationType == relationType)
            .Select(cr => cr.ChildId)
            .ToListAsync(ct);

        // Sub-case rows outlive their relation when a link is removed, so their numbers must be
        // counted too; otherwise re-linking would hand out an id that already exists in Cases.
        var parentNumber = await _context.Cases.AsNoTracking()
            .Where(c => c.Id == parentCaseId)
            .Select(c => c.CaseNumber)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(parentNumber))
        {
            var marker = parentNumber + (relationType == ChildRelationType.Link ? "-L" : "-R");
            var subcaseNumbers = await _context.Cases.AsNoTracking()
                .Where(c => c.ParentCaseId == parentCaseId && c.CaseNumber.StartsWith(marker))
                .Select(c => c.CaseNumber)
                .ToListAsync(ct);
            existingChildIds.AddRange(subcaseNumbers);
        }

        int maxSuffix = 0;
        foreach (var childId in existingChildIds)
        {
            if (string.IsNullOrWhiteSpace(childId)) continue;

            // Suffix is the trailing digits of e.g. "C-10479-L02".
            int digitStart = childId.Length;
            while (digitStart > 0 && char.IsDigit(childId[digitStart - 1])) digitStart--;

            if (digitStart < childId.Length &&
                int.TryParse(childId.AsSpan(digitStart), out int suffix) &&
                suffix > maxSuffix)
            {
                maxSuffix = suffix;
            }
        }

        return maxSuffix + 1;
    }

    public async Task AddChildRelationAsync(CaseChildRelation relation, CancellationToken ct = default)
    {
        _context.CaseChildRelations.Add(relation);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<CaseChildRelation?> GetChildRelationByChildIdAsync(string childId, CancellationToken ct = default)
    {
        return await _context.CaseChildRelations
            .Include(cr => cr.ParentCase).ThenInclude(pc => pc.Department)
            .Include(cr => cr.ParentCase).ThenInclude(pc => pc.Customer)
            .Include(cr => cr.LinkedCase)
            .Include(cr => cr.CreatedByUser)
            .FirstOrDefaultAsync(cr => cr.ChildId == childId, ct);
    }

    public async Task<PagedResponseDto<CaseAuditEventDto>> GetCaseAuditEventsAsync(int page = 1, int pageSize = 10, string? actionType = null, string? search = null, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        var query = _context.CaseEvents
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(actionType) && !string.Equals(actionType, "all", StringComparison.OrdinalIgnoreCase))
        {
            var cleanAction = actionType.Trim().ToLower();
            if (cleanAction.Contains("create"))
                query = query.Where(ce => ce.EventType == EventType.Create || ce.ActionType == "CREATE");
            else if (cleanAction.Contains("assign"))
                query = query.Where(ce => ce.EventType == EventType.Assign);
            else if (cleanAction.Contains("note"))
                query = query.Where(ce => ce.EventType == EventType.Note);
            else if (cleanAction.Contains("escalat"))
                query = query.Where(ce => ce.EventType == EventType.Escalate);
            else if (cleanAction.Contains("resolv"))
                query = query.Where(ce => ce.EventType == EventType.Resolve);
            else if (cleanAction.Contains("transfer"))
                query = query.Where(ce => ce.EventType == EventType.Transfer);
            else if (cleanAction.Contains("cowork"))
                query = query.Where(ce => ce.EventType == EventType.Cowork);
            else if (cleanAction.Contains("config"))
                query = query.Where(ce => ce.Module == "Configurable Settings" || ce.ActionType != null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(ce =>
                (ce.Case != null && ce.Case.CaseNumber != null && ce.Case.CaseNumber.ToLower().Contains(s)) ||
                (ce.Case != null && ce.Case.Title != null && ce.Case.Title.ToLower().Contains(s)) ||
                (ce.Case != null && ce.Case.Customer != null && ce.Case.Customer.FullName != null && ce.Case.Customer.FullName.ToLower().Contains(s)) ||
                (ce.User != null && ce.User.Name != null && ce.User.Name.ToLower().Contains(s)) ||
                (ce.Message != null && ce.Message.ToLower().Contains(s)) ||
                (ce.Module != null && ce.Module.ToLower().Contains(s)) ||
                (ce.EntityName != null && ce.EntityName.ToLower().Contains(s))
            );
        }

        var totalCount = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(ce => ce.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ce => new AuditEventRow
            {
                Id = ce.Id,
                CreatedAt = ce.CreatedAt,
                EventType = ce.EventType,
                Message = ce.Message,
                UserId = ce.UserId,
                ActorName = ce.User != null ? ce.User.Name : null,
                ActorRole = ce.User != null ? ce.User.Role : null,
                CaseId = ce.CaseId,
                CaseNumber = ce.Case != null ? ce.Case.CaseNumber : (ce.EntityName ?? ce.Module ?? "System"),
                CaseTitle = ce.Case != null ? ce.Case.Title : (ce.Module ?? "Configurable Settings"),
                CustomerId = ce.Case != null ? (Guid?)ce.Case.CustomerId : null,
                CustomerName = ce.Case != null && ce.Case.Customer != null ? ce.Case.Customer.FullName : null,
                DepartmentName = ce.Case != null && ce.Case.Department != null ? ce.Case.Department.Name : null,
                Severity = ce.Case != null ? ce.Case.Severity : null,
                NewOwner = ce.Case != null && ce.Case.Owner != null ? ce.Case.Owner.Name : null,
                Module = ce.Module,
                EntityName = ce.EntityName,
                OldValue = ce.OldValue,
                NewValue = ce.NewValue,
                ActionType = ce.ActionType
            })
            .ToListAsync(ct);

        var items = rows.Select(e => new CaseAuditEventDto
        {
            Id = e.Id,
            Timestamp = e.CreatedAt,
            ActionType = !string.IsNullOrEmpty(e.ActionType) ? e.ActionType : e.EventType.ToString(),
            ActionLabel = !string.IsNullOrEmpty(e.ActionType) ? $"Config {e.ActionType}" : e.EventType switch
            {
                EventType.Create => "Case Created",
                EventType.Assign => "Case Assigned",
                EventType.Note => "Note Added",
                EventType.Cowork => "Coworker Added",
                EventType.Transfer => "Department Transferred",
                EventType.Escalate => "Case Escalated",
                EventType.Resolve => "Case Resolved",
                _ => "Case Event"
            },
            ActorId = e.UserId,
            ActorName = e.ActorName ?? "System Admin",
            ActorRole = e.ActorRole ?? "Administrator",
            CaseId = e.CaseId,
            CaseNumber = e.CaseNumber ?? "Configurable Settings",
            CaseTitle = e.CaseTitle ?? "System Settings",
            CustomerId = e.CustomerId,
            CustomerName = e.CustomerName,
            Description = e.Message,
            DepartmentName = e.DepartmentName,
            Severity = e.Severity,
            NewOwner = e.NewOwner,
            Module = e.Module,
            EntityName = e.EntityName,
            OldValue = e.OldValue,
            NewValue = e.NewValue,
            PreviousStatus = e.OldValue,
            NewStatus = e.NewValue
        }).ToList();

        return new PagedResponseDto<CaseAuditEventDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    private sealed class AuditEventRow
    {
        public Guid Id { get; init; }
        public DateTime CreatedAt { get; init; }
        public EventType EventType { get; init; }
        public string Message { get; init; } = string.Empty;
        public Guid UserId { get; init; }
        public string? ActorName { get; init; }
        public string? ActorRole { get; init; }
        public Guid? CaseId { get; init; }
        public string? CaseNumber { get; init; }
        public string? CaseTitle { get; init; }
        public Guid? CustomerId { get; init; }
        public string? CustomerName { get; init; }
        public string? DepartmentName { get; init; }
        public string? Severity { get; init; }
        public string? NewOwner { get; init; }
        public string? Module { get; init; }
        public string? EntityName { get; init; }
        public string? OldValue { get; init; }
        public string? NewValue { get; init; }
        public string? ActionType { get; init; }
    }
}
