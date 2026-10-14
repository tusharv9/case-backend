using System;
using System.Linq;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Xunit;

namespace CaseManagement.Tests;

public class StatusTransitionTests
{
    [Theory]
    [InlineData(CaseStatus.Open, CaseStatus.InProgress, true)]
    [InlineData(CaseStatus.Open, CaseStatus.WaitingOnCustomer, true)]
    [InlineData(CaseStatus.InProgress, CaseStatus.Open, true)]
    [InlineData(CaseStatus.InProgress, CaseStatus.WaitingOnCustomer, true)]
    [InlineData(CaseStatus.WaitingOnCustomer, CaseStatus.Open, true)]
    [InlineData(CaseStatus.WaitingOnCustomer, CaseStatus.InProgress, true)]
    [InlineData(CaseStatus.Escalated, CaseStatus.InProgress, true)]
    public void IsValidTransition_AllowedTransitions_ReturnsTrue(CaseStatus from, CaseStatus to, bool expected)
    {
        var result = CaseService.IsValidTransition(from, to);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(CaseStatus.Resolved, CaseStatus.Open, false)]
    [InlineData(CaseStatus.Resolved, CaseStatus.InProgress, false)]
    [InlineData(CaseStatus.Closed, CaseStatus.Open, false)]
    [InlineData(CaseStatus.Closed, CaseStatus.InProgress, false)]
    [InlineData(CaseStatus.Cancelled, CaseStatus.Open, false)]
    [InlineData(CaseStatus.Cancelled, CaseStatus.InProgress, false)]
    public void IsValidTransition_TerminalStatuses_CannotTransitionDirectly(CaseStatus from, CaseStatus to, bool expected)
    {
        var result = CaseService.IsValidTransition(from, to);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetAllowedTransitions_ForResolved_ReturnsEmpty()
    {
        var transitions = CaseService.GetAllowedTransitions(CaseStatus.Resolved);
        Assert.Empty(transitions);
    }

    [Fact]
    public void GetAllowedTransitions_ForOpen_ReturnsExpectedTargets()
    {
        var transitions = CaseService.GetAllowedTransitions(CaseStatus.Open).ToList();
        Assert.Contains(nameof(CaseStatus.InProgress), transitions);
        Assert.Contains(nameof(CaseStatus.WaitingOnCustomer), transitions);
    }
}
