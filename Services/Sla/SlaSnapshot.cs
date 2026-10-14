namespace CaseManagement.Api.Services;

using CaseManagement.Api.Models;

/// <summary>Everything the SLA clock needs to know about a case. Built from a <see cref="Case"/> or projected straight from the database.</summary>
public sealed class SlaInputs
{
    public CaseStatus Status { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public DateTime? PausedAt { get; init; }

    /// <summary>Business minutes already spent paused (completed pauses only).</summary>
    public int PausedMinutes { get; init; }

    public int FirstResponseTargetMinutes { get; init; }
    public int InternalTargetMinutes { get; init; }
    public int ExternalTargetMinutes { get; init; }

    public DateTime? FirstResponseActualAt { get; init; }
    public string FirstResponseStatus { get; init; } = "Pending";

    public static SlaInputs From(Case c) => new()
    {
        Status = c.Status,
        StartUtc = c.SlaStartTime,
        ResolvedAt = c.ResolvedAt,
        PausedAt = c.SlaPausedAt,
        PausedMinutes = c.SlaTotalPausedMinutes,
        FirstResponseTargetMinutes = c.FirstResponseTargetMinutes,
        InternalTargetMinutes = c.InternalResolutionTargetMinutes,
        ExternalTargetMinutes = c.ExternalResolutionTargetMinutes,
        FirstResponseActualAt = c.FirstResponseActualAt,
        FirstResponseStatus = c.FirstResponseStatus,
    };
}

/// <summary>One SLA target measured against the clock, in business minutes.</summary>
public sealed record SlaTargetState(
    int TargetMinutes,
    double ConsumedMinutes,
    double RemainingMinutes,
    double ConsumedPercent,
    DateTime? DueAt,
    bool IsBreached);

public enum SlaHealth { Healthy, Approaching, Breached, Paused, Met }

/// <summary>The single answer to "how is this case doing against its SLA right now".</summary>
public sealed record SlaSnapshot(
    SlaHealth Health,
    bool IsPaused,
    bool IsStopped,
    bool IsClockRunning,
    SlaTargetState Internal,
    SlaTargetState External,
    SlaTargetState FirstResponse,
    string FirstResponseStatus,
    DateTime ComputedAt);
