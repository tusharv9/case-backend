using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>Agent Assignment → Needs an agent: which open cases nobody is working (and why), and the refusal of a hand-picked agent.</summary>
/// <remarks>
/// Nothing on a case says "needs an agent"; the list is worked out from who holds each case now. Getting it wrong either hides a
/// case a customer is waiting on (a lead holding it, or an agent who left) or floods supervisors with cases an agent is working.
/// A team lead who also takes cases is a working agent, not a holder; a finished case never waits. The refusal keeps the
/// configuration in charge: under an automatic algorithm a form (or a direct call) cannot pick the agent itself.
/// </remarks>
public class AssignmentQueueTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Guid lead = Guid.NewGuid(), agent = Guid.NewGuid(), leaver = Guid.NewGuid(), gone = Guid.NewGuid(), workingLead = Guid.NewGuid();
    private readonly Guid cards = Guid.NewGuid(), loans = Guid.NewGuid();

    public AssignmentQueueTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"assign-queue-{Guid.NewGuid()}").Options);
        // Everyone is here (Case Management open) unless a test says otherwise.
        User U(Guid id, string name, bool active = true) => new() { Id = id, Name = name, Email = $"{id:N}@x.test", Role = "Agent", IsActive = active, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        db.Users.AddRange(U(lead, "Lena Lead"), U(agent, "Ann Agent"), U(leaver, "Leo Left"), U(gone, "Gil Gone", active: false), U(workingLead, "Wes Working Lead"));
        db.Teams.AddRange(
            new Team { Id = cards, Name = "Cards", Code = "CD", OwnerId = lead, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Team { Id = loans, Name = "Loans", Code = "LN", OwnerId = workingLead, IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.AddRange(
            Member(cards, lead, assignable: false),          // the lead only holds
            Member(cards, agent),
            Member(cards, gone),
            Member(loans, workingLead, assignable: true));   // this lead takes cases too
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static TeamMember Member(Guid team, Guid user, bool assignable = true) => new()
    {
        Id = Guid.NewGuid(), TeamId = team, UserId = user, MemberRole = "Agent", IsActive = true, IsAssignable = assignable, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
    };

    private Case Case(string number, Guid team, Guid owner, CaseStatus status = CaseStatus.Open)
    {
        var c = new Case { CaseNumber = number, Title = $"Case {number}", Severity = "High", TeamId = team, OwnerId = owner, Status = status, CreatedAt = DateTime.UtcNow };
        db.Cases.Add(c);
        db.SaveChanges();
        return c;
    }

    private Task<AssignmentQueueDto> Read(string? reason = null) =>
        AssignmentQueue.ReadAsync(db, new CaseAccessScope(db), new AssignmentQueueQuery(null, reason, null));

    [Fact]
    public async Task A_case_held_by_a_lead_who_takes_no_cases_and_one_left_with_a_former_member_wait_for_an_agent()
    {
        Case("C-1", cards, lead);
        Case("C-2", cards, leaver);   // never in the team (any more)
        Case("C-3", cards, gone);     // still listed, but no longer an active person

        var queue = await Read();

        Assert.Equal((3, 1, 2), (queue.NeedsAgentCount, queue.HeldByLeadCount, queue.AgentLeftCount));
        Assert.Equal(AssignmentQueue.HeldByLead, queue.Items.Single(i => i.CaseNumber == "C-1").Reason);
        Assert.Equal(("Leo Left", AssignmentQueue.AgentLeft), queue.Items.Where(i => i.CaseNumber == "C-2").Select(i => (i.HolderName, i.Reason)).Single());
    }

    [Fact]
    public async Task A_case_an_agent_is_working_a_lead_who_takes_cases_or_a_finished_case_never_waits()
    {
        Case("C-4", cards, agent);
        Case("C-5", loans, workingLead);
        Case("C-6", cards, lead, CaseStatus.Resolved);

        var queue = await Read();

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.NeedsAgentCount);
    }

    [Fact]
    public async Task The_why_filter_narrows_the_rows_but_the_counts_stay_whole()
    {
        Case("C-1", cards, lead);
        Case("C-2", cards, leaver);

        var held = await Read(AssignmentQueue.HeldByLead);

        Assert.Equal("C-1", Assert.Single(held.Items).CaseNumber);
        Assert.Equal((2, 1), (held.NeedsAgentCount, held.Total));
    }

    [Fact]
    public async Task The_person_a_case_was_escalated_to_or_an_agent_active_in_another_team_has_not_left()
    {
        var escalated = Case("C-7", cards, leaver);
        db.CaseEscalations.Add(new CaseEscalation { CaseId = escalated.Id, ToLevel = 2, ToLevelName = "Level 2", Type = EscalationTypes.Manual, ReasonText = "x", ToOwnerId = leaver, CreatedAt = DateTime.UtcNow });
        Case("C-8", cards, workingLead);   // leads Loans and takes cases there: working this one from another team
        db.SaveChanges();

        var queue = await Read();

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.AgentLeftCount);
    }

    [Fact]
    public async Task An_agent_who_is_away_leaves_their_case_waiting_as_unavailable_but_a_busy_one_does_not()
    {
        Case("C-9", cards, agent);
        Case("C-10", loans, workingLead);
        db.Users.Single(u => u.Id == agent).LastSeenAt = DateTime.UtcNow.AddMinutes(-10);   // closed the tab ten minutes ago
        db.Users.Single(u => u.Id == workingLead).Status = UserStatus.Busy;
        db.SaveChanges();

        var queue = await Read();
        var unavailable = await Read(AssignmentQueue.AgentUnavailable);

        Assert.Equal(("C-9", AssignmentQueue.AgentUnavailable, "Ann Agent"), queue.Items.Select(i => (i.CaseNumber, i.Reason, i.HolderName)).Single());
        Assert.Equal((1, 1, 0, 0), (queue.NeedsAgentCount, queue.AgentUnavailableCount, queue.AgentLeftCount, queue.HeldByLeadCount));
        Assert.Equal("C-9", Assert.Single(unavailable.Items).CaseNumber);
    }

    [Fact]
    public void An_agent_chosen_under_an_automatic_rule_is_refused_naming_the_team_and_the_rule()
    {
        var rule = new AssignmentRuleDto(cards, "Cards", AssignmentAlgorithms.RoundRobin, AssignmentCaseSources.Default, OwnerRequired: false);

        var error = Assert.Single(AssignmentRules.AgentNotChosenByHand(rule, "assigneeId").Errors);

        Assert.Equal("assigneeId", error.Field);
        Assert.Contains("Cards are given out automatically (round robin)", error.Message);
    }
}
