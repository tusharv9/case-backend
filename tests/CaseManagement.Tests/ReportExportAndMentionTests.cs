using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CaseManagement.Tests;

/// <summary>
/// The Reports CSV and case @mentions. A report is opened in Excel by people outside the team, so a case title must never
/// break a row or run as a formula; and a mention must reach only the people working the case — never someone picked out
/// of the whole directory because part of their name happened to match.
/// </summary>
public class ReportExportAndMentionTests : IDisposable
{
    private readonly AppDbContext db;

    public ReportExportAndMentionTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"mentions-{Guid.NewGuid()}").Options;
        db = new AppDbContext(options);
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    // ------------------------------------------------------------------------------------------------ CSV

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("-1+2", "'-1+2")]
    [InlineData(null, "")]
    public void A_field_is_quoted_when_it_must_be_and_never_starts_a_formula(string? value, string expected)
    {
        Assert.Equal(expected, ReportCsv.Field(value));
    }

    [Fact]
    public void A_report_has_its_header_then_one_crlf_terminated_line_per_case()
    {
        var csv = ReportCsv.Build(new[] { "Case Number", "Title" }, new[] { new[] { "C-00001", "Card, lost" } });

        Assert.Equal("Case Number,Title\r\nC-00001,\"Card, lost\"\r\n", csv);
    }

    [Fact]
    public void The_exported_bytes_start_with_a_utf8_byte_order_mark_so_excel_reads_names_correctly()
    {
        var bytes = ReportCsv.ToBytes("Nama\r\n");

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    // ------------------------------------------------------------------------------------------------ mentions

    private sealed record World(Guid CaseId, Guid Owner, Guid Lead, Guid CoWorker, Guid Watcher, Guid Outsider, Guid Inactive);

    private async Task<World> SeedAsync()
    {
        User U(string name, bool active = true) => new() { Id = Guid.NewGuid(), Name = name, Email = name.ToLower().Replace(' ', '.') + "@x.test", Role = "Agent", IsActive = active, CreatedAt = DateTime.UtcNow };
        var owner = U("Olivia Owner"); var lead = U("Liam Lead"); var cow = U("Cora Worker"); var watcher = U("Wade Watcher");
        var outsider = U("Olive Outsider"); var inactive = U("Ivy Inactive", active: false);
        db.Users.AddRange(owner, lead, cow, watcher, outsider, inactive);
        var team = new Team { Id = Guid.NewGuid(), Name = "Cards", Code = "CRD", OwnerId = lead.Id, CreatedAt = DateTime.UtcNow };
        db.Teams.Add(team);
        var c = new Case { Id = Guid.NewGuid(), CaseNumber = "C-00001", Title = "Lost card", TeamId = team.Id, OwnerId = owner.Id, Severity = "High", CreatedAt = DateTime.UtcNow };
        db.Cases.Add(c);
        db.CaseParticipants.AddRange(
            new CaseParticipant { CaseId = c.Id, UserId = cow.Id, Role = ParticipantRole.CoWorker },
            new CaseParticipant { CaseId = c.Id, UserId = watcher.Id, Role = ParticipantRole.Watcher },
            new CaseParticipant { CaseId = c.Id, UserId = inactive.Id, Role = ParticipantRole.CoWorker });
        await db.SaveChangesAsync();
        return new World(c.Id, owner.Id, lead.Id, cow.Id, watcher.Id, outsider.Id, inactive.Id);
    }

    private static MentionService Service(AppDbContext db, INotificationService notifications) =>
        new(db, notifications, NullLogger<MentionService>.Instance);

    [Fact]
    public async Task Only_the_cases_owner_team_lead_coworkers_and_watchers_can_be_mentioned_and_never_the_author()
    {
        var w = await SeedAsync();

        var people = await Service(db, Mock.Of<INotificationService>()).GetMentionableAsync(w.CaseId, excludeUserId: w.Watcher);

        Assert.Equal(new[] { w.Owner, w.Lead, w.CoWorker }, people.Select(p => p.UserId).ToArray());
        Assert.Equal(new[] { "Owner", "Team Lead", "Co-worker" }, people.Select(p => p.Relation).ToArray());
    }

    [Fact]
    public async Task A_mention_chosen_from_the_suggestions_notifies_that_user_by_id_and_the_text_reads_as_plain_at_name()
    {
        var w = await SeedAsync();
        var notifications = new Mock<INotificationService>();

        await Service(db, notifications.Object).NotifyAsync($"Please check, @[Cora Worker]({w.CoWorker}) thanks", w.Owner, "Olivia Owner", w.CaseId, "C-00001", "Mentioned in Case");

        notifications.Verify(n => n.CreateNotificationAsync(w.CoWorker, "USER_MENTIONED", "Mentioned in Case",
            It.Is<string>(m => m.Contains("\"Please check, @Cora Worker thanks\"")), w.CaseId, "C-00001", "High", 0, It.IsAny<CancellationToken>(), null), Times.Once);
        notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_typed_at_token_or_an_id_of_someone_outside_the_case_reaches_nobody_outside_the_case()
    {
        var w = await SeedAsync();
        var notifications = new Mock<INotificationService>();

        // "@Oliv" matches the owner (on the case) and the outsider (not on it); the outsider's id is pasted as markup too.
        await Service(db, notifications.Object).NotifyAsync($"@Oliv and @[Olive Outsider]({w.Outsider})", w.Lead, "Liam Lead", w.CaseId, "C-00001", "Mentioned in Case");

        notifications.Verify(n => n.CreateNotificationAsync(w.Owner, "USER_MENTIONED", It.IsAny<string>(), It.IsAny<string>(), w.CaseId, "C-00001", "High", 0, It.IsAny<CancellationToken>(), null), Times.Once);
        notifications.VerifyNoOtherCalls();
    }

    [Fact]
    public void Plain_text_drops_the_id_but_keeps_everything_around_the_mention()
    {
        var id = Guid.NewGuid();

        Assert.Equal("Hi @Cora Worker, see above.", MentionService.ToPlainText($"Hi @[Cora Worker]({id}), see above."));
    }

    // ------------------------------------------------------------------------------------------------ sidebar

    [Fact]
    public void The_host_sidebar_lists_Cases_first_then_Reports_on_the_unchanged_dashboard_route()
    {
        var cases = CaseManagement.Api.HostIntegration.HostPermissionCatalog.Modules.Single(m => m.Key == "cases");

        var rows = cases.Nav.OrderBy(n => n.SortOrder).Select(n => (n.Key, n.Label)).ToArray();

        Assert.Equal(new[] { ("case-management", "Cases"), ("dashboard", "Reports") }, rows);
    }

    // ------------------------------------------------------------------------------------------------ unstaffed team

    [Theory]
    [InlineData("AssignToSupervisor", true)]
    [InlineData(" assigntosupervisor ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("AssignToCreator", false)]
    public void Only_an_explicit_supervisor_choice_lets_a_case_into_a_team_with_nobody_available(string? value, bool expected)
    {
        Assert.Equal(expected, UnstaffedTeamActions.IsAssignToSupervisor(value));
    }
}
