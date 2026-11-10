using CaseManagement.Api.Services;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// The live stream that shows an agent's status change on every open screen at once.
/// </summary>
/// <remarks>
/// Every listener must get the change (one tab per person, plus Team Monitoring and the Teams page), and a tab that closed
/// must stop receiving — otherwise each closed tab would keep a queue alive in the server for ever.
/// </remarks>
public class LiveEventsTests
{
    [Fact]
    public void Every_open_listener_receives_a_published_change()
    {
        var live = new LiveEvents();
        using var first = live.Subscribe();
        using var second = live.Subscribe();

        live.Publish(new LiveEvent("agent-status", new { status = "Offline" }));

        Assert.True(first.Reader.TryRead(out var a));
        Assert.True(second.Reader.TryRead(out var b));
        Assert.Equal("agent-status", a!.Topic);
        Assert.Equal("agent-status", b!.Topic);
    }

    [Fact]
    public void A_closed_listener_is_forgotten()
    {
        var live = new LiveEvents();
        var gone = live.Subscribe();
        gone.Dispose();

        live.Publish(new LiveEvent("agent-status", new { }));

        Assert.True(gone.Reader.Completion.IsCompleted);
        Assert.False(gone.Reader.TryRead(out _));
    }
}
