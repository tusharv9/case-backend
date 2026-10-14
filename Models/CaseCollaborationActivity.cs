namespace CaseManagement.Api.Models;

/// <summary>
/// One entry in a case's collaboration feed. Kept separate from <see cref="CaseEvent"/> so the
/// Case Collaboration panel shows only collaboration activity (collaborators added or removed,
/// collaboration notes, swarm requests) and never the general case workflow timeline.
/// </summary>
public class CaseCollaborationActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    /// <summary>See <see cref="CollaborationActivityTypes"/>.</summary>
    public string ActivityType { get; set; } = string.Empty;

    /// <summary>The user who performed the action.</summary>
    public Guid ActorUserId { get; set; }
    public User ActorUser { get; set; } = null!;

    /// <summary>The collaborator added or removed, when the activity is about a person.</summary>
    public Guid? TargetUserId { get; set; }
    public User? TargetUser { get; set; }

    /// <summary>Note text for <see cref="CollaborationActivityTypes.NoteAdded"/>.</summary>
    public string? Content { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class CollaborationActivityTypes
{
    public const string CollaboratorAdded = "CollaboratorAdded";
    public const string CollaboratorRemoved = "CollaboratorRemoved";
    public const string NoteAdded = "NoteAdded";
    public const string SwarmRequested = "SwarmRequested";
}
