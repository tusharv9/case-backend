namespace CaseManagement.Api.Services;

public interface ISlaClockProvider
{
    /// <summary>A clock bound to the current working calendar and escalation policy.</summary>
    Task<SlaClock> GetAsync(CancellationToken ct = default);
}

public class SlaClockProvider : ISlaClockProvider
{
    private readonly IBusinessTimeService _businessTime;
    private readonly IEscalationService _escalation;

    public SlaClockProvider(IBusinessTimeService businessTime, IEscalationService escalation)
    {
        _businessTime = businessTime;
        _escalation = escalation;
    }

    public async Task<SlaClock> GetAsync(CancellationToken ct = default)
    {
        var calendar = await _businessTime.GetCalendarAsync(ct);
        var policy = await _escalation.GetPolicyAsync(ct);
        return new SlaClock(calendar, policy.ReminderPercent);
    }
}
