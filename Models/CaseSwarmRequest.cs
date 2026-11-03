namespace CaseManagement.Api.Models;

/// <summary>
/// A request for help on a case, sent to one Team Lead or Supervisor (Request Swarm). The recipient is added to the case
/// as a Watcher so they can open it, and answers the request: accepting makes them a collaborator, declining removes the
/// watch. Regular agents are never swarm recipients — they are added through Case Collaboration instead.
/// </summary>
public class CaseSwarmRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;

    public Guid RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = null!;

    public Guid RecipientUserId { get; set; }
    public User RecipientUser { get; set; } = null!;

    /// <summary>See <see cref="SwarmRecipientKinds"/>.</summary>
    public string RecipientKind { get; set; } = SwarmRecipientKinds.TeamLead;

    /// <summary>The team the Team Lead was chosen for; null for a Supervisor.</summary>
    public Guid? TeamId { get; set; }
    public Department? Team { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>See <see cref="SwarmRequestStatuses"/>.</summary>
    public string Status { get; set; } = SwarmRequestStatuses.Pending;
    public string? ResponseNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? RespondedAt { get; set; }
}

public static class SwarmRecipientKinds
{
    public const string TeamLead = "TeamLead";
    public const string Supervisor = "Supervisor";
}

public static class SwarmRequestStatuses
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";
}
