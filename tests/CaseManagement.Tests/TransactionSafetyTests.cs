using System;
using System.IO;
using System.Threading.Tasks;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace CaseManagement.Tests;

public class TransactionSafetyTests
{
    private AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task TransferDepartmentAsync_WhenTargetDepartmentNotFound_ThrowsAndDoesNotChangeCase()
    {
        using var db = CreateInMemoryDbContext();

        var originalDept = new Department { Id = Guid.NewGuid(), Name = "Card Services", Code = "CRD" };
        var user = new User { Id = Guid.NewGuid(), Name = "Officer Tan", Email = "tan@bank.com", Role = "Agent", DepartmentId = originalDept.Id };
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Lee Min", NRIC = "880808-10-8888", PhoneNumber = "+60123456780" };

        db.Departments.Add(originalDept);
        db.Users.Add(user);
        db.Customers.Add(customer);

        var testCase = new Case
        {
            Id = Guid.NewGuid(),
            CaseNumber = "C-5001",
            Title = "Disputed Charge",
            CustomerId = customer.Id,
            Customer = customer,
            DepartmentId = originalDept.Id,
            Department = originalDept,
            OwnerId = user.Id,
            Owner = user,
            Status = CaseStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
        db.Cases.Add(testCase);
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object
        );

        var nonExistentDeptId = Guid.NewGuid();
        var transferDto = new TransferDepartmentDto
        {
            DepartmentId = nonExistentDeptId,
            Reason = "Transfer test",
            HandoverNote = "Handover"
        };

        // Transfer should fail because the target department doesn't exist
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.TransferDepartmentAsync(testCase.Id, transferDto, user.Id));

        // Verify case department did not change
        var caseInDb = await db.Cases.FindAsync(testCase.Id);
        Assert.NotNull(caseInDb);
        Assert.Equal(originalDept.Id, caseInDb.DepartmentId);
    }

    [Fact]
    public async Task AssignCaseAsync_WhenTargetUserNotFound_ThrowsAndDoesNotReassign()
    {
        using var db = CreateInMemoryDbContext();

        var dept = new Department { Id = Guid.NewGuid(), Name = "Support", Code = "SUP" };
        var initialOwner = new User { Id = Guid.NewGuid(), Name = "Owner 1", Email = "owner1@bank.com", Role = "Agent", DepartmentId = dept.Id };
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Wong", NRIC = "920202-14-2222", PhoneNumber = "+60123456781" };

        db.Departments.Add(dept);
        db.Users.Add(initialOwner);
        db.Customers.Add(customer);

        var testCase = new Case
        {
            Id = Guid.NewGuid(),
            CaseNumber = "C-5002",
            Title = "Account Query",
            CustomerId = customer.Id,
            Customer = customer,
            DepartmentId = dept.Id,
            Department = dept,
            OwnerId = initialOwner.Id,
            Owner = initialOwner,
            Status = CaseStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
        db.Cases.Add(testCase);
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object
        );

        var nonExistentUserId = Guid.NewGuid();
        var assignDto = new AssignCaseDto
        {
            OwnerId = nonExistentUserId,
            UserId = initialOwner.Id,
            Reason = "Reassigning"
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.AssignCaseAsync(testCase.Id, assignDto));

        var caseInDb = await db.Cases.FindAsync(testCase.Id);
        Assert.NotNull(caseInDb);
        Assert.Equal(initialOwner.Id, caseInDb.OwnerId);
    }

    [Fact]
    public async Task TransferDepartmentAsync_WithoutADifferentTeam_IsRefused_AndRecordsNothing()
    {
        using var db = CreateInMemoryDbContext();

        var dept = new Department { Id = Guid.NewGuid(), Name = "Support", Code = "SUP" };
        var user = new User { Id = Guid.NewGuid(), Name = "Agent 1", Email = "agent1@bank.com", Role = "Agent", DepartmentId = dept.Id };
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Customer A", NRIC = "990101-14-1111", PhoneNumber = "+60123456782" };

        db.Departments.Add(dept);
        db.Users.Add(user);
        db.Customers.Add(customer);

        var testCase = new Case
        {
            Id = Guid.NewGuid(),
            CaseNumber = "C-5003",
            Title = "Queue Transfer Case",
            CustomerId = customer.Id,
            Customer = customer,
            DepartmentId = dept.Id,
            Department = dept,
            OwnerId = user.Id,
            Owner = user,
            Status = CaseStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
        db.Cases.Add(testCase);
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object
        );

        // A team IS the queue: "transferring" without naming another team (the old fake queue option) is refused.
        var transferDto = new TransferDepartmentDto
        {
            DepartmentId = Guid.Empty,
            TransferTo = "Queue",
            TargetQueue = "Tier 2 - Escalations",
            Reason = "Complex query",
            HandoverNote = "Customer needs urgent assistance"
        };

        await Assert.ThrowsAsync<FieldValidationException>(() => service.TransferDepartmentAsync(testCase.Id, transferDto, user.Id));
        await Assert.ThrowsAsync<FieldValidationException>(() => service.TransferDepartmentAsync(testCase.Id, new TransferDepartmentDto { DepartmentId = dept.Id, Reason = "Complex query" }, user.Id));

        Assert.False(await db.CaseEvents.AnyAsync(e => e.CaseId == testCase.Id && e.EventType == EventType.Transfer));
    }

    [Fact]
    public async Task EscalateCaseAsync_SucceedsAndUpdatesLevelAndStatus()
    {
        using var db = CreateInMemoryDbContext();

        var dept = new Department { Id = Guid.NewGuid(), Name = "Support", Code = "SUP" };
        var agent = new User { Id = Guid.NewGuid(), Name = "Frontline Agent", Email = "frontline@bank.com", Role = "Agent", DepartmentId = dept.Id };
        var supervisor = new User { Id = Guid.NewGuid(), Name = "Supervisor Amina", Email = "supervisor@bank.com", Role = "CX Supervisor", DepartmentId = dept.Id };
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Customer B", NRIC = "990101-14-2222", PhoneNumber = "+60123456783" };

        db.Departments.Add(dept);
        db.Users.AddRange(agent, supervisor);
        db.Customers.Add(customer);

        var testCase = new Case
        {
            Id = Guid.NewGuid(),
            CaseNumber = "C-5004",
            Title = "Escalation Case",
            CustomerId = customer.Id,
            Customer = customer,
            DepartmentId = dept.Id,
            Department = dept,
            OwnerId = agent.Id,
            Owner = agent,
            Status = CaseStatus.Open,
            EscalationLevel = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.Cases.Add(testCase);
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        // The reason must be an active option of the configured escalation reason list.
        var reasonList = new LookupType { Id = Guid.NewGuid(), Code = "ESCALATION_REASON", Name = "Escalation Reason" };
        db.LookupTypes.Add(reasonList);
        db.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), LookupTypeId = reasonList.Id, TypeCode = "ESCALATION_REASON", Value = "Management Escalation", Label = "Management Escalation", IsActive = true });

        db.EscalationLevelConfigs.AddRange(
            new EscalationLevelConfig { LevelNumber = 1, Name = "Level 1", AssignmentType = "Owner", TriggerType = "SlaPercentage", TriggerValue = 70, ReassignOwner = false, IsActive = true },
            new EscalationLevelConfig { LevelNumber = 2, Name = "Level 2", AssignmentType = "Role", TargetRole = supervisor.Role, TriggerType = "SlaPercentage", TriggerValue = 90, ReassignOwner = true, IsActive = true });
        await db.SaveChangesAsync();

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object,
            escalation: new EscalationService(db, new ConfigCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))
        );

        var escalateDto = new EscalateCaseDto
        {
            TargetUserId = supervisor.Id,
            Reason = "Management Escalation",
            Note = "Handled smoothly"
        };

        await service.EscalateCaseAsync(testCase.Id, escalateDto, agent.Id);

        var updatedCase = await db.Cases.FindAsync(testCase.Id);
        Assert.NotNull(updatedCase);
        Assert.Equal(CaseStatus.Escalated, updatedCase.Status);
        Assert.Equal(supervisor.Id, updatedCase.OwnerId);
        Assert.Equal(2, updatedCase.EscalationLevel);

        var events = await db.CaseEvents.Where(e => e.CaseId == testCase.Id).ToListAsync();
        Assert.Contains(events, e => e.EventType == EventType.Escalate && e.Message.Contains("Reason: Management Escalation"));
    }

    [Fact]
    public async Task ResolveCaseAsync_SucceedsAndSetsResolutionFields()
    {
        using var db = CreateInMemoryDbContext();

        var dept = new Department { Id = Guid.NewGuid(), Name = "Support", Code = "SUP" };
        var user = new User { Id = Guid.NewGuid(), Name = "Agent 2", Email = "agent2@bank.com", Role = "Agent", DepartmentId = dept.Id };
        var customer = new Customer { Id = Guid.NewGuid(), FullName = "Customer C", NRIC = "990101-14-3333", PhoneNumber = "+60123456784" };

        db.Departments.Add(dept);
        db.Users.Add(user);
        db.Customers.Add(customer);

        var testCase = new Case
        {
            Id = Guid.NewGuid(),
            CaseNumber = "C-5005",
            Title = "Resolvable Case",
            CustomerId = customer.Id,
            Customer = customer,
            DepartmentId = dept.Id,
            Department = dept,
            OwnerId = user.Id,
            Owner = user,
            Status = CaseStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
        db.Cases.Add(testCase);
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object
        );

        var resolveDto = new ResolveCaseDto
        {
            Disposition = "Resolved - Explained Fee Structure",
            ResolutionNote = "Customer agreed with explanation",
            UserId = user.Id
        };

        await service.ResolveCaseAsync(testCase.Id, resolveDto);

        var updatedCase = await db.Cases.FindAsync(testCase.Id);
        Assert.NotNull(updatedCase);
        Assert.Equal(CaseStatus.Resolved, updatedCase.Status);
        Assert.NotNull(updatedCase.ResolvedAt);
        Assert.Equal("Resolved - Explained Fee Structure", updatedCase.Disposition);

        var events = await db.CaseEvents.Where(e => e.CaseId == testCase.Id).ToListAsync();
        Assert.Contains(events, e => e.EventType == EventType.Resolve);
    }
}
