using CaseManagement.Api.Data;
using CaseManagement.Api.HostIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseManagement.Tests;

/// <summary>
/// The directory sync worker runs for the life of the process, so one bad iteration must never end it.
/// </summary>
/// <remarks>
/// HttpClient reports its own timeout as a <see cref="TaskCanceledException"/>, which is an
/// <see cref="OperationCanceledException"/>. The worker used to let that escape its per-iteration catch,
/// and an outer catch swallowed it: the loop ended on the first slow directory call, with nothing logged,
/// and users stopped being reconciled until the next restart.
/// </remarks>
public class HostUserSyncWorkerTests
{
    private sealed class FlakySynchronizer : IHostUserSynchronizer
    {
        private readonly Queue<Exception?> _outcomes;
        public int Calls { get; private set; }
        public TaskCompletionSource Exhausted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FlakySynchronizer(params Exception?[] outcomes) => _outcomes = new Queue<Exception?>(outcomes);

        public Task<(int Created, int Updated, int Deactivated)> SyncAllAsync(CancellationToken ct)
        {
            Calls++;
            if (_outcomes.Count == 0) { Exhausted.TrySetResult(); return Task.FromResult((0, 0, 0)); }
            var next = _outcomes.Dequeue();
            if (next != null) throw next;
            return Task.FromResult((1, 0, 0));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, exception));
        }
    }

    private static (HostUserSyncWorker Worker, CapturingLogger<HostUserSyncWorker> Log, List<TimeSpan> Delays) Build(IHostUserSynchronizer synchronizer, int intervalMinutes = 10)
    {
        var services = new ServiceCollection();
        services.AddSingleton(synchronizer);
        var provider = services.BuildServiceProvider();

        var state = new DatabaseInitializationState();
        state.Set(DatabaseInitializationStatus.Ready);
        var options = Options.Create(new HostIntegrationOptions { Directory = { SyncIntervalMinutes = intervalMinutes } });
        var log = new CapturingLogger<HostUserSyncWorker>();
        var delays = new List<TimeSpan>();

        var worker = new HostUserSyncWorker(provider.GetRequiredService<IServiceScopeFactory>(), state, options, log)
        {
            Delay = (span, ct) => { lock (delays) delays.Add(span); return Task.CompletedTask; }
        };
        return (worker, log, delays);
    }

    [Fact]
    public async Task An_HttpClient_timeout_is_logged_and_the_worker_keeps_syncing()
    {
        var synchronizer = new FlakySynchronizer(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var (worker, log, _) = Build(synchronizer);

        await worker.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(synchronizer.Exhausted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        Assert.Same(synchronizer.Exhausted.Task, finished);
        Assert.True(synchronizer.Calls >= 2);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Exception is TaskCanceledException);
    }

    [Fact]
    public async Task Consecutive_failures_back_off_but_never_wait_longer_than_the_sync_interval()
    {
        var synchronizer = new FlakySynchronizer(
            new HttpRequestException("down"), new HttpRequestException("down"), new HttpRequestException("down"),
            new HttpRequestException("down"), new HttpRequestException("down"), new HttpRequestException("down"));
        var (worker, _, delays) = Build(synchronizer, intervalMinutes: 5);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(synchronizer.Exhausted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        List<TimeSpan> seen;
        lock (delays) seen = delays.Take(6).ToList();
        Assert.Equal(TimeSpan.FromSeconds(30), seen[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), seen[1]);
        Assert.Equal(TimeSpan.FromSeconds(120), seen[2]);
        Assert.Equal(TimeSpan.FromSeconds(240), seen[3]);
        Assert.Equal(TimeSpan.FromMinutes(5), seen[4]);
        Assert.Equal(TimeSpan.FromMinutes(5), seen[5]);
    }

    [Fact]
    public async Task A_successful_sync_waits_the_full_interval_before_the_next_one()
    {
        var synchronizer = new FlakySynchronizer((Exception?)null);
        var (worker, _, delays) = Build(synchronizer, intervalMinutes: 7);

        await worker.StartAsync(CancellationToken.None);
        await Task.WhenAny(synchronizer.Exhausted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await worker.StopAsync(CancellationToken.None);

        lock (delays) Assert.Equal(TimeSpan.FromMinutes(7), delays[0]);
    }

    [Fact]
    public async Task Stopping_the_host_ends_the_worker_quietly()
    {
        var synchronizer = new FlakySynchronizer();
        var (worker, log, _) = Build(synchronizer);
        worker.Delay = (_, ct) => Task.Delay(Timeout.Infinite, ct);

        await worker.StartAsync(CancellationToken.None);
        await synchronizer.Exhausted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }
}
