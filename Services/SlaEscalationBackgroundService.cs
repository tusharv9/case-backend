namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using Microsoft.Extensions.Options;

/// <summary>Runs the SLA monitor on a schedule. All the logic lives in <see cref="ISlaMonitor"/>; this only keeps it going.</summary>
public class SlaEscalationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseInitializationState _databaseState;
    private readonly IOptions<SlaMonitorOptions> _options;
    private readonly ILogger<SlaEscalationBackgroundService> _logger;

    public SlaEscalationBackgroundService(
        IServiceScopeFactory scopeFactory,
        DatabaseInitializationState databaseState,
        IOptions<SlaMonitorOptions> options,
        ILogger<SlaEscalationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _databaseState = databaseState;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("SLA monitor is disabled (SlaMonitor:Enabled = false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.Value.IntervalSeconds));
        _logger.LogInformation("SLA monitor started; cycle every {Interval}.", interval);

        try
        {
            // Wait until migrations/seeding have finished.
            await _databaseState.WhenReady.WaitAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var monitor = scope.ServiceProvider.GetRequiredService<ISlaMonitor>();
                    var report = await monitor.RunCycleAsync(null, stoppingToken);
                    if (report.Ran && (report.Escalations > 0 || report.Reminders > 0))
                        _logger.LogInformation("SLA cycle: {Evaluated} cases, {Reminders} reminders, {Escalations} escalations, {Breaches} breached.",
                            report.Evaluated, report.Reminders, report.Escalations, report.Breaches);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error occurred during SLA monitoring cycle.");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SLA monitor stopped.");
        }
    }
}

public class SlaMonitorOptions
{
    public const string SectionName = "SlaMonitor";

    /// <summary>Turn the background monitor off (e.g. on instances that only serve web traffic).</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 120;
}
