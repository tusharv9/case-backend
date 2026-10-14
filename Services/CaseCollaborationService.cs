namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public interface ICaseCollaborationService
{
    Task AddCoworkersAsync(Guid caseId, List<Guid> coworkerUserIds, Guid addedByUserId);
    Task RemoveCoworkerAsync(Guid caseId, Guid coworkerUserId, Guid removedByUserId);
    Task RequestSwarmAsync(Guid caseId, RequestSwarmDto dto, Guid userId);

    /// <summary>The collaboration feed (collaborators + collaboration-only activity), separate from the workflow timeline.</summary>
    Task<CaseCollaborationDto> GetCollaborationAsync(Guid caseId, DateTime? before, int limit, CancellationToken ct = default);
    Task<CollaborationActivityDto> AddCollaborationNoteAsync(Guid caseId, string content, Guid userId);
}

/// <summary>Working a case together: collaborators, swarming, and the collaboration notes feed.</summary>
public class CaseCollaborationService : ICaseCollaborationService
{
    private readonly ICaseRepository _caseRepository;
    private readonly INotificationService _notificationService;
    private readonly IMentionService _mentions;
    private readonly ISlaClockProvider _slaClock;
    private readonly AppDbContext _context;
    private readonly ILogger<CaseCollaborationService> _logger;

    public CaseCollaborationService(
        ICaseRepository caseRepository,
        INotificationService notificationService,
        IMentionService mentions,
        ISlaClockProvider slaClock,
        AppDbContext context,
        ILogger<CaseCollaborationService> logger)
    {
        _caseRepository = caseRepository;
        _notificationService = notificationService;
        _mentions = mentions;
        _slaClock = slaClock;
        _context = context;
        _logger = logger;
    }

    public async Task AddCoworkersAsync(Guid caseId, List<Guid> coworkerUserIds, Guid addedByUserId)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        if (existingCase.Status == CaseStatus.Resolved)
            throw new InvalidOperationException("Cannot add coworkers to a resolved case.");

        var ids = (coworkerUserIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one collaborator.");

        // Validate everything first so a bad id cannot leave the case half-updated.
        if (existingCase.Participants.Any(p => ids.Contains(p.UserId) && p.Role == ParticipantRole.CoWorker))
            throw new InvalidOperationException("One or more users are already coworkers on this case.");

        var users = await _context.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id) || u.Id == addedByUserId)
            .Select(u => new { u.Id, u.Name })
            .ToListAsync();
        if (ids.Any(id => users.All(u => u.Id != id)))
            throw new InvalidOperationException("One or more selected users no longer exist.");

        var now = DateTime.UtcNow;
        var actorName = users.FirstOrDefault(u => u.Id == addedByUserId)?.Name ?? "A user";
        var addedNames = new List<string>();

        foreach (var id in ids)
        {
            _context.CaseParticipants.Add(new CaseParticipant
            {
                CaseId = caseId,
                UserId = id,
                Role = ParticipantRole.CoWorker
            });
            _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
            {
                CaseId = caseId,
                ActivityType = CollaborationActivityTypes.CollaboratorAdded,
                ActorUserId = addedByUserId,
                TargetUserId = id,
                CreatedAt = now
            });
            addedNames.Add(users.First(u => u.Id == id).Name);
        }

        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Cowork,
            Message = $"{actorName} added {string.Join(", ", addedNames)} as collaborator{(addedNames.Count > 1 ? "s" : "")}.",
            CreatedAt = now,
            UserId = addedByUserId
        });

        // Participants, collaboration activity and the audit event are saved together.
        await _context.SaveChangesAsync();
    }

    public async Task RemoveCoworkerAsync(Guid caseId, Guid coworkerUserId, Guid removedByUserId)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");
        
        if (existingCase.Status == CaseStatus.Resolved)
            throw new InvalidOperationException("Cannot remove coworkers from a resolved case.");

        var participant = existingCase.Participants.FirstOrDefault(p => p.UserId == coworkerUserId);
        if (participant == null)
            throw new InvalidOperationException("That user is not a collaborator on this case.");

        var names = await _context.Users.AsNoTracking()
            .Where(u => u.Id == coworkerUserId || u.Id == removedByUserId)
            .ToDictionaryAsync(u => u.Id, u => u.Name);
        var now = DateTime.UtcNow;
        var actorName = names.TryGetValue(removedByUserId, out var actorValue) ? actorValue : "A user";
        var targetName = names.TryGetValue(coworkerUserId, out var targetValue) ? targetValue : "a collaborator";

        _context.CaseParticipants.Remove(participant);
        _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
        {
            CaseId = caseId,
            ActivityType = CollaborationActivityTypes.CollaboratorRemoved,
            ActorUserId = removedByUserId,
            TargetUserId = coworkerUserId,
            CreatedAt = now
        });
        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Cowork,
            Message = $"{actorName} removed {targetName} as collaborator.",
            CreatedAt = now,
            UserId = removedByUserId
        });

        await _context.SaveChangesAsync();
    }

    public async Task<CaseCollaborationDto> GetCollaborationAsync(Guid caseId, DateTime? before, int limit, CancellationToken ct = default)
    {
        if (limit < 1) limit = 50;
        if (limit > 200) limit = 200;

        var collaborators = await _context.CaseParticipants.AsNoTracking()
            .Where(p => p.CaseId == caseId)
            .Select(p => new ParticipantDto
            {
                UserId = p.UserId,
                UserName = p.User != null ? p.User.Name : string.Empty,
                Role = p.Role.ToString()
            })
            .ToListAsync(ct);

        var query = _context.CaseCollaborationActivities.AsNoTracking().Where(a => a.CaseId == caseId);
        if (before.HasValue)
        {
            var cutoff = DateTime.SpecifyKind(before.Value, DateTimeKind.Utc);
            query = query.Where(a => a.CreatedAt < cutoff);
        }

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit + 1)
            .Select(a => new CollaborationActivityDto
            {
                Id = a.Id,
                ActivityType = a.ActivityType,
                ActorUserId = a.ActorUserId,
                ActorName = a.ActorUser != null ? a.ActorUser.Name : string.Empty,
                ActorRole = a.ActorUser != null ? a.ActorUser.Role : null,
                TargetUserId = a.TargetUserId,
                TargetName = a.TargetUser != null ? a.TargetUser.Name : null,
                Content = a.Content,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync(ct);

        return new CaseCollaborationDto
        {
            Collaborators = collaborators,
            Activities = rows.Take(limit).ToList(),
            HasMore = rows.Count > limit
        };
    }

    public async Task<CollaborationActivityDto> AddCollaborationNoteAsync(Guid caseId, string content, Guid userId)
    {
        var text = content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Note cannot be empty.");
        if (text.Length > 2000)
            throw new InvalidOperationException("Note cannot exceed 2000 characters.");

        var existingCase = await _context.Cases.AsNoTracking()
            .Where(c => c.Id == caseId)
            .Select(c => new { c.Id, c.CaseNumber })
            .FirstOrDefaultAsync();
        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        var sender = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);

        var activity = new CaseCollaborationActivity
        {
            CaseId = caseId,
            ActivityType = CollaborationActivityTypes.NoteAdded,
            ActorUserId = userId,
            Content = text,
            CreatedAt = DateTime.UtcNow
        };
        _context.CaseCollaborationActivities.Add(activity);
        await _context.SaveChangesAsync();

        // @mentions notify colleagues, as internal notes always have.
        await _mentions.NotifyAsync(text, userId, sender?.Name, caseId, existingCase.CaseNumber, "Mentioned in Case Collaboration");

        return new CollaborationActivityDto
        {
            Id = activity.Id,
            ActivityType = activity.ActivityType,
            ActorUserId = userId,
            ActorName = sender?.Name ?? string.Empty,
            ActorRole = sender?.Role,
            Content = activity.Content,
            CreatedAt = activity.CreatedAt
        };
    }


    public async Task RequestSwarmAsync(Guid caseId, RequestSwarmDto dto, Guid userId)
    {
        var existingCase = await _context.Cases
            .Include(c => c.Department)
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == caseId);

        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        if (existingCase.Status == CaseStatus.Resolved)
            throw new InvalidOperationException("Cannot request a swarm on a resolved case.");

        var requestingUser = await _context.Users.FindAsync(userId);
        var addedUsers = new List<User>();

        var specifiedIds = (dto.ParticipantUserIds ?? new List<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (specifiedIds.Count > 0)
        {
            // Dynamic user-selected participant resolution
            var requestedUsers = await _context.Users
                .Where(u => specifiedIds.Contains(u.Id) && u.IsActive)
                .ToListAsync();

            foreach (var user in requestedUsers)
            {
                if (!existingCase.Participants.Any(p => p.UserId == user.Id))
                {
                    _context.CaseParticipants.Add(new CaseParticipant
                    {
                        CaseId = caseId,
                        UserId = user.Id,
                        Role = ParticipantRole.CoWorker
                    });
                    addedUsers.Add(user);
                }
            }
        }
        else
        {
            // 1. Resolve Team Lead dynamically from department and role architecture
            var deptMemberUserIds = await _context.TeamMembers
                .Where(m => m.DepartmentId == existingCase.DepartmentId && m.IsActive)
                .Select(m => m.UserId)
                .ToListAsync();

            var deptUsers = await _context.Users
                .Where(u => u.DepartmentId == existingCase.DepartmentId || deptMemberUserIds.Contains(u.Id))
                .ToListAsync();

            User? teamLead = null;
            if (existingCase.Department != null && existingCase.Department.OwnerId.HasValue)
            {
                teamLead = await _context.Users.FindAsync(existingCase.Department.OwnerId.Value);
            }

            if (teamLead == null)
            {
                teamLead = deptUsers.FirstOrDefault(u =>
                    u.Role.Contains("Lead", StringComparison.OrdinalIgnoreCase) ||
                    u.Role.Contains("Supervisor", StringComparison.OrdinalIgnoreCase) ||
                    u.Role.Contains("Manager", StringComparison.OrdinalIgnoreCase)
                );
            }

            if (teamLead == null)
            {
                teamLead = await _context.Users.FirstOrDefaultAsync(u =>
                    u.Role.Contains("Lead", StringComparison.OrdinalIgnoreCase) ||
                    u.Role.Contains("Supervisor", StringComparison.OrdinalIgnoreCase) ||
                    u.Role.Contains("Manager", StringComparison.OrdinalIgnoreCase)
                );
            }

            // 2. Resolve Subject Matter Experts (SMEs) dynamically
            var smes = deptUsers.Where(u =>
                (teamLead == null || u.Id != teamLead.Id) &&
                u.Id != existingCase.OwnerId &&
                (u.Role.Contains("Senior", StringComparison.OrdinalIgnoreCase) ||
                 u.Role.Contains("Sr.", StringComparison.OrdinalIgnoreCase) ||
                 u.Role.Contains("Specialist", StringComparison.OrdinalIgnoreCase) ||
                 u.Role.Contains("Expert", StringComparison.OrdinalIgnoreCase) ||
                 u.Role.Contains("Officer", StringComparison.OrdinalIgnoreCase))
            ).Take(2).ToList();

            if (smes.Count == 0)
            {
                smes = deptUsers.Where(u =>
                    (teamLead == null || u.Id != teamLead.Id) &&
                    u.Id != existingCase.OwnerId
                ).Take(1).ToList();
            }

            if (smes.Count == 0)
            {
                smes = await _context.Users.Where(u =>
                    (teamLead == null || u.Id != teamLead.Id) &&
                    u.Id != existingCase.OwnerId &&
                    (u.Role.Contains("Senior", StringComparison.OrdinalIgnoreCase) ||
                     u.Role.Contains("Sr.", StringComparison.OrdinalIgnoreCase) ||
                     u.Role.Contains("Specialist", StringComparison.OrdinalIgnoreCase) ||
                     u.Role.Contains("Expert", StringComparison.OrdinalIgnoreCase) ||
                     u.Role.Contains("Officer", StringComparison.OrdinalIgnoreCase))
                ).Take(2).ToListAsync();
            }

            if (smes.Count == 0)
            {
                smes = await _context.Users.Where(u =>
                    (teamLead == null || u.Id != teamLead.Id) &&
                    u.Id != existingCase.OwnerId
                ).Take(1).ToListAsync();
            }

            // 3. Add Team Lead and SMEs as case participants if not already added
            if (teamLead != null && !existingCase.Participants.Any(p => p.UserId == teamLead.Id))
            {
                var p = new CaseParticipant
                {
                    CaseId = caseId,
                    UserId = teamLead.Id,
                    Role = ParticipantRole.CoWorker
                };
                _context.CaseParticipants.Add(p);
                addedUsers.Add(teamLead);
            }

            foreach (var sme in smes)
            {
                if (!existingCase.Participants.Any(p => p.UserId == sme.Id))
                {
                    var p = new CaseParticipant
                    {
                        CaseId = caseId,
                        UserId = sme.Id,
                        Role = ParticipantRole.CoWorker
                    };
                    _context.CaseParticipants.Add(p);
                    addedUsers.Add(sme);
                }
            }
        }

        // Collaboration feed: the swarm request and every person it pulled in.
        var swarmTime = DateTime.UtcNow;
        _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
        {
            CaseId = caseId,
            ActivityType = CollaborationActivityTypes.SwarmRequested,
            ActorUserId = userId,
            Content = string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason.Trim(),
            CreatedAt = swarmTime
        });
        foreach (var added in addedUsers)
        {
            _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
            {
                CaseId = caseId,
                ActivityType = CollaborationActivityTypes.CollaboratorAdded,
                ActorUserId = userId,
                TargetUserId = added.Id,
                CreatedAt = swarmTime
            });
        }

        // 4. Raise the case's priority one step towards the most urgent configured priority (if it can go higher).
        var moreUrgent = await GetNextMoreUrgentPriorityAsync(existingCase.Severity);
        if (moreUrgent != null)
        {
            existingCase.Severity = moreUrgent.Priority;
            (await _slaClock.GetAsync()).ApplyRule(existingCase, moreUrgent);
        }

        await _context.SaveChangesAsync();

        // 5. Add Timeline Event matching Screenshot 1, 2, 3
        var swarmEvent = new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Cowork,
            Message = "⚡ Swarm requested — pulling in team lead and subject-matter experts. Priority attention needed.",
            IsInternal = true,
            CreatedAt = DateTime.UtcNow,
            UserId = userId
        };
        await _caseRepository.AddEventAsync(swarmEvent);

        // 6. Notify Team Lead and SMEs
        foreach (var u in addedUsers)
        {
            try
            {
                await _notificationService.CreateNotificationAsync(
                    u.Id,
                    "CASE_SWARM_REQUESTED",
                    "⚡ Swarm Requested",
                    $"Swarm requested on Case {existingCase.CaseNumber} ({existingCase.Title}) by {requestingUser?.Name ?? "Agent"}. You have been added to assist.",
                    existingCase.Id,
                    existingCase.CaseNumber,
                    "Critical"
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Swarm Notification Error");
            }
        }
    }


    /// <summary>The active priority immediately more urgent than <paramref name="currentPriority"/> (null if it is already the most urgent).</summary>
    private async Task<PrioritySlaRule?> GetNextMoreUrgentPriorityAsync(string currentPriority)
    {
        var lowered = currentPriority.ToLower();
        var rules = await _context.PrioritySlaRules.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.DisplayOrder).ToListAsync();
        var current = rules.FirstOrDefault(r => r.Priority.ToLower() == lowered);

        // A priority that is no longer active/known is treated as the least urgent.
        var currentOrder = current?.DisplayOrder ?? int.MaxValue;
        return rules.LastOrDefault(r => r.DisplayOrder < currentOrder);
    }
}
