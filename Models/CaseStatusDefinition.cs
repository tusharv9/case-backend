namespace CaseManagement.Api.Models;

/// <summary>What a status means to the workflow. Code reasons about the category, never about a status's name.</summary>
public static class CaseStatusCategories
{
    /// <summary>New, not yet handled (only the system Open).</summary>
    public const string Open = "Open";
    /// <summary>Being worked (In Progress, and custom statuses such as Under Review).</summary>
    public const string Active = "Active";
    /// <summary>Waiting on someone else (Waiting on Customer, and custom statuses such as Pending Documents).</summary>
    public const string Waiting = "Waiting";
    /// <summary>Escalated by the SLA engine or the Escalate action (only the system Escalated).</summary>
    public const string Escalated = "Escalated";
    /// <summary>Finished (Resolved; the legacy Closed and Cancelled).</summary>
    public const string Closed = "Closed";

    public static readonly string[] All = [Open, Active, Waiting, Escalated, Closed];

    /// <summary>The categories an administrator may give a status they add.</summary>
    public static readonly string[] Custom = [Active, Waiting];

    /// <summary>The system status that stands for a category in <see cref="Case.Status"/>.</summary>
    public static CaseStatus SystemStatusOf(string category) => category switch
    {
        Open => CaseStatus.Open,
        Waiting => CaseStatus.WaitingOnCustomer,
        Escalated => CaseStatus.Escalated,
        Closed => CaseStatus.Resolved,
        _ => CaseStatus.InProgress,
    };
}

/// <summary>Whether the SLA clock runs while a case is in a status.</summary>
public static class SlaBehaviours
{
    public const string Running = "Running";
    public const string Paused = "Paused";
    public const string Stopped = "Stopped";
    public static readonly string[] All = [Running, Paused, Stopped];
}

/// <summary>What happens when a paused status's maximum pause runs out (applied from Phase 3).</summary>
public static class PauseExpiryActions
{
    public const string ResumeSla = "ResumeSla";
    public const string MoveToStatus = "MoveToStatus";
    public const string NotifyOnly = "NotifyOnly";
    public static readonly string[] All = [ResumeSla, MoveToStatus, NotifyOnly];
}

/// <summary>
/// One case status as configured under Configurable Settings → Status: its name, order, category and SLA behaviour. The five
/// workflow statuses are system statuses (renamed, recoloured and reordered, never deleted or recategorised); administrators
/// add their own Active or Waiting statuses alongside them.
/// </summary>
public class CaseStatusDefinition
{
    /// <summary>Stable identifier stored on cases (<see cref="Case.StatusKey"/>); a system status's key is its enum name.</summary>
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    /// <summary><see cref="CaseStatusCategories"/>.</summary>
    public string Category { get; set; } = CaseStatusCategories.Active;
    /// <summary>Display order (board columns, menus, filters) — never implies the next status.</summary>
    public int Level { get; set; }
    /// <summary><see cref="SlaBehaviours"/>.</summary>
    public string SlaBehaviour { get; set; } = SlaBehaviours.Running;
    /// <summary>Paused statuses: the longest a case may stay paused here, in business minutes; null = until the status changes.</summary>
    public int? MaxPauseMinutes { get; set; }
    /// <summary><see cref="PauseExpiryActions"/>, when <see cref="MaxPauseMinutes"/> is set.</summary>
    public string? OnPauseExpiry { get; set; }
    /// <summary>The status to move to when <see cref="OnPauseExpiry"/> is MoveToStatus.</summary>
    public string? PauseExpiryStatusKey { get; set; }
    /// <summary>A colour name from the design system's status palette (blue, indigo, sky, amber, red, green, slate, violet).</summary>
    public string Color { get; set; } = "slate";
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}

/// <summary>One allowed manual move between statuses (the Status menu). Moves into Open, Escalated and Resolved are actions, not rows.</summary>
public class CaseStatusTransition
{
    public string FromKey { get; set; } = string.Empty;
    public string ToKey { get; set; } = string.Empty;
}
