namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public interface ICaseCollaborationService
{
    /// <summary>Active teams to choose collaborators from, the case's own team first.</summary>
    Task<IReadOnlyList<TeamOptionDto>> GetCollaboratorTeamsAsync(Guid caseId, CancellationToken ct = default);
    /// <summary>The active members and lead of one team, marking who is already on the case.</summary>
    Task<IReadOnlyList<CollaboratorCandidateDto>> GetCollaboratorCandidatesAsync(Guid caseId, Guid teamId, Guid callerId, CancellationToken ct = default);
    Task AddCoworkersAsync(Guid caseId, AddCollaboratorsDto dto, Guid addedByUserId);
    Task RemoveCoworkerAsync(Guid caseId, Guid coworkerUserId, Guid removedByUserId);

    /// <summary>Who a swarm can be requested from: every team's lead and the Supervisors (never regular agents).</summary>
    Task<SwarmCandidatesDto> GetSwarmCandidatesAsync(Guid caseId, Guid callerId, CancellationToken ct = default);
    Task<SwarmRequestDto> RequestSwarmAsync(Guid caseId, CreateSwarmRequestDto dto, Guid userId);
    Task<SwarmRequestDto> RespondToSwarmAsync(Guid caseId, Guid requestId, RespondSwarmRequestDto dto, Guid userId);

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
    private readonly IOrgDirectory? _directory;

    public CaseCollaborationService(
        ICaseRepository caseRepository,
        INotificationService notificationService,
        IMentionService mentions,
        ISlaClockProvider slaClock,
        AppDbContext context,
        ILogger<CaseCollaborationService> logger,
        IOrgDirectory? directory = null)
    {
        _directory = directory;
        _caseRepository = caseRepository;
        _notificationService = notificationService;
        _mentions = mentions;
        _slaClock = slaClock;
        _context = context;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TeamOptionDto>> GetCollaboratorTeamsAsync(Guid caseId, CancellationToken ct = default)
    {
        var caseTeamId = await _context.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => (Guid?)c.TeamId).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Case not found");
        var teams = await _context.Teams.AsNoTracking().Where(d => d.IsActive)
            .Select(d => new TeamOptionDto { Id = d.Id, Name = d.Name, IsCaseTeam = d.Id == caseTeamId }).ToListAsync(ct);
        return teams.OrderByDescending(t => t.IsCaseTeam).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<CollaboratorCandidateDto>> GetCollaboratorCandidatesAsync(Guid caseId, Guid teamId, Guid callerId, CancellationToken ct = default)
    {
        var c = await _context.Cases.AsNoTracking().Where(x => x.Id == caseId)
            .Select(x => new { x.OwnerId, CoWorkers = x.Participants.Where(p => p.Role == ParticipantRole.CoWorker).Select(p => p.UserId).ToList() })
            .FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Case not found");

        var people = await TeamPeopleAsync(teamId, ct);
        return people.Select(p =>
        {
            string? why = p.User.Id == c.OwnerId ? "Holds this case"
                : c.CoWorkers.Contains(p.User.Id) ? "Already a collaborator"
                : p.User.Id == callerId ? "You" : null;
            return new CollaboratorCandidateDto
            {
                UserId = p.User.Id, Name = p.User.Name, Email = p.User.Email, TeamRole = p.IsLead ? "Team Lead" : p.MemberRole,
                IsTeamLead = p.IsLead, IsUnavailable = why != null, UnavailableReason = why,
            };
        }).ToList();
    }

    private sealed record TeamPerson(User User, string MemberRole, bool IsLead);

    /// <summary>A team's active members and its lead, active users only. Refuses an unknown or inactive team.</summary>
    private async Task<List<TeamPerson>> TeamPeopleAsync(Guid teamId, CancellationToken ct)
    {
        var team = await _context.Teams.AsNoTracking().FirstOrDefaultAsync(d => d.Id == teamId, ct)
            ?? throw new KeyNotFoundException("Team not found.");
        if (!team.IsActive) throw new InvalidOperationException($"'{team.Name}' is inactive.");

        var members = await _context.TeamMembers.AsNoTracking().Include(m => m.User)
            .Where(m => m.TeamId == teamId && m.IsActive && m.User.IsActive).ToListAsync(ct);
        var result = members.Select(m => new TeamPerson(m.User, m.MemberRole, m.UserId == team.OwnerId)).ToList();
        if (team.OwnerId.HasValue && result.All(p => p.User.Id != team.OwnerId.Value)
            && await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == team.OwnerId.Value && u.IsActive, ct) is { } lead)
            result.Add(new TeamPerson(lead, "Team Lead", true));
        return result.OrderByDescending(p => p.IsLead).ThenBy(p => p.User.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task AddCoworkersAsync(Guid caseId, AddCollaboratorsDto dto, Guid addedByUserId)
    {
        var existingCase = await _caseRepository.GetByIdAsync(caseId);
        if (existingCase == null) throw new KeyNotFoundException("Case not found");

        if (existingCase.Status == CaseStatus.Resolved)
            throw new InvalidOperationException("Cannot add coworkers to a resolved case.");
        if (!dto.TeamId.HasValue)
            throw new ArgumentException("Choose the team the collaborators come from.");

        var ids = (dto.CoworkerIds ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            throw new InvalidOperationException("Select at least one collaborator.");

        // Validate everything first so a bad id cannot leave the case half-updated.
        var teamPeople = await TeamPeopleAsync(dto.TeamId.Value, default);
        var outsiders = ids.Where(id => teamPeople.All(p => p.User.Id != id)).ToList();
        if (outsiders.Count > 0)
            throw new ArgumentException("Every collaborator must be an active member or the lead of the chosen team.");
        if (ids.Contains(existingCase.OwnerId))
            throw new InvalidOperationException("The person holding the case cannot also be a collaborator on it.");
        if (existingCase.Participants.Any(p => ids.Contains(p.UserId) && p.Role == ParticipantRole.CoWorker))
            throw new InvalidOperationException("One or more users are already coworkers on this case.");

        var now = DateTime.UtcNow;
        var actorName = await _context.Users.AsNoTracking().Where(u => u.Id == addedByUserId).Select(u => u.Name).FirstOrDefaultAsync() ?? "A user";
        var teamName = await _context.Teams.AsNoTracking().Where(d => d.Id == dto.TeamId.Value).Select(d => d.Name).FirstAsync();
        var addedNames = new List<string>();

        foreach (var id in ids)
        {
            // Someone watching the case (a pending swarm request) becomes a collaborator instead of a second row.
            var watcher = existingCase.Participants.FirstOrDefault(p => p.UserId == id);
            if (watcher != null) watcher.Role = ParticipantRole.CoWorker;
            else _context.CaseParticipants.Add(new CaseParticipant { CaseId = caseId, UserId = id, Role = ParticipantRole.CoWorker });

            _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
            {
                CaseId = caseId,
                ActivityType = CollaborationActivityTypes.CollaboratorAdded,
                ActorUserId = addedByUserId,
                TargetUserId = id,
                CreatedAt = now
            });
            addedNames.Add(teamPeople.First(p => p.User.Id == id).User.Name);
        }

        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Cowork,
            ActionType = "COLLABORATOR_ADDED",
            NewValue = string.Join(", ", addedNames),
            Message = $"{actorName} added {string.Join(", ", addedNames)} from {teamName} as collaborator{(addedNames.Count > 1 ? "s" : "")}.",
            CreatedAt = now,
            UserId = addedByUserId
        });

        // Participants, collaboration activity and the audit event are saved together.
        await _context.SaveChangesAsync();

        foreach (var id in ids.Where(id => id != addedByUserId))
        {
            try
            {
                await _notificationService.CreateNotificationAsync(id, "CASE_COLLABORATOR_ADDED", "Added as Collaborator",
                    $"{actorName} added you as a collaborator on Case {existingCase.CaseNumber} ({existingCase.Title}).",
                    existingCase.Id, existingCase.CaseNumber, "Medium");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Collaborator Notification Error"); }
        }
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
            ActionType = "COLLABORATOR_REMOVED",
            OldValue = targetName,
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

        var swarm = await _context.CaseSwarmRequests.AsNoTracking().Where(r => r.CaseId == caseId)
            .OrderByDescending(r => r.CreatedAt).Take(50).Select(ProjectSwarm).ToListAsync(ct);

        return new CaseCollaborationDto
        {
            SwarmRequests = swarm,
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


    private static readonly System.Linq.Expressions.Expression<Func<CaseSwarmRequest, SwarmRequestDto>> ProjectSwarm = r => new SwarmRequestDto
    {
        Id = r.Id, CaseId = r.CaseId, CaseNumber = r.Case.CaseNumber,
        RequestedByUserId = r.RequestedByUserId, RequestedByName = r.RequestedByUser.Name,
        RecipientUserId = r.RecipientUserId, RecipientName = r.RecipientUser.Name, RecipientKind = r.RecipientKind,
        TeamId = r.TeamId, TeamName = r.Team != null ? r.Team.Name : null,
        Message = r.Message, Status = r.Status, ResponseNote = r.ResponseNote, CreatedAt = r.CreatedAt, RespondedAt = r.RespondedAt,
    };

    public async Task<SwarmCandidatesDto> GetSwarmCandidatesAsync(Guid caseId, Guid callerId, CancellationToken ct = default)
    {
        var c = await _context.Cases.AsNoTracking().Where(x => x.Id == caseId)
            .Select(x => new { x.OwnerId, x.TeamId, TeamName = x.Team.Name }).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Case not found");
        var pending = await _context.CaseSwarmRequests.AsNoTracking()
            .Where(r => r.CaseId == caseId && r.Status == SwarmRequestStatuses.Pending).Select(r => r.RecipientUserId).ToListAsync(ct);

        // Every active team's lead — another team's lead may be asked too — the case's own team first.
        var leads = await _context.Teams.AsNoTracking()
            .Where(d => d.IsActive && d.OwnerId != null && d.Owner!.IsActive && d.OwnerId != callerId && d.OwnerId != c.OwnerId)
            .Select(d => new SwarmRecipientDto
            {
                UserId = d.OwnerId!.Value, Name = d.Owner!.Name, Email = d.Owner.Email, Kind = SwarmRecipientKinds.TeamLead,
                TeamId = d.Id, TeamName = d.Name, IsCaseTeam = d.Id == c.TeamId,
            }).ToListAsync(ct);
        foreach (var l in leads) l.HasPendingRequest = pending.Contains(l.UserId);

        var result = new SwarmCandidatesDto
        {
            CaseTeamId = c.TeamId,
            CaseTeamName = c.TeamName,
            TeamLeads = leads.OrderByDescending(l => l.IsCaseTeam).ThenBy(l => l.TeamName, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        try
        {
            result.Supervisors = (await SupervisorsAsync(ct))
                .Where(u => u.Id != callerId && u.Id != c.OwnerId)
                .Select(u => new SwarmRecipientDto
                {
                    UserId = u.Id, Name = u.Name, Email = u.Email, Kind = SwarmRecipientKinds.Supervisor, HasPendingRequest = pending.Contains(u.Id),
                }).ToList();
        }
        catch (HostUsersException ex) { result.SupervisorsUnavailableMessage = ex.Message; }
        return result;
    }

    private async Task<IReadOnlyList<User>> SupervisorsAsync(CancellationToken ct) =>
        _directory == null ? Array.Empty<User>() : await _directory.SupervisorsAsync(ct);

    public async Task<SwarmRequestDto> RequestSwarmAsync(Guid caseId, CreateSwarmRequestDto dto, Guid userId)
    {
        var existingCase = await _context.Cases.Include(c => c.Participants).FirstOrDefaultAsync(c => c.Id == caseId)
            ?? throw new KeyNotFoundException("Case not found");
        if (CaseWorkflow.IsFinal(existingCase.Status))
            throw new InvalidOperationException($"Cannot request a swarm on a case that is {CaseWorkflow.Label(existingCase.Status)}.");

        if (dto.RecipientUserId == Guid.Empty) throw new ArgumentException("Choose the Team Lead or Supervisor to ask.");
        if (dto.RecipientUserId == userId) throw new ArgumentException("You cannot request a swarm from yourself.");
        if (dto.RecipientUserId == existingCase.OwnerId) throw new ArgumentException("The person holding the case cannot be asked to swarm on it.");

        // Only Team Leads and Supervisors receive swarm requests — checked here whatever the screen offered.
        Guid? teamId = null;
        if (string.Equals(dto.RecipientKind, SwarmRecipientKinds.TeamLead, StringComparison.OrdinalIgnoreCase))
        {
            if (!dto.TeamId.HasValue) throw new ArgumentException("Choose the team whose lead you are asking.");
            var team = await _context.Teams.AsNoTracking().FirstOrDefaultAsync(d => d.Id == dto.TeamId.Value)
                ?? throw new KeyNotFoundException("Team not found.");
            if (!team.IsActive) throw new InvalidOperationException($"'{team.Name}' is inactive.");
            if (team.OwnerId != dto.RecipientUserId) throw new ArgumentException($"The chosen person is not the lead of {team.Name}.");
            teamId = team.Id;
        }
        else if (string.Equals(dto.RecipientKind, SwarmRecipientKinds.Supervisor, StringComparison.OrdinalIgnoreCase))
        {
            if ((await SupervisorsAsync(default)).All(u => u.Id != dto.RecipientUserId))
                throw new ArgumentException("The chosen person is not a Supervisor (their role is not granted 'Supervise cases').");
        }
        else
        {
            throw new ArgumentException("A swarm request goes to a Team Lead or a Supervisor.");
        }

        var recipient = await _context.Users.FirstOrDefaultAsync(u => u.Id == dto.RecipientUserId && u.IsActive)
            ?? throw new ArgumentException("The chosen person is not an active user.");
        if (await _context.CaseSwarmRequests.AnyAsync(r => r.CaseId == caseId && r.RecipientUserId == recipient.Id && r.Status == SwarmRequestStatuses.Pending))
            throw new InvalidOperationException($"{recipient.Name} already has a pending swarm request on this case.");

        var requester = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        var now = DateTime.UtcNow;
        var kind = string.Equals(dto.RecipientKind, SwarmRecipientKinds.TeamLead, StringComparison.OrdinalIgnoreCase) ? SwarmRecipientKinds.TeamLead : SwarmRecipientKinds.Supervisor;
        var message = dto.Message?.Trim() ?? string.Empty;
        if (message.Length > 2000) throw new ArgumentException("The message cannot exceed 2000 characters.");

        // The first swarm on a case keeps the existing rule of raising its priority one step (never again on later requests).
        var firstSwarm = !await _context.CaseSwarmRequests.AnyAsync(r => r.CaseId == caseId);

        var request = new CaseSwarmRequest
        {
            CaseId = caseId, RequestedByUserId = userId, RecipientUserId = recipient.Id, RecipientKind = kind, TeamId = teamId,
            Message = message, Status = SwarmRequestStatuses.Pending, CreatedAt = now,
        };
        _context.CaseSwarmRequests.Add(request);

        // Watching lets the recipient open the case to decide; accepting makes them a collaborator.
        if (existingCase.Participants.All(p => p.UserId != recipient.Id))
            _context.CaseParticipants.Add(new CaseParticipant { CaseId = caseId, UserId = recipient.Id, Role = ParticipantRole.Watcher });

        _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
        {
            CaseId = caseId, ActivityType = CollaborationActivityTypes.SwarmRequested, ActorUserId = userId, TargetUserId = recipient.Id,
            Content = message.Length > 0 ? message : null, CreatedAt = now,
        });

        string? priorityChange = null;
        if (firstSwarm)
        {
            var moreUrgent = await GetNextMoreUrgentPriorityAsync(existingCase.Severity);
            if (moreUrgent != null)
            {
                priorityChange = $" Priority raised from {existingCase.Severity} to {moreUrgent.Priority}.";
                existingCase.Severity = moreUrgent.Priority;
                (await _slaClock.GetAsync()).ApplyRule(existingCase, moreUrgent);
            }
        }

        var kindLabel = kind == SwarmRecipientKinds.TeamLead ? "Team Lead" : "Supervisor";
        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Cowork,
            ActionType = "SWARM_REQUESTED",
            NewValue = $"{recipient.Name} ({kindLabel})",
            Message = $"⚡ Swarm requested by {requester?.Name ?? "a user"} from {recipient.Name} ({kindLabel}).{(message.Length > 0 ? $" \"{message}\"" : "")}{priorityChange}",
            IsInternal = true,
            CreatedAt = now,
            UserId = userId
        });
        await _context.SaveChangesAsync();

        try
        {
            await _notificationService.CreateNotificationAsync(recipient.Id, "CASE_SWARM_REQUESTED", "⚡ Swarm Requested",
                $"{requester?.Name ?? "An agent"} asked for your help on Case {existingCase.CaseNumber} ({existingCase.Title}).{(message.Length > 0 ? $" \"{message}\"" : "")}",
                existingCase.Id, existingCase.CaseNumber, "Critical");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Swarm Notification Error"); }

        return await _context.CaseSwarmRequests.AsNoTracking().Where(r => r.Id == request.Id).Select(ProjectSwarm).FirstAsync();
    }

    public async Task<SwarmRequestDto> RespondToSwarmAsync(Guid caseId, Guid requestId, RespondSwarmRequestDto dto, Guid userId)
    {
        var request = await _context.CaseSwarmRequests.Include(r => r.Case).FirstOrDefaultAsync(r => r.Id == requestId && r.CaseId == caseId)
            ?? throw new KeyNotFoundException("Swarm request not found.");
        if (request.RecipientUserId != userId)
            throw new CaseLockedException("Only the person a swarm request was sent to can answer it.");
        if (request.Status != SwarmRequestStatuses.Pending)
            throw new InvalidOperationException($"This swarm request was already {request.Status.ToLower()}.");

        var now = DateTime.UtcNow;
        var note = dto.Note?.Trim();
        request.Status = dto.Accept ? SwarmRequestStatuses.Accepted : SwarmRequestStatuses.Declined;
        request.ResponseNote = string.IsNullOrEmpty(note) ? null : note;
        request.RespondedAt = now;

        var participant = await _context.CaseParticipants.FirstOrDefaultAsync(p => p.CaseId == caseId && p.UserId == userId);
        if (dto.Accept)
        {
            if (participant == null) _context.CaseParticipants.Add(new CaseParticipant { CaseId = caseId, UserId = userId, Role = ParticipantRole.CoWorker });
            else participant.Role = ParticipantRole.CoWorker;
            _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
            {
                CaseId = caseId, ActivityType = CollaborationActivityTypes.CollaboratorAdded, ActorUserId = userId, TargetUserId = userId, CreatedAt = now,
            });
        }
        else if (participant is { Role: ParticipantRole.Watcher }
                 && !await _context.CaseSwarmRequests.AnyAsync(r => r.CaseId == caseId && r.RecipientUserId == userId && r.Id != requestId && r.Status == SwarmRequestStatuses.Pending))
        {
            _context.CaseParticipants.Remove(participant);
        }

        _context.CaseCollaborationActivities.Add(new CaseCollaborationActivity
        {
            CaseId = caseId, ActivityType = dto.Accept ? CollaborationActivityTypes.SwarmAccepted : CollaborationActivityTypes.SwarmDeclined,
            ActorUserId = userId, TargetUserId = request.RequestedByUserId, Content = request.ResponseNote, CreatedAt = now,
        });

        var me = await _context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync() ?? "The recipient";
        _context.CaseEvents.Add(new CaseEvent
        {
            CaseId = caseId,
            EventType = EventType.Cowork,
            ActionType = dto.Accept ? "SWARM_ACCEPTED" : "SWARM_DECLINED",
            OldValue = SwarmRequestStatuses.Pending,
            NewValue = request.Status,
            Message = $"{me} {(dto.Accept ? "accepted" : "declined")} the swarm request{(request.ResponseNote != null ? $": {request.ResponseNote}" : ".")}",
            IsInternal = true,
            CreatedAt = now,
            UserId = userId
        });
        await _context.SaveChangesAsync();

        try
        {
            await _notificationService.CreateNotificationAsync(request.RequestedByUserId, dto.Accept ? "CASE_SWARM_ACCEPTED" : "CASE_SWARM_DECLINED",
                dto.Accept ? "Swarm Accepted" : "Swarm Declined",
                $"{me} {(dto.Accept ? "joined" : "declined")} the swarm on Case {request.Case.CaseNumber}.{(request.ResponseNote != null ? $" \"{request.ResponseNote}\"" : "")}",
                caseId, request.Case.CaseNumber, "Medium");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Swarm Notification Error"); }

        return await _context.CaseSwarmRequests.AsNoTracking().Where(r => r.Id == requestId).Select(ProjectSwarm).FirstAsync();
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
