namespace CaseManagement.Api.Services.EmailIntake;

using Microsoft.Extensions.Options;

/// <summary>
/// Reads the mailboxes every <see cref="EmailIntakeOptions.PollSeconds"/> and works through what arrived. Safe on several servers
/// sharing one database: each mailbox read, each email and each acknowledgement retry is claimed with a version check first
/// (<see cref="EmailIntakeProcessor"/>), so nothing is read, turned into a case or answered twice.
/// </summary>
public sealed class EmailIntakeWorker(IServiceScopeFactory scopes, IOptions<EmailIntakeOptions> options, ILogger<EmailIntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Email intake is switched off on this instance (EmailIntake:Enabled=false).");
            return;
        }
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);   // let startup (database checks, pool warm-up) finish first
        var interval = TimeSpan.FromSeconds(Math.Max(15, options.Value.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EmailIntakeProcessor>().RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Email intake cycle failed."); }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
