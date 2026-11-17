using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Notifications;
using CaseManagement.Api.Services.Tickets;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

/// <summary>
/// Tickets: who sees which mailbox's tickets, and assigning a ticket — which must make exactly one case, hand the case the
/// request's arrival time (kept as the request time; the case's own SLA starts when the case is made), record how long the
/// ticket waited against its own assign-within target, and refuse a ticket someone else already handled.
/// </summary>
/// <remarks>
/// A ticket is the only record of a customer's request until it is assigned: a second case from one ticket, a ticket that
/// silently stays "New" after its case was made, or another team reading a mailbox it was not given are each a real
/// failure for the people relying on the queue.
/// </remarks>
public class TicketServiceTests : IDisposable
{
    private readonly AppDbContext db;
    private readonly MemoryStore store = new();
    private readonly RecordingNotificationSink sink = new();
    private readonly List<(Guid CaseId, string Name)> uploads = new();
    private readonly List<(Guid? OwnerId, TicketOrigin Origin)> created = new();
    private readonly User assigner = new() { Name = "Asha Triage", Email = "asha@rma.test", CreatedAt = DateTime.UtcNow };
    private readonly User agent = new() { Name = "Bala Agent", Email = "bala@rma.test", CreatedAt = DateTime.UtcNow };
    private readonly User outsider = new() { Name = "Chen Other", Email = "chen@rma.test", CreatedAt = DateTime.UtcNow };
    private readonly Team support;
    private readonly Team other;
    private readonly EmailMailbox restricted;
    private readonly EmailMailbox unshared;

    public TicketServiceTests()
    {
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"tickets-{Guid.NewGuid()}").Options);
        db.Users.AddRange(assigner, agent, outsider);
        support = new Team { Name = "Support", OwnerId = assigner.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
        other = new Team { Name = "Loans", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Teams.AddRange(support, other);
        db.TeamMembers.Add(new TeamMember { TeamId = support.Id, UserId = agent.Id, IsActive = true, CreatedAt = DateTime.UtcNow });
        db.TeamMembers.Add(new TeamMember { TeamId = other.Id, UserId = outsider.Id, IsActive = true, CreatedAt = DateTime.UtcNow });
        restricted = new EmailMailbox { Name = "Support", Address = "support@rma.test", ImapHost = "i", SmtpHost = "s", CreatedAt = DateTime.UtcNow };
        restricted.Teams.Add(new EmailMailboxTeam { MailboxId = restricted.Id, TeamId = support.Id });
        unshared = new EmailMailbox { Name = "General", Address = "hello@rma.test", ImapHost = "i", SmtpHost = "s", CreatedAt = DateTime.UtcNow };
        db.EmailMailboxes.AddRange(restricted, unshared);
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); GC.SuppressFinalize(this); }

    // ───────────── fakes ─────────────

    private sealed class Caller(Guid id, params string[] permissions) : ICurrentUserAccessor
    {
        public Guid? LocalUserId => id;
        public HostPrincipal? Principal => null;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>(permissions);
        public bool Has(string permission) => Permissions.Contains("*") || Permissions.Contains(permission);
    }

    private sealed class MemoryStore : IAttachmentStore
    {
        private readonly Dictionary<string, byte[]> files = new();
        public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            var key = Guid.NewGuid().ToString("N") + extension;
            files[key] = copy.ToArray();
            return key;
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream>(new MemoryStream(files[key]));
        public Task DeleteAsync(string key, CancellationToken ct = default) { files.Remove(key); return Task.CompletedTask; }
    }

    private TicketService Service(User who, params string[] permissions)
    {
        var cases = new Mock<ICaseService>();
        cases.Setup(c => c.CreateCaseForTicketAsync(It.IsAny<CreateCaseDto>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<TicketOrigin>(), It.IsAny<bool>(), It.IsAny<Func<Case, Task>?>()))
            .Returns(async (CreateCaseDto dto, Guid by, Guid? owner, TicketOrigin origin, bool _, Func<Case, Task>? inTx) =>
            {
                var c = new Case { CaseNumber = $"C-{created.Count + 1:D5}", Title = dto.Title, Description = dto.Description, CaseType = dto.CaseType,
                    Severity = dto.Severity, SourceChannel = dto.SourceChannel ?? "", PreferredCommunicationChannel = dto.PreferredCommunicationChannel ?? "",
                    ContactEmail = dto.ContactEmail, CustomerId = dto.CustomerId, OwnerId = owner ?? by, TeamId = support.Id, CreatedAt = DateTime.UtcNow };
                db.Cases.Add(c);
                if (inTx != null) await inTx(c);
                await db.SaveChangesAsync();
                created.Add((owner, origin));
                return c;
            });
        var attachments = new Mock<IAttachmentService>();
        attachments.Setup(a => a.UploadAttachmentAsync(It.IsAny<Guid>(), It.IsAny<IFormFile>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid caseId, IFormFile file, string? _, Guid _, CancellationToken _) =>
            {
                uploads.Add((caseId, file.FileName));
                return Task.FromResult(new CaseAttachmentDto { FileName = file.FileName });
            });
        var caller = new Caller(who.Id, permissions);
        return new TicketService(db, new MailboxAccessScope(db, caller), cases.Object, attachments.Object, store,
            new Mock<IAssignmentEligibilityService>().Object, new Mock<ILiveEvents>().Object, sink, NullLogger<TicketService>.Instance);
    }

    /// <summary>A ticket as email intake leaves it: by default 3 hours old with the 30-minute assign-within target started.</summary>
    private async Task<Ticket> Waiting(EmailMailbox mailbox, string subject = "Refund not received", TimeSpan? age = null, int target = 30)
    {
        var received = DateTime.UtcNow - (age ?? TimeSpan.FromHours(3));
        var ticket = new Ticket
        {
            MailboxId = mailbox.Id, RequesterName = "Ravi Kumar", RequesterEmail = "ravi@gmail.com", Subject = subject, Description = "Charged twice.",
            ReceivedAt = received, CreatedById = assigner.Id, CreatedAt = DateTime.UtcNow,
            AssignTargetMinutes = target, AssignDueAt = target > 0 ? received.AddMinutes(target) : null,
        };
        var key = await store.SaveAsync(new MemoryStream([1, 2, 3]), ".pdf");
        ticket.Attachments.Add(new TicketAttachment { FileName = "statement.pdf", StorageKey = key, SizeBytes = 3 });
        await CaseManagement.Api.Services.EmailIntake.TicketNumbers.AddAsync(db, ticket, default);
        db.InboundEmails.Add(new InboundEmail { MailboxId = mailbox.Id, MessageKey = $"m-{ticket.Id}", TicketId = ticket.Id, Status = EmailStatuses.TicketCreated, ReceivedAt = ticket.ReceivedAt, CreatedAt = DateTime.UtcNow });
        db.InboundEmails.Add(new InboundEmail { MailboxId = mailbox.Id, MessageKey = $"r-{ticket.Id}", TicketId = ticket.Id, Status = EmailStatuses.LinkedToTicket,
            FromAddress = "ravi@gmail.com", Subject = "Re: refund", BodyText = "Any news?", ReceivedAt = ticket.ReceivedAt.AddHours(1), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return ticket;
    }

    private static AssignTicketDto Assign(int version, Guid? owner = null) => new()
    {
        Case = new CreateCaseDto { CaseType = "Complaint", DepartmentId = Guid.NewGuid(), Subcategory = "Refunds", Severity = "High" },
        OwnerId = owner, CreateCustomerFromRequester = true, Version = version,
    };

    // ───────────── tests ─────────────

    [Fact]
    public async Task Assigning_makes_one_case_for_the_chosen_agent_hands_it_the_arrival_time_and_moves_the_ticket_its_emails_and_files_to_it()
    {
        var ticket = await Waiting(restricted);

        var result = await Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, Assign(ticket.Version, agent.Id), assigner.Id, false, default);

        var c = await db.Cases.SingleAsync();
        Assert.Equal((TicketStatuses.Converted, c.Id, c.CaseNumber), (result.Status, result.CaseId, result.CaseNumber));
        var (owner, origin) = Assert.Single(created);
        Assert.Equal((agent.Id, ticket.Id, ticket.TicketNumber, ticket.ReceivedAt), (owner, origin.TicketId, origin.TicketNumber, origin.ReceivedAt));
        Assert.Equal(("Refund not received", "Charged twice."), (c.Title, c.Description));
        Assert.All(await db.InboundEmails.ToListAsync(), e => Assert.Equal(c.Id, e.CaseId));
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.ActionType == "TICKET_CONVERTED"));
        Assert.True(await db.CaseEvents.AnyAsync(e => e.CaseId == c.Id && e.ActionType == "EMAIL_REPLY_RECEIVED" && e.Message.Contains("Any news?")));
        Assert.Equal((c.Id, "statement.pdf"), Assert.Single(uploads));
        Assert.Equal("ravi@gmail.com", (await db.Customers.SingleAsync(x => x.Id == c.CustomerId)).Email);
    }

    [Fact]
    public async Task When_email_cases_are_assigned_by_hand_converting_an_email_ticket_without_an_agent_is_refused_and_makes_no_case()
    {
        var cards = new Department { Name = "Cards", Code = "CD", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.Departments.Add(cards);
        db.TeamDepartments.Add(new TeamDepartment { TeamId = support.Id, DepartmentId = cards.Id });
        db.AssignmentConfigurations.Add(new AssignmentConfiguration { TeamId = null, Algorithm = AssignmentAlgorithms.RoundRobin, MaxConcurrentCapacity = 5, CreatedAt = DateTime.UtcNow });
        db.AssignmentConfigurations.Add(new AssignmentConfiguration { TeamId = null, CaseSource = AssignmentCaseSources.Email, Algorithm = AssignmentAlgorithms.Manual, MaxConcurrentCapacity = 5, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var ticket = await Waiting(restricted);
        AssignTicketDto Into(Guid? owner) => new()
        {
            Case = new CreateCaseDto { CaseType = "Complaint", DepartmentId = cards.Id, Subcategory = "Refunds", Severity = "High", SourceChannel = "Walk-in" },
            OwnerId = owner, CreateCustomerFromRequester = true, Version = ticket.Version,
        };

        var rule = await Service(assigner).AssignmentRuleAsync(ticket.Id, cards.Id, default);
        var refused = await Assert.ThrowsAsync<FieldValidationException>(() =>
            Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, Into(null), assigner.Id, false, default));
        var done = await Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, Into(agent.Id), assigner.Id, false, default);

        Assert.Equal(("Support", AssignmentAlgorithms.Manual, true), (rule!.TeamName, rule.Algorithm, rule.OwnerRequired));
        Assert.Contains(refused.Errors, e => e.Field == "ownerId" && e.Message.Contains("Email cases are assigned by hand"));
        Assert.Equal(TicketStatuses.Converted, done.Status);
        Assert.Equal("Email", (await db.Cases.SingleAsync()).SourceChannel);   // a case from an email ticket is an email case, whatever was sent
    }

    [Fact]
    public async Task A_case_made_from_an_email_ticket_keeps_to_email_and_replies_go_to_the_address_it_came_from()
    {
        var ticket = await Waiting(restricted);
        var dto = Assign(ticket.Version, agent.Id);
        dto.Case.SourceChannel = "Voice";                    // whatever the form sent
        dto.Case.PreferredCommunicationChannel = "Phone";
        dto.Case.ContactEmail = "someone-else@example.com";

        await Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, dto, assigner.Id, false, default);

        var c = await db.Cases.SingleAsync();
        Assert.Equal(("Email", "Email", ticket.RequesterEmail), (c.SourceChannel, c.PreferredCommunicationChannel, c.ContactEmail));
    }

    [Fact]
    public async Task A_registered_senders_ticket_becomes_a_case_for_their_organisation_without_making_a_person_customer()
    {
        var org = new EmailOrganisation { Name = "ABC Financial Services", NameKey = "abc financial services", CreatedAt = DateTime.UtcNow };
        var domain = new EmailVerifiedDomain { Domain = "abcfinancial.com", OrganisationId = org.Id, OrganisationName = org.Name, VerificationPolicy = VerificationPolicies.SpecificSenders, CreatedAt = DateTime.UtcNow };
        var rahul = new EmailVerifiedSender { VerifiedDomainId = domain.Id, Address = "rahul@abcfinancial.com", DisplayName = "Rahul Sharma", CreatedAt = DateTime.UtcNow };
        db.AddRange(org, domain, rahul);
        db.SaveChanges();
        var ticket = await Waiting(restricted);
        var tracked = db.Tickets.Single(t => t.Id == ticket.Id);
        (tracked.OrganisationId, tracked.VerifiedSenderId) = (org.Id, rahul.Id);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var detail = await Service(assigner).GetAsync(ticket.Id, default);
        var other = Assign(ticket.Version, agent.Id);
        other.Case.OrganisationContactId = Guid.NewGuid();   // someone else's sender: refused
        await Assert.ThrowsAsync<FieldValidationException>(() => Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, other, assigner.Id, false, default));
        var dto = Assign(ticket.Version, agent.Id);
        dto.CreateCustomerFromRequester = false;
        dto.Case.OrganisationContactId = rahul.Id;
        await Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, dto, assigner.Id, false, default);

        Assert.Equal((rahul.Id, "ABC Financial Services", "Rahul Sharma"), (detail.OrganisationContactId, detail.OrganisationName, detail.ContactName));
        Assert.Empty(db.Customers);   // no individual customer was made from the requester
        Assert.Single(db.Cases);
    }

    [Fact]
    public async Task A_ticket_already_assigned_is_refused_naming_who_did_it_and_no_second_case_is_made()
    {
        var ticket = await Waiting(restricted);
        await Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, Assign(ticket.Version), assigner.Id, false, default);
        db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<TicketConflictException>(() =>
            Service(agent, Permissions.TicketsAssign).AssignAsync(ticket.Id, Assign(ticket.Version), agent.Id, false, default));

        Assert.Contains("Asha Triage", ex.Message);
        Assert.Equal(1, await db.Cases.CountAsync());
    }

    [Fact]
    public async Task A_ticket_that_changed_since_it_was_opened_is_refused_until_it_is_reviewed_again()
    {
        var ticket = await Waiting(restricted);

        await Assert.ThrowsAsync<TicketConflictException>(() =>
            Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, Assign(ticket.Version + 1), assigner.Id, false, default));

        Assert.Empty(db.Cases);
        Assert.Equal(TicketStatuses.New, (await db.Tickets.SingleAsync()).Status);
    }

    [Fact]
    public async Task Assigning_without_a_customer_asks_for_one_instead_of_inventing_it()
    {
        var ticket = await Waiting(restricted);
        var dto = Assign(ticket.Version);
        dto.CreateCustomerFromRequester = false;

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => Service(assigner).AssignAsync(ticket.Id, dto, assigner.Id, false, default));

        Assert.Contains(ex.Errors, e => e.Field == "selectCustomer");
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task A_mailbox_is_seen_only_by_its_teams_lead_and_members_and_one_shared_with_no_team_only_by_those_who_see_every_mailbox()
    {
        var supportTicket = await Waiting(restricted, "Support only");
        var unsharedTicket = await Waiting(unshared, "No team yet");

        string[] Subjects(PagedResponseDto<TicketListItemDto> p) => p.Items.Select(i => i.Subject).OrderBy(s => s).ToArray();
        Assert.Equal(["Support only"], Subjects(await Service(assigner).ListAsync("new", null, null, 1, 25, default)));
        Assert.Equal(["Support only"], Subjects(await Service(agent).ListAsync("new", null, null, 1, 25, default)));
        Assert.Empty(Subjects(await Service(outsider).ListAsync("new", null, null, 1, 25, default)));
        Assert.Equal(["No team yet", "Support only"], Subjects(await Service(outsider, Permissions.TicketsViewAll).ListAsync("new", null, null, 1, 25, default)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(outsider).GetAsync(supportTicket.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(agent).GetAsync(unsharedTicket.Id, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Service(outsider, Permissions.TicketsAssign).AssignAsync(supportTicket.Id, Assign(supportTicket.Version), outsider.Id, false, default));
    }

    [Fact]
    public async Task Closing_without_a_case_needs_a_reason_and_ends_the_ticket()
    {
        var ticket = await Waiting(restricted);

        await Assert.ThrowsAsync<FieldValidationException>(() => Service(assigner).CloseAsync(ticket.Id, new CloseTicketDto { Reason = " ", Version = ticket.Version }, assigner.Id, default));
        var closed = await Service(assigner).CloseAsync(ticket.Id, new CloseTicketDto { Reason = "Spam", Version = ticket.Version }, assigner.Id, default);

        Assert.Equal((TicketStatuses.Closed, "Spam", "Asha Triage"), (closed.Status, closed.CloseReason, closed.HandledByName));
        Assert.Empty(db.Cases);
    }

    [Fact]
    public async Task Assigning_records_how_long_the_ticket_waited_and_that_it_missed_its_target()
    {
        var ticket = await Waiting(restricted);   // 3 hours old, 30-minute target

        var result = await Service(assigner, Permissions.TicketsAssign).AssignAsync(ticket.Id, Assign(ticket.Version, agent.Id), assigner.Id, false, default);

        var stored = await db.Tickets.AsNoTracking().SingleAsync();
        Assert.InRange(stored.AssignedWithinMinutes!.Value, 179, 181);
        Assert.Equal("Breached", stored.SlaOutcome);
        Assert.Equal((TicketSlaStates.Breached, false), (result.Sla!.State, result.Sla.IsRunning));
        Assert.InRange(result.Sla.WaitedMinutes!.Value, 179, 181);
    }

    [Fact]
    public async Task Closing_a_ticket_in_time_stops_its_clock_as_met()
    {
        var ticket = await Waiting(restricted, age: TimeSpan.FromMinutes(5));

        var closed = await Service(assigner).CloseAsync(ticket.Id, new CloseTicketDto { Reason = "Duplicate", Version = ticket.Version }, assigner.Id, default);

        Assert.Equal(("Met", TicketSlaStates.Met), ((await db.Tickets.AsNoTracking().SingleAsync()).SlaOutcome, closed.Sla!.State));
    }

    [Fact]
    public async Task The_list_shows_each_tickets_assign_time_and_overdue_holds_only_late_tickets_still_waiting()
    {
        await Waiting(restricted, "Late");
        await Waiting(restricted, "Fresh", age: TimeSpan.FromMinutes(5));
        var handled = await Waiting(restricted, "Late but handled");
        await Service(assigner).CloseAsync(handled.Id, new CloseTicketDto { Reason = "Spam", Version = handled.Version }, assigner.Id, default);
        var service = Service(assigner);

        var waiting = await service.ListAsync("new", null, null, 1, 25, default);
        var overdue = await service.ListAsync("overdue", null, null, 1, 25, default);
        var counts = await service.CountsAsync(null, default);

        Assert.Equal(TicketSlaStates.Breached, waiting.Items.Single(i => i.Subject == "Late").Sla!.State);
        Assert.Equal(TicketSlaStates.Healthy, waiting.Items.Single(i => i.Subject == "Fresh").Sla!.State);
        Assert.Equal(["Late"], overdue.Items.Select(i => i.Subject).ToArray());
        Assert.Equal((2, 1), (counts.New, counts.Overdue));
    }

    [Fact]
    public async Task A_ticket_whose_priority_has_no_assign_target_never_reads_overdue()
    {
        await Waiting(restricted, "No target", target: 0);

        var service = Service(assigner);

        Assert.Empty((await service.ListAsync("overdue", null, null, 1, 25, default)).Items);
        Assert.Equal(TicketSlaStates.None, (await service.ListAsync("new", null, null, 1, 25, default)).Items.Single().Sla!.State);
    }
}
