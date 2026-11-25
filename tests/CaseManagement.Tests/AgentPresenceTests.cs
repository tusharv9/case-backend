using CaseManagement.Api.Models;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// An agent's status as others see it: Available, Busy or Away — what they chose, only while they have Case Management open.
/// </summary>
/// <remarks>
/// Worth testing because this decides who automatic assignment gives cases to: a stale "Available" left behind by a closed
/// browser or a sign-out would keep sending work to someone who is not there.
/// </remarks>
public class AgentPresenceTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(UserStatus.Available, 30, UserStatus.Available)]
    [InlineData(UserStatus.Busy, 30, UserStatus.Busy)]
    [InlineData(UserStatus.Away, 30, UserStatus.Away)]
    [InlineData(UserStatus.Offline, 30, UserStatus.Away)]        // an older screen's "Offline" is Away
    [InlineData(UserStatus.Available, 120, UserStatus.Away)]     // the tab went away without saying so: lapsed
    public void The_chosen_status_counts_only_while_the_agent_is_here(UserStatus chosen, int secondsSinceSeen, UserStatus expected)
    {
        Assert.Equal(expected, AgentPresence.Effective(chosen, Now.AddSeconds(-secondsSinceSeen), Now));
    }

    [Fact]
    public void Someone_who_never_opened_Case_Management_is_Away_and_cannot_be_given_work()
    {
        var user = new User { Status = UserStatus.Available, LastSeenAt = null };

        Assert.Equal(UserStatus.Away, AgentPresence.Effective(user, Now));
        Assert.False(AgentPresence.IsOnline(user, Now));
    }

    [Fact]
    public void Only_three_statuses_are_ever_shown()
    {
        Assert.Equal(new[] { "Available", "Busy", "Away", "Away" },
            new[] { UserStatus.Available, UserStatus.Busy, UserStatus.Away, UserStatus.Offline }.Select(AgentPresence.Label));
    }
}
