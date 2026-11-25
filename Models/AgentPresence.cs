namespace CaseManagement.Api.Models;

/// <summary>
/// An agent's status as everyone else sees it: <b>Available</b>, <b>Busy</b> or <b>Away</b>. It is the status the person chose
/// (<see cref="User.Status"/>) <i>while they are here</i> — Case Management open in a tab (<see cref="User.LastSeenAt"/>). Someone
/// who signed out, closed the tab or lost their connection is Away, so a stale "Available" never sends them work. Signing in
/// (opening Case Management) restores the status they last chose.
/// </summary>
/// <remarks>Presence is not authentication: a person can be signed in to OmniConnect without Case Management open.</remarks>
public static class AgentPresence
{
    /// <summary>How long after the last sign of life a person still counts as here (the live connection refreshes it each minute).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(90);

    public static bool IsHere(DateTime? lastSeenAt, DateTime now) => lastSeenAt is { } seen && now - seen <= Window;

    /// <summary>Available, Busy or Away.</summary>
    public static UserStatus Effective(UserStatus chosen, DateTime? lastSeenAt, DateTime now) =>
        !IsHere(lastSeenAt, now) ? UserStatus.Away
        : chosen is UserStatus.Available or UserStatus.Busy ? chosen
        : UserStatus.Away;

    public static UserStatus Effective(User u, DateTime now) => Effective(u.Status, u.LastSeenAt, now);

    /// <summary>Here and able to take work: Available or Busy.</summary>
    public static bool IsOnline(User u, DateTime now) => Effective(u, now) is UserStatus.Available or UserStatus.Busy;

    public static string Label(UserStatus effective) => effective switch
    {
        UserStatus.Available => "Available",
        UserStatus.Busy => "Busy",
        _ => "Away",
    };
}
