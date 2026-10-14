namespace CaseManagement.Api.Data;

public enum DatabaseInitializationStatus
{
    /// <summary>Not started yet.</summary>
    Pending,
    /// <summary>Connecting / migrating / seeding.</summary>
    Initializing,
    /// <summary>Schema is current and seed data is in place; the API can serve requests.</summary>
    Ready,
    /// <summary>The schema needs migrating but automatic migration is disabled.</summary>
    MigrationRequired,
    /// <summary>Initialisation failed; see <see cref="DatabaseInitializationState.Message"/>.</summary>
    Failed
}

/// <summary>
/// Shared, thread-safe view of whether the database is usable. Lets the process answer liveness
/// (<c>/health</c>) immediately while database preparation continues in the background, lets
/// <c>/ready</c> and a middleware report the truth, and lets background workers wait for readiness.
/// </summary>
public sealed class DatabaseInitializationState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile DatabaseInitializationStatus _status = DatabaseInitializationStatus.Pending;
    private volatile string? _message;

    public DatabaseInitializationStatus Status => _status;
    public string? Message => _message;
    public bool IsReady => _status == DatabaseInitializationStatus.Ready;

    /// <summary>Completes when the database becomes ready (never completes if it never does).</summary>
    public Task WhenReady => _ready.Task;

    public void Set(DatabaseInitializationStatus status, string? message = null)
    {
        _message = message;
        _status = status;
        if (status == DatabaseInitializationStatus.Ready) _ready.TrySetResult();
    }
}
