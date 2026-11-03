using System;
using System.Linq;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// The case status lifecycle as a table. Every status endpoint and the case detail's "what may I do" answer read these
/// rules, so a wrong row here is a wrong button and a wrong server answer at once.
/// </summary>
public class StatusTransitionTests
{
    [Theory]
    [InlineData(CaseStatus.Open, CaseStatus.InProgress)]
    [InlineData(CaseStatus.InProgress, CaseStatus.WaitingOnCustomer)]
    [InlineData(CaseStatus.WaitingOnCustomer, CaseStatus.InProgress)]
    [InlineData(CaseStatus.Escalated, CaseStatus.InProgress)]
    [InlineData(CaseStatus.Escalated, CaseStatus.WaitingOnCustomer)]
    public void The_status_menu_allows_the_lifecycle_moves(CaseStatus from, CaseStatus to)
    {
        Assert.True(CaseService.IsValidTransition(from, to));
    }

    [Theory]
    [InlineData(CaseStatus.InProgress, CaseStatus.Open)]          // never back to Open once work has started
    [InlineData(CaseStatus.WaitingOnCustomer, CaseStatus.Open)]
    [InlineData(CaseStatus.Escalated, CaseStatus.Open)]
    [InlineData(CaseStatus.Open, CaseStatus.WaitingOnCustomer)]   // waiting needs work to have started
    [InlineData(CaseStatus.Resolved, CaseStatus.Open)]            // a resolved case comes back only through Reopen
    [InlineData(CaseStatus.Resolved, CaseStatus.InProgress)]
    [InlineData(CaseStatus.Resolved, CaseStatus.WaitingOnCustomer)]
    [InlineData(CaseStatus.Closed, CaseStatus.InProgress)]
    [InlineData(CaseStatus.Cancelled, CaseStatus.InProgress)]
    [InlineData(CaseStatus.InProgress, CaseStatus.Resolved)]      // Resolve and Escalate are workflows, not menu moves
    [InlineData(CaseStatus.InProgress, CaseStatus.Escalated)]
    public void Moves_outside_the_lifecycle_are_refused(CaseStatus from, CaseStatus to)
    {
        Assert.False(CaseService.IsValidTransition(from, to));
    }

    [Fact]
    public void Resolve_and_escalate_start_only_from_working_statuses()
    {
        Assert.True(CaseWorkflow.CanResolveFrom(CaseStatus.Open));   // passes through In Progress
        Assert.True(CaseWorkflow.CanResolveFrom(CaseStatus.WaitingOnCustomer));
        Assert.True(CaseWorkflow.CanResolveFrom(CaseStatus.Escalated));
        Assert.False(CaseWorkflow.CanResolveFrom(CaseStatus.Resolved));

        Assert.False(CaseWorkflow.CanEscalateFrom(CaseStatus.Open));
        Assert.True(CaseWorkflow.CanEscalateFrom(CaseStatus.InProgress));
        Assert.True(CaseWorkflow.CanEscalateFrom(CaseStatus.WaitingOnCustomer));
        Assert.True(CaseWorkflow.CanEscalateFrom(CaseStatus.Escalated));
        Assert.False(CaseWorkflow.CanEscalateFrom(CaseStatus.Resolved));
    }

    [Fact]
    public void An_escalated_case_is_locked_to_everyone_but_its_holder_and_supervisors()
    {
        var holder = Guid.NewGuid();
        var previous = Guid.NewGuid();
        var c = new Case { Status = CaseStatus.Escalated, OwnerId = holder };

        Assert.NotNull(CaseWorkflow.LockReason(c, previous, supervises: false, "Amina"));
        Assert.Null(CaseWorkflow.LockReason(c, holder, supervises: false));
        Assert.Null(CaseWorkflow.LockReason(c, previous, supervises: true));
        Assert.Null(CaseWorkflow.LockReason(new Case { Status = CaseStatus.InProgress, OwnerId = holder }, previous, supervises: false));
    }

    [Fact]
    public void A_resolved_case_offers_no_status_changes()
    {
        Assert.Empty(CaseService.GetAllowedTransitions(CaseStatus.Resolved));
        Assert.Equal(new[] { nameof(CaseStatus.InProgress) }, CaseService.GetAllowedTransitions(CaseStatus.Open).ToArray());
    }
}
