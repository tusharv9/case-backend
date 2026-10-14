namespace CaseManagement.Api.DTOs;

/// <summary>One line of the Case Collaboration feed.</summary>
public class CollaborationActivityDto
{
    public Guid Id { get; set; }
    public string ActivityType { get; set; } = string.Empty; // CollaboratorAdded, CollaboratorRemoved, NoteAdded, SwarmRequested
    public Guid ActorUserId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string? ActorRole { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? TargetName { get; set; }
    public string? Content { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Collaborators on a case plus one page of its collaboration feed, newest first.</summary>
public class CaseCollaborationDto
{
    public List<ParticipantDto> Collaborators { get; set; } = new();
    public List<CollaborationActivityDto> Activities { get; set; } = new();
    public bool HasMore { get; set; }
}

public class AddCollaborationNoteDto
{
    public string Content { get; set; } = string.Empty;
}

/// <summary>Header counts for the Case Management page (computed in the database).</summary>
public class CaseStatsDto
{
    public int OpenCount { get; set; }
    public int BreachedCount { get; set; }
}
