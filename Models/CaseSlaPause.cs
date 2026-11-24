namespace CaseManagement.Api.Models;

/// <summary>
/// One stretch of time a case's SLA clock was paused: which status paused it, when, for how long (business minutes) and why it
/// ended. The case itself keeps only the running totals (SlaPausedAt, SlaTotalPausedMinutes); this is the history behind them,
/// for the audit trail, the calendar and the maximum-pause rule. Written by AppDbContext whenever a case's pause starts or ends.
/// </summary>
public class CaseSlaPause
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CaseId { get; set; }
    public Case? Case { get; set; }
    /// <summary>The status the case was in when the pause started.</summary>
    public string StatusKey { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>Business minutes the pause added to the case's paused total, once it ended.</summary>
    public int? PausedMinutes { get; set; }
    /// <summary>Why it ended: StatusChange, PauseLimit, Resolved, Escalated, Reopened.</summary>
    public string? EndReason { get; set; }
    /// <summary>When the status's maximum pause was reached and acted on (so it is acted on once).</summary>
    public DateTime? LimitReachedAt { get; set; }
}
