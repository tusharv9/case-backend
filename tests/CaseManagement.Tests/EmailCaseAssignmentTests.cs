using CaseManagement.Api.Data;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Strategies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Email cases given out by their own setting (Assign &amp; Reassign → Email cases), and the Manual choice.
/// </summary>
/// <remarks>
/// The business asked for email cases and other cases to be configured independently: email cases by hand while the rest go round
/// robin, or the other way round. Getting the lookup wrong silently applies one source's rule to the other; a Manual setting that
/// still picked an agent would hand a case to someone the team lead never chose. Capacity is not part of the email setting, so it
/// must keep coming from the team's own settings.
/// </remarks>
public class EmailCaseAssignmentTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly User lead = new() { Name = "Lena Lead", Email = "lena@x.test", IsActive = true, Status = UserStatus.Available, CreatedAt = DateTime.UtcNow };
    private readonly User agent = new() { Name = "Arun Agent", Email = "arun@x.test", IsActive = true, Status = UserStatus.Available, CreatedAt = DateTime.UtcNow };
    private readonly Team team;

    public EmailCaseAssignmentTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"email-assign-{Guid.NewGuid()}").Options);
        db.Users.AddRange(lead, agent);
        team = new Team { Name = "Cards", Code = "CD", OwnerId = lead.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        db.TeamMembers.Add(new TeamMember { TeamId = team.Id, UserId = agent.Id, IsActive = true, IsAssignable = true, CreatedAt = DateTime.UtcNow });
        db.AssignmentConfigurations.Add(new AssignmentConfiguration { TeamId = null, Algorithm = AssignmentAlgorithms.RoundRobin, MaxConcurrentCapacity = 5, CreatedAt = DateTime.UtcNow });
        db.ManualAssignmentSettings.Add(new ManualAssignmentSettings { CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private void EmailCases(string algorithm)
    {
        db.AssignmentConfigurations.Add(new AssignmentConfiguration
        {
            TeamId = null, CaseSource = AssignmentCaseSources.Email, Algorithm = algorithm, MaxConcurrentCapacity = 99, CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private Case NewCase(string source) => new()
    {
        CaseNumber = "C-1", Title = "Card blocked", CaseType = "Complaint", Severity = "High", TeamId = team.Id, OwnerId = Guid.Empty,
        SourceChannel = source, CreatedAt = DateTime.UtcNow,
    };

    private RoutingEngineService Engine() =>
        new(db, new IAssignmentStrategy[] { new RoundRobinAssignmentStrategy(db), new LeastOccupancyAssignmentStrategy() }, new AgentPoolService(db));

    [Fact]
    public async Task Email_cases_follow_their_own_algorithm_and_every_other_case_the_default()
    {
        EmailCases(AssignmentAlgorithms.Manual);

        var email = await AssignmentSettings.ResolveForSourceAsync(db, null, team.Id, AssignmentCaseSources.Email);
        var other = await AssignmentSettings.ResolveForSourceAsync(db, null, team.Id, AssignmentCaseSources.Default);

        Assert.Equal((AssignmentAlgorithms.Manual, AssignmentAlgorithms.RoundRobin), (email.Algorithm, other.Algorithm));
        Assert.Equal(5, email.MaxConcurrentCapacity);   // capacity is the team's, never the email row's
    }

    [Fact]
    public async Task Without_an_email_setting_email_cases_are_given_out_like_any_other_including_a_teams_own_override()
    {
        db.AssignmentConfigurations.Add(new AssignmentConfiguration { TeamId = team.Id, Algorithm = AssignmentAlgorithms.LeastOccupancy, MaxConcurrentCapacity = 3, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();

        var email = await AssignmentSettings.ResolveForSourceAsync(db, null, team.Id, AssignmentCaseSources.Email);

        Assert.Equal((AssignmentAlgorithms.LeastOccupancy, 3), (email.Algorithm, email.MaxConcurrentCapacity));
    }

    [Fact]
    public async Task The_global_default_is_never_mistaken_for_the_email_row_whichever_comes_first()
    {
        EmailCases(AssignmentAlgorithms.Manual);

        var global = await AssignmentSettings.ResolveAsync(db, null, null);

        Assert.Equal((AssignmentAlgorithms.RoundRobin, AssignmentCaseSources.Default), (global.Algorithm, global.CaseSource));
    }

    [Fact]
    public async Task Under_Manual_an_email_case_is_held_by_the_team_lead_and_no_agent_is_picked()
    {
        EmailCases(AssignmentAlgorithms.Manual);
        var c = NewCase("Email");

        var result = await Engine().AssignWithinTeamAsync(c, null, team.Id, "test");

        Assert.Equal((lead.Id, lead.Id), (c.OwnerId, result.HeldByUserId));
        Assert.Equal(AssignmentAlgorithms.Manual, result.AlgorithmUsed);
        Assert.Contains("assigned by hand", result.HeldReason);
    }

    [Fact]
    public async Task A_case_from_another_source_still_goes_to_an_agent_automatically_while_email_cases_are_manual()
    {
        EmailCases(AssignmentAlgorithms.Manual);
        var c = NewCase("Voice");

        var result = await Engine().AssignWithinTeamAsync(c, null, team.Id, "test");

        Assert.Equal(agent.Id, c.OwnerId);
        Assert.Null(result.HeldReason);
        Assert.Equal(AssignmentAlgorithms.RoundRobin, result.AlgorithmUsed);
    }

    // ───────────── the one rule both Create Case and ticket conversion use ─────────────

    private Guid Department()
    {
        var d = new Department { Name = "Cards", Code = "CD", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Departments.Add(d);
        db.TeamDepartments.Add(new TeamDepartment { TeamId = team.Id, DepartmentId = d.Id });
        db.SaveChanges();
        return d.Id;
    }

    private void DefaultIs(string algorithm)
    {
        db.AssignmentConfigurations.Single(c => c.TeamId == null && c.CaseSource == AssignmentCaseSources.Default).Algorithm = algorithm;
        db.SaveChanges();
    }

    [Fact]
    public async Task Under_a_Manual_default_every_new_case_needs_an_agent_chosen_whatever_its_source()
    {
        var dept = Department();
        DefaultIs(AssignmentAlgorithms.Manual);

        var voice = await AssignmentRules.ForDepartmentAsync(db, null, dept, "Voice");
        var email = await AssignmentRules.ForDepartmentAsync(db, null, dept, "Email");

        Assert.Equal(("Cards", true), (voice!.TeamName, voice.OwnerRequired));
        Assert.True(email!.OwnerRequired);   // no email setting: email cases follow the default
    }

    [Fact]
    public async Task Email_cases_can_be_manual_while_cases_from_other_sources_are_still_given_out_automatically()
    {
        var dept = Department();
        EmailCases(AssignmentAlgorithms.Manual);

        var voice = await AssignmentRules.ForDepartmentAsync(db, null, dept, "Voice");
        var email = await AssignmentRules.ForDepartmentAsync(db, null, dept, " email ");

        Assert.Equal((AssignmentAlgorithms.RoundRobin, false), (voice!.Algorithm, voice.OwnerRequired));
        Assert.Equal((AssignmentAlgorithms.Manual, true, AssignmentCaseSources.Email), (email!.Algorithm, email.OwnerRequired, email.CaseSource));
    }

    [Fact]
    public async Task The_refusal_names_the_field_the_screen_shows_and_says_why()
    {
        var dept = Department();
        DefaultIs(AssignmentAlgorithms.Manual);
        var rule = (await AssignmentRules.ForDepartmentAsync(db, null, dept, "Voice"))!;

        var error = Assert.Single(AssignmentRules.AgentRequired(rule, "assigneeId").Errors);

        Assert.Equal("assigneeId", error.Field);
        Assert.Contains("choose the agent", error.Message);
    }

    [Fact]
    public async Task A_department_without_an_active_team_has_no_rule()
    {
        var orphan = new Department { Name = "Orphan", Code = "OR", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Departments.Add(orphan);
        db.SaveChanges();

        Assert.Null(await AssignmentRules.ForDepartmentAsync(db, null, orphan.Id, "Voice"));
    }
}
