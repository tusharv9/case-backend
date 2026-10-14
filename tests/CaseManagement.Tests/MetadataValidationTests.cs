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

public class MetadataValidationTests
{
    private static IFieldValidationEngine NewEngine(AppDbContext db) =>
        new FieldValidationEngine(db, new ConfigCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FieldValidationEngine>.Instance);

    private AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private async Task<(Customer customer, Department department, string subcategory)> SeedValidCustomerAndDepartmentAsync(AppDbContext db)
    {
        var cust = new Customer
        {
            Id = Guid.NewGuid(),
            FullName = "Test Customer",
            NRIC = "123456-78-9012"
        };
        var dept = new Department
        {
            Id = Guid.NewGuid(),
            Name = "Contact Center",
            Code = "CC",
            IsActive = true
        };
        var subcat = new DepartmentSubCategory
        {
            Id = Guid.NewGuid(),
            DepartmentId = dept.Id,
            Name = "General Inquiry",
            IsActive = true
        };
        db.Customers.Add(cust);
        db.Departments.Add(dept);
        db.CaseTypeConfigs.Add(new CaseTypeConfig { Id = Guid.NewGuid(), Code = "Inquiry", Name = "Inquiry", Prefix = "I-", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.DepartmentSubCategories.Add(subcat);
        await db.SaveChangesAsync();
        return (cust, dept, subcat.Name);
    }

    [Fact]
    public async Task ValidateCreateCase_MissingRequiredField_ThrowsArgumentException()
    {
        using var db = CreateInMemoryDbContext();
        var (cust, dept, subcat) = await SeedValidCustomerAndDepartmentAsync(db);

        db.FieldConfigurations.Add(new FieldConfiguration
        {
            Id = Guid.NewGuid(),
            ModuleKey = "CaseManagement",
            SectionKey = "CreateCase",
            ApiField = "description",
            DisplayLabel = "Description",
            IsRequired = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object,
            fieldValidation: NewEngine(db)
        );

        var dto = new CreateCaseDto
        {
            CustomerId = cust.Id,
            DepartmentId = dept.Id,
            CaseType = "Inquiry",
            Subcategory = subcat,
            SourceChannel = "Voice",
            PreferredCommunicationChannel = "Phone",
            Severity = "Medium",
            Title = "Valid Title",
            Description = "" // Empty description when required
        };

        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ValidateCreateCaseMetadataAsync(dto));
        Assert.Contains("Description is required", ex.Message);
    }

    [Fact]
    public async Task ValidateCreateCase_MinLengthViolation_ThrowsArgumentException()
    {
        using var db = CreateInMemoryDbContext();
        var (cust, dept, subcat) = await SeedValidCustomerAndDepartmentAsync(db);

        db.FieldConfigurations.Add(new FieldConfiguration
        {
            Id = Guid.NewGuid(),
            ModuleKey = "CaseManagement",
            SectionKey = "CreateCase",
            ApiField = "title",
            DisplayLabel = "Title",
            IsRequired = true,
            MinLength = 10,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object,
            fieldValidation: NewEngine(db)
        );

        var dto = new CreateCaseDto
        {
            CustomerId = cust.Id,
            DepartmentId = dept.Id,
            CaseType = "Inquiry",
            Subcategory = subcat,
            SourceChannel = "Voice",
            PreferredCommunicationChannel = "Phone",
            Severity = "Medium",
            Title = "Short", // Length 5 < 10
            Description = "A valid description for this test case."
        };

        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ValidateCreateCaseMetadataAsync(dto));
        Assert.Contains("must be at least 10 characters", ex.Message);
    }

    [Fact]
    public async Task ValidateCreateCase_RegexViolation_ThrowsArgumentException()
    {
        using var db = CreateInMemoryDbContext();
        var (cust, dept, subcat) = await SeedValidCustomerAndDepartmentAsync(db);

        db.FieldConfigurations.Add(new FieldConfiguration
        {
            Id = Guid.NewGuid(),
            ModuleKey = "CaseManagement",
            SectionKey = "CreateCase",
            ApiField = "title",
            DisplayLabel = "Title",
            ValidationRegex = "^[A-Z].*", // Must start with capital letter
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object,
            fieldValidation: NewEngine(db)
        );

        var dto = new CreateCaseDto
        {
            CustomerId = cust.Id,
            DepartmentId = dept.Id,
            CaseType = "Inquiry",
            Subcategory = subcat,
            SourceChannel = "Voice",
            PreferredCommunicationChannel = "Phone",
            Severity = "Medium",
            Title = "lowercase start title",
            Description = "Valid description here."
        };

        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ValidateCreateCaseMetadataAsync(dto));
        Assert.Contains("format is invalid", ex.Message);
    }

    [Fact]
    public async Task ValidateCreateCase_MissingCustomerOrDepartment_ThrowsArgumentException()
    {
        using var db = CreateInMemoryDbContext();
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(Path.GetTempPath());

        var service = new CaseService(
            new CaseRepository(db),
            new Mock<INotificationService>().Object,
            new Mock<IConfigurableSettingsService>().Object,
            db,
            mockEnv.Object,
            fieldValidation: NewEngine(db)
        );

        var dto = new CreateCaseDto
        {
            CustomerId = Guid.Empty, // Missing customer
            DepartmentId = Guid.NewGuid(),
            Title = "Valid Title",
            Description = "Valid Description"
        };

        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ValidateCreateCaseMetadataAsync(dto));
        Assert.Contains("customer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
