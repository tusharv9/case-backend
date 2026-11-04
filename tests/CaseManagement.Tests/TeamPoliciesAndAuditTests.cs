using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// Teams serving several departments, the administrator's team policies, the Case Audit Trail's filters and export, and the
/// Team Monitor's nudge.
/// </summary>
/// <remarks>
/// The policies are checks across every team ("one team per person"), so they are easy to get subtly wrong: a team lead's own
/// member row must not count as a second membership, tightening a policy must never silently drop anyone from a team, and a
/// field hidden from the form must never be wiped on save. The audit export is read by people outside the team, so it must hold
/// exactly the filtered rows, never more than the caller may see, and stop at the system cap. A nudge reaches a real person,
/// so it must only reach someone the caller supervises, and only once per cooldown however often the button is clicked.
/// </remarks>
public class TeamPoliciesAndAuditTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly Guid lead = Guid.NewGuid(), ann = Guid.NewGuid(), bob = Guid.NewGuid(), cy = Guid.NewGuid();
    private readonly Guid cards = Guid.NewGuid(), loans = Guid.NewGuid();
    private readonly Guid retail = Guid.NewGuid(), corporate = Guid.NewGuid(), closed = Guid.NewGuid();

    public TeamPoliciesAndAuditTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"team-policies-{Guid.NewGuid()}").Options;
        db = new AppDbContext(options);

        User U(Guid id, string name) => new() { Id = id, Name = name, Email = name.ToLower().Replace(' ', '.') + "@x.test", Role = "Agent", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(U(lead, "Lena Lead"), U(ann, "Ann Agent"), U(bob, "Bob Agent"), U(cy, "Cy Outsider"));
        db.Teams.AddRange(
            new Team { Id = cards, Name = "Cards", Code = "CD", OwnerId = lead, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Team { Id = loans, Name = "Loans", Code = "LN", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.AddRange(
            Member(cards, lead, assignable: false),
            Member(cards, ann),
            Member(loans, cy));
        db.Departments.AddRange(
            new Department { Id = retail, Name = "Retail Banking", Code = "RB", IsActive = true, DisplayOrder = 1, CreatedAt = DateTime.UtcNow },
            new Department { Id = corporate, Name = "Corporate Banking", Code = "CB", IsActive = true, DisplayOrder = 2, CreatedAt = DateTime.UtcNow },
            new Department { Id = closed, Name = "Closed Unit", Code = "CU", IsActive = false, DisplayOrder = 3, CreatedAt = DateTime.UtcNow });
        db.BusinessCalendarSettings.Add(new BusinessCalendarSetting { Id = Guid.NewGuid(), TimeZoneId = "UTC", CreatedAt = DateTime.UtcNow });
        foreach (var d in Enum.GetValues<DayOfWeek>())
            db.BusinessHours.Add(new BusinessHour { DayOfWeek = d, DayName = d.ToString(), IsEnabled = true, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromHours(24), CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    private static TeamMember Member(Guid team, Guid user, bool assignable = true) => new()
    {
        Id = Guid.NewGuid(), TeamId = team, UserId = user, MemberRole = "Agent", IsActive = true, IsAssignable = assignable, JoinedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
    };

    private TeamService Teams()
    {
        var routing = new Mock<IRoutingEngineService>();
        routing.Setup(r => r.GetAssignmentConfigAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssignmentConfigDto { Algorithm = "RoundRobin", MaxConcurrentCapacity = 5 });
        return new TeamService(db, routing.Object, new TeamSettingsService(db));
    }

    private async Task SetPolicy(bool members = true, bool leads = true, bool departments = true, bool empty = true, bool leadElsewhere = true,
        bool requireLead = false, bool deactivateWithOpen = true)
    {
        await new TeamSettingsService(db).SaveAsync(new UpdateTeamSettingsDto
        {
            AllowMultipleTeamMembership = members, AllowMultipleTeamLeads = leads, AllowMultipleDepartments = departments,
            AllowTeamsWithoutMembers = empty, AllowLeadInOtherTeams = leadElsewhere, RequireTeamLead = requireLead,
            AllowDeactivateWithOpenCases = deactivateWithOpen,
        }, lead);
        db.ChangeTracker.Clear();
    }

    /// <summary>One field of the Create/Edit Team form, as Case Settings → Teams → Create Team Form stores it.</summary>
    private void FormField(string apiField, bool visible = true, bool required = false, string type = "Text", bool custom = false, string? lookup = null, string? regex = null)
    {
        db.FieldConfigurations.Add(new FieldConfiguration
        {
            ModuleKey = "CaseManagement", SectionKey = TeamFormFields.SectionKey, ApiField = apiField, DisplayLabel = apiField,
            IsVisible = visible, IsRequired = required, FieldType = type, IsCustomField = custom, LookupTypeCode = lookup,
            ValidationRegex = regex, DisplayOrder = db.FieldConfigurations.Local.Count + 1, CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ------------------------------------------------------------------------------------------------ departments

    [Fact]
    public async Task A_team_can_handle_several_departments_listed_in_display_order_and_they_are_then_taken()
    {
        var both = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "Wealth", DepartmentIds = new() { corporate, retail } }, lead);
        db.ChangeTracker.Clear();

        var taken = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "Mortgages", DepartmentIds = new() { retail } }, lead));

        Assert.Equal(new[] { "Retail Banking", "Corporate Banking" }, both.Departments.Select(d => d.Name));
        Assert.Contains(taken.Errors, e => e.Message.Contains("Retail Banking is already assigned to the team 'Wealth'"));
    }

    [Fact]
    public async Task Editing_a_team_without_sending_its_departments_keeps_them_and_sending_a_new_list_replaces_them()
    {
        var team = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "Wealth", DepartmentIds = new() { retail, corporate } }, lead);
        db.ChangeTracker.Clear();

        var renamed = await Teams().UpdateTeamAsync(team.Id, new UpdateTeamDto { Name = "Wealth Desk" }, lead);
        db.ChangeTracker.Clear();
        var narrowed = await Teams().UpdateTeamAsync(team.Id, new UpdateTeamDto { DepartmentIds = new() { corporate } }, lead);

        Assert.Equal(2, renamed.Departments.Count);
        Assert.Equal(new[] { corporate }, narrowed.Departments.Select(d => d.Id));
    }

    [Fact]
    public async Task An_inactive_department_cannot_be_added_but_a_team_already_linked_to_it_keeps_the_link()
    {
        db.TeamDepartments.Add(new TeamDepartment { TeamId = cards, DepartmentId = closed });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var kept = await Teams().UpdateTeamAsync(cards, new UpdateTeamDto { DepartmentIds = new() { closed, retail } }, lead);
        db.ChangeTracker.Clear();
        var refused = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "New", DepartmentIds = new() { closed } }, lead));

        Assert.Equal(2, kept.Departments.Count);
        Assert.Contains(refused.Errors, e => e.Field == "departments" && e.Message.Contains("inactive"));
    }

    [Fact]
    public async Task With_one_department_per_team_a_second_department_is_refused()
    {
        await SetPolicy(departments: false);

        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().CreateTeamAsync(new CreateTeamDto { Name = "Wealth", DepartmentIds = new() { retail, corporate } }, lead));

        Assert.Contains(error.Errors, e => e.Field == "departments");
    }

    [Fact]
    public async Task A_department_still_served_by_a_team_cannot_be_deleted()
    {
        db.TeamDepartments.Add(new TeamDepartment { TeamId = cards, DepartmentId = retail });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DepartmentService(db).DeleteAsync(retail, lead));

        Assert.Contains("Cards", error.Message);
        Assert.True(await db.Departments.AnyAsync(d => d.Id == retail));
    }

    [Fact]
    public async Task Two_departments_cannot_share_a_name_or_a_code()
    {
        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            new DepartmentService(db).CreateAsync(new SaveDepartmentDto { Name = "retail banking", Code = "cb" }, lead));

        Assert.Contains(error.Errors, e => e.Field == "name");
        Assert.Contains(error.Errors, e => e.Field == "code");
    }

    // ------------------------------------------------------------------------------------------------ form rules

    [Fact]
    public async Task A_required_field_must_be_filled_and_a_hidden_field_is_never_required()
    {
        FormField(TeamFormFields.Departments, required: true, type: "MultiSelect");
        FormField(TeamFormFields.TeamLead, visible: false, required: true, type: "Dropdown");

        var error = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().CreateTeamAsync(new CreateTeamDto { Name = "No Dept" }, lead));
        var created = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "With Dept", DepartmentIds = new() { retail } }, lead);

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.Departments);
        Assert.DoesNotContain(error.Errors, e => e.Field == TeamFormFields.TeamLead);   // hidden, so not required even though it was ticked
        Assert.Null(created.TeamLeadId);
    }

    [Fact]
    public async Task A_built_in_field_format_from_the_form_configuration_is_enforced_on_the_team_code()
    {
        FormField(TeamFormFields.Code, regex: "^[A-Z]{2,4}$");

        var error = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().CreateTeamAsync(new CreateTeamDto { Name = "Wealth", Code = "W1" }, lead));
        var created = await Teams().CreateTeamAsync(new CreateTeamDto { Name = "Wealth", Code = "WLT" }, lead);

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.Code);
        Assert.Equal("WLT", created.Code);
    }

    [Fact]
    public async Task A_custom_team_field_is_validated_against_its_list_stored_and_kept_when_an_edit_does_not_send_it()
    {
        db.LookupTypes.Add(new LookupType { Code = "REGION", Name = "Region" });
        db.LookupValues.AddRange(
            new LookupValue { TypeCode = "REGION", Value = "North", Label = "North", IsActive = true },
            new LookupValue { TypeCode = "REGION", Value = "South", Label = "South", IsActive = true });
        await db.SaveChangesAsync();
        FormField("regions", required: true, type: "MultiSelect", custom: true, lookup: "REGION");

        var bad = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().CreateTeamAsync(new CreateTeamDto
        {
            Name = "Field Ops", CustomAttributes = new() { ["regions"] = "[\"North\",\"Mars\"]" },
        }, lead));
        var created = await Teams().CreateTeamAsync(new CreateTeamDto
        {
            Name = "Field Ops", CustomAttributes = new() { ["regions"] = "[\"north\",\"South\"]" },
        }, lead);
        db.ChangeTracker.Clear();
        var renamed = await Teams().UpdateTeamAsync(created.Id, new UpdateTeamDto { Name = "Field Operations" }, lead);
        var unknown = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().UpdateTeamAsync(created.Id, new UpdateTeamDto
        {
            CustomAttributes = new() { ["shoeSize"] = "9" },
        }, lead));

        Assert.Contains(bad.Errors, e => e.Field == "regions" && e.Message.Contains("Mars"));
        Assert.Equal("[\"North\",\"South\"]", created.CustomAttributes["regions"]);   // stored in the list's own spelling
        Assert.Equal("[\"North\",\"South\"]", renamed.CustomAttributes["regions"]);
        Assert.Contains(unknown.Errors, e => e.Field == "shoeSize");
    }

    [Fact]
    public async Task Saving_the_rules_needs_every_rule_and_starts_from_the_behaviour_before_rules_existed()
    {
        var defaults = await new TeamSettingsService(db).GetAsync();
        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            new TeamSettingsService(db).SaveAsync(new UpdateTeamSettingsDto { AllowMultipleTeamMembership = false }, lead));

        Assert.True(defaults.AllowMultipleTeamMembership && defaults.AllowMultipleTeamLeads && defaults.AllowMultipleDepartments);
        Assert.True(defaults.AllowTeamsWithoutMembers && defaults.AllowLeadInOtherTeams && defaults.AllowDeactivateWithOpenCases);
        Assert.False(defaults.RequireTeamLead);
        Assert.Contains(error.Errors, e => e.Field == "requireTeamLead");
    }

    // ------------------------------------------------------------------------------------------------ membership policies

    [Fact]
    public async Task With_one_team_per_person_someone_already_in_a_team_cannot_join_another()
    {
        await SetPolicy(members: false);

        var error = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Teams().UpdateTeamAsync(loans, new UpdateTeamDto { Members = new() { new TeamMemberInputDto { UserId = cy }, new TeamMemberInputDto { UserId = ann } } }, lead));
        var viaAdd = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().AddMemberAsync(loans, new AddTeamMemberDto { UserId = ann }, lead));

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.Members && e.Message.Contains("Ann Agent (Cards)"));
        Assert.NotNull(viaAdd);
        Assert.False(await db.TeamMembers.AnyAsync(m => m.TeamId == loans && m.UserId == ann));
    }

    [Fact]
    public async Task A_leads_own_member_row_does_not_count_as_a_second_team()
    {
        await SetPolicy(members: false);

        // Lena leads Cards (and so has a member row there); joining Loans as a member is her ONE membership.
        await Teams().AddMemberAsync(loans, new AddTeamMemberDto { UserId = lead }, lead);

        Assert.True(await db.TeamMembers.AnyAsync(m => m.TeamId == loans && m.UserId == lead && m.IsActive));
    }

    [Fact]
    public async Task With_one_team_per_lead_the_lead_of_one_team_cannot_lead_another()
    {
        await SetPolicy(leads: false);

        var error = await Assert.ThrowsAsync<FieldValidationException>(() => Teams().UpdateTeamAsync(loans, new UpdateTeamDto { TeamLeadId = lead }, lead));

        Assert.Contains(error.Errors, e => e.Field == TeamFormFields.TeamLead && e.Message.Contains("already leads Cards"));
        Assert.Null(await db.Teams.Where(d => d.Id == loans).Select(d => d.OwnerId).FirstAsync());
    }

    [Fact]
    public async Task A_policy_cannot_be_tightened_while_teams_break_it_and_nobody_is_removed()
    {
        db.TeamMembers.Add(Member(loans, ann));   // Ann is now in Cards and Loans
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<FieldValidationException>(() => SetPolicy(members: false));

        Assert.Contains(error.Errors, e => e.Field == "allowMultipleTeamMembership" && e.Message.Contains("Ann Agent"));
        Assert.Equal(2, await db.TeamMembers.CountAsync(m => m.UserId == ann && m.IsActive));
        Assert.True((await new TeamSettingsService(db).GetAsync()).AllowMultipleTeamMembership);
    }

    [Fact]
    public async Task A_save_based_on_an_old_version_of_the_settings_is_refused()
    {
        var loaded = await new TeamSettingsService(db).GetAsync();
        await SetPolicy(departments: false);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new TeamSettingsService(db).SaveAsync(new UpdateTeamSettingsDto
        {
            AllowMultipleTeamMembership = true, AllowMultipleTeamLeads = true, AllowMultipleDepartments = true, AllowTeamsWithoutMembers = true,
            AllowLeadInOtherTeams = true, RequireTeamLead = false, AllowDeactivateWithOpenCases = true, ExpectedVersion = loaded.Version.AddMinutes(-1),
        }, lead));
    }

    // ------------------------------------------------------------------------------------------------ audit trail

    private async Task SeedAuditAsync()
    {
        var cardCase = new Case { Id = Guid.NewGuid(), CaseNumber = "C-00001", Title = "Lost card", TeamId = cards, OwnerId = ann, Severity = "High", CreatedAt = DateTime.UtcNow };
        var loanCase = new Case { Id = Guid.NewGuid(), CaseNumber = "L-00002", Title = "Loan query", TeamId = loans, OwnerId = cy, Severity = "Low", CreatedAt = DateTime.UtcNow };
        db.Cases.AddRange(cardCase, loanCase);
        db.TeamDepartments.Add(new TeamDepartment { TeamId = loans, DepartmentId = corporate });
        var t = DateTime.UtcNow;
        CaseEvent E(Case c, Guid user, EventType type, string message, int minutesAgo) =>
            new() { Id = Guid.NewGuid(), CaseId = c.Id, UserId = user, EventType = type, Message = message, CreatedAt = t.AddMinutes(-minutesAgo) };
        db.CaseEvents.AddRange(
            E(cardCase, ann, EventType.Create, "Case created", 300),
            E(cardCase, ann, EventType.Note, "Status changed to In Progress", 200),
            E(cardCase, ann, EventType.Note, "=HYPERLINK(\"x\") called the customer", 100),
            E(loanCase, cy, EventType.Create, "Case created", 50),
            E(loanCase, cy, EventType.Assign, "Assigned to Cy", 10));
        db.CaseEvents.Add(new CaseEvent { Id = Guid.NewGuid(), UserId = lead, EventType = EventType.Other, Message = "Teams settings updated.", Module = "Configurable Settings", ActionType = "UPDATE", CreatedAt = t });
        await db.SaveChangesAsync();
    }

    private AuditLogService Audit(ICaseAccessScope? access = null)
    {
        var cache = new ConfigCache(new MemoryCache(new MemoryCacheOptions()));
        var clock = new SlaClockProvider(new BusinessTimeService(db, cache), new EscalationService(db, cache));
        return new AuditLogService(db, new CaseRepository(db, access: access), clock);
    }

    [Fact]
    public async Task Person_team_department_action_and_time_filters_combine()
    {
        await SeedAuditAsync();
        var audit = Audit();

        var annInCards = await audit.ListAsync(new AuditFilter(ActorId: ann, TeamId: cards), 1, 50);
        var statusChanges = await audit.ListAsync(new AuditFilter(ActionType: "status"), 1, 50);
        var corporateDept = await audit.ListAsync(new AuditFilter(DepartmentId: corporate), 1, 50);
        var lastHour = await audit.ListAsync(new AuditFilter(From: DateTime.UtcNow.AddMinutes(-60)), 1, 50);
        var byCase = await audit.ListAsync(new AuditFilter(CaseNumber: "l-000"), 1, 50);

        Assert.Equal(3, annInCards.TotalCount);
        Assert.Equal("Status changed to In Progress", Assert.Single(statusChanges.Items).Description);
        Assert.Equal(2, corporateDept.TotalCount);                     // the Loans team serves Corporate Banking
        Assert.Equal(3, lastHour.TotalCount);                          // 50 and 10 minutes ago, and the settings change
        Assert.All(byCase.Items, e => Assert.Equal("L-00002", e.CaseNumber));
    }

    [Fact]
    public async Task The_export_holds_the_newest_rows_up_to_the_chosen_number_and_says_when_more_matched()
    {
        await SeedAuditAsync();

        var export = await Audit().ExportAsync(new AuditFilter(TeamId: cards), 2);
        var lines = export.Csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, export.RowCount);
        Assert.True(export.Truncated);                                 // three matched, two were asked for
        Assert.StartsWith("Timestamp,Action,Performed By", lines[0]);
        Assert.Contains("'=HYPERLINK", lines[1]);                      // newest first, and never runs as a formula
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task Someone_who_does_not_see_every_team_never_exports_other_teams_history_or_configuration_changes()
    {
        await SeedAuditAsync();
        var scope = new Mock<ICaseAccessScope>();
        scope.SetupGet(s => s.SeesAll).Returns(false);
        scope.Setup(s => s.Apply(It.IsAny<IQueryable<Case>>())).Returns((IQueryable<Case> q) => q.Where(c => c.TeamId == cards));
        scope.Setup(s => s.Apply(It.IsAny<IQueryable<Case>>(), It.IsAny<AppDbContext>())).Returns((IQueryable<Case> q, AppDbContext _) => q.Where(c => c.TeamId == cards));

        var export = await Audit(scope.Object).ExportAsync(new AuditFilter(), 100);

        Assert.Equal(3, export.RowCount);
        Assert.DoesNotContain("L-00002", export.Csv);
        Assert.DoesNotContain("Teams settings updated", export.Csv);
    }

    [Fact]
    public async Task What_a_background_job_recorded_under_a_persons_name_is_not_counted_as_their_activity()
    {
        await SeedAuditAsync();
        var card = await db.Cases.FirstAsync(c => c.CaseNumber == "C-00001");
        db.CaseEvents.Add(new CaseEvent { Id = Guid.NewGuid(), CaseId = card.Id, UserId = ann, EventType = EventType.Escalate, CreatedAt = DateTime.UtcNow,
            Message = "[Automatic Escalation] Escalated to Level 2 (no active user found — owner unchanged) from Ann Agent." });
        await db.SaveChangesAsync();

        var annOnly = await Audit().ListAsync(new AuditFilter(ActorId: ann), 1, 50);
        var everything = await Audit().ListAsync(new AuditFilter(TeamId: cards), 1, 50);

        Assert.Equal(3, annOnly.TotalCount);                                     // her three real actions, not the escalation
        Assert.Equal(AutomaticEvents.ActorName, everything.Items.First().ActorName);   // the trail shows it as the system's
    }

    [Fact]
    public async Task An_export_must_ask_for_at_least_one_row()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Audit().ExportAsync(new AuditFilter(), 0));
    }

    // ------------------------------------------------------------------------------------------------ nudge

    private TeamMonitoringService Monitor(Guid caller, bool seesAll, int cooldownSeconds = 120)
    {
        var scope = new Mock<ICaseAccessScope>();
        scope.SetupGet(s => s.SeesAll).Returns(seesAll);
        var user = new Mock<ICurrentUserAccessor>();
        user.SetupGet(u => u.LocalUserId).Returns(caller);
        return new TeamMonitoringService(db, null!, null!, Options.Create(new TeamMonitoringOptions { NudgeCooldownSeconds = cooldownSeconds }), scope.Object, user.Object);
    }

    [Fact]
    public async Task A_nudge_notifies_the_agent_names_who_sent_it_and_is_audited()
    {
        var result = await Monitor(lead, seesAll: false).NudgeAgentAsync(ann, null, lead);

        var note = await db.Notifications.SingleAsync(n => n.RecipientUserId == ann);
        Assert.Equal("Nudge from Lena Lead", note.Title);
        Assert.Equal("Nudge sent to Ann Agent.", result.Message);
        Assert.True(await db.CaseEvents.AnyAsync(e => e.ActionType == "NUDGE" && e.UserId == lead && e.EntityName == "Ann Agent"));
    }

    [Fact]
    public async Task Clicking_nudge_again_within_the_cooldown_does_not_send_a_second_nudge()
    {
        var monitor = Monitor(lead, seesAll: false);
        await monitor.NudgeAgentAsync(ann, null, lead);

        var error = await Assert.ThrowsAsync<NudgeCooldownException>(() => monitor.NudgeAgentAsync(ann, null, lead));

        Assert.InRange(error.RetryAfterSeconds, 1, 120);
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.RecipientUserId == ann));
    }

    [Fact]
    public async Task A_lead_cannot_nudge_or_look_up_someone_outside_the_teams_they_lead_or_belong_to()
    {
        var monitor = Monitor(lead, seesAll: false);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => monitor.NudgeAgentAsync(cy, null, lead));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => monitor.EnsureCanMonitorAsync(cy));
        await monitor.EnsureCanMonitorAsync(ann);
        await Monitor(lead, seesAll: true).EnsureCanMonitorAsync(cy);   // someone who sees every team may
        Assert.False(await db.Notifications.AnyAsync(n => n.RecipientUserId == cy));
    }
}
