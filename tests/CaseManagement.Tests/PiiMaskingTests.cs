using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CaseManagement.Api.HostIntegration;

namespace CaseManagement.Tests;

public class PiiMaskingTests
{
    private static IConfigCache NewCache() => new ConfigCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    private AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void Mask_FullMask_ReturnsAllAsterisks()
    {
        using var db = CreateInMemoryDbContext();
        var service = new PiiMaskingService(db, NewCache());

        var result = service.Mask("900101-14-5555", "FullMask");

        Assert.Equal("**************", result);
    }

    [Fact]
    public void Mask_HideMiddle_PreservesPrefixAndSuffix()
    {
        using var db = CreateInMemoryDbContext();
        var service = new PiiMaskingService(db, NewCache());

        // "0123456789", visibleChars = 2 -> prefix "01", middle "***", suffix "89"
        var result = service.Mask("0123456789", "HideMiddle", 2);

        Assert.Equal("01******89", result);
    }

    [Fact]
    public void Mask_HideFirstShowLast_HidesPrefixShowsSuffix()
    {
        using var db = CreateInMemoryDbContext();
        var service = new PiiMaskingService(db, NewCache());

        var result = service.Mask("+60123456789", "HideFirstShowLast", 4);

        Assert.Equal("********6789", result);
    }

    [Fact]
    public void Mask_None_ReturnsOriginalValue()
    {
        using var db = CreateInMemoryDbContext();
        var service = new PiiMaskingService(db, NewCache());

        var result = service.Mask("normal text", "None");

        Assert.Equal("normal text", result);
    }

    [Fact]
    public async Task MaskCustomerSummaryAsync_AppliesConfiguredRules()
    {
        using var db = CreateInMemoryDbContext();

        db.FieldConfigurations.AddRange(
            new FieldConfiguration
            {
                Id = Guid.NewGuid(),
                ModuleKey = "Customer360",
                SectionKey = "AddNewCustomer",
                ApiField = "nric",
                DisplayLabel = "NRIC",
                MaskingRule = "HideMiddle",
                VisibleChars = 3,
                CreatedAt = DateTime.UtcNow
            },
            new FieldConfiguration
            {
                Id = Guid.NewGuid(),
                ModuleKey = "Customer360",
                SectionKey = "AddNewCustomer",
                ApiField = "phoneNumber",
                DisplayLabel = "Phone Number",
                MaskingRule = "HideFirstShowLast",
                VisibleChars = 4,
                CreatedAt = DateTime.UtcNow
            },
            new FieldConfiguration
            {
                Id = Guid.NewGuid(),
                ModuleKey = "Customer360",
                SectionKey = "AddNewCustomer",
                ApiField = "dateOfBirth",
                DisplayLabel = "Date of Birth",
                MaskingRule = "FullMask",
                VisibleChars = 0,
                CreatedAt = DateTime.UtcNow
            }
        );
        await db.SaveChangesAsync();

        var service = new PiiMaskingService(db, NewCache());

        var summary = new CustomerSummaryDto
        {
            Id = Guid.NewGuid(),
            FullName = "Ahmad Razak",
            NRIC = "900101-14-5555",
            PhoneNumber = "+60123456789",
            DateOfBirth = new DateTime(1990, 1, 1)
        };

        await service.MaskCustomerSummaryAsync(summary);

        Assert.Equal("Ahmad Razak", summary.FullName);
        Assert.StartsWith("900", summary.NRIC);
        Assert.EndsWith("555", summary.NRIC);
        Assert.Contains("*", summary.NRIC);
        Assert.EndsWith("6789", summary.PhoneNumber);
        Assert.Null(summary.DateOfBirth);
    }
}

public class PiiMaskingEngineTests
{
    private static IConfigCache NewCache() =>
        new ConfigCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FieldConfiguration Field(string apiField, string rule, int visible, string section = "AddNewCustomer") => new()
    {
        Id = Guid.NewGuid(), ModuleKey = "Customer360", SectionKey = section, ApiField = apiField, DisplayLabel = apiField,
        MaskingRule = rule, VisibleChars = visible, CreatedAt = DateTime.UtcNow
    };

    private static ICurrentUserAccessor User(params string[] permissions)
    {
        var mock = new Moq.Mock<ICurrentUserAccessor>();
        mock.SetupGet(u => u.Permissions).Returns(new HashSet<string>(permissions));
        return mock.Object;
    }

    private static CustomerDetailDto Customer() => new()
    {
        Id = Guid.NewGuid(), FullName = "Ahmad Razak", IdType = "Passport Number",
        IdValue = "A98765432", NRIC = "900101-14-5555", Passport = "A98765432", AccountNumber = "ACC-1234567",
        PhoneNumber = "+60123456789", Email = "ahmad@example.test", DateOfBirth = new DateTime(1990, 1, 1)
    };

    [Fact]
    public async Task OneIdValueRule_CoversEveryIdentifierProperty_NotJustNric()
    {
        // The audit's leak: masking was keyed to a field name ("nric") that does not exist in the seeded
        // configuration, and IdValue/Passport/AccountNumber were never masked at all.
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("idValue", "HideFirstShowLast", 4));
        await db.SaveChangesAsync();

        var dto = Customer();
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(dto);

        Assert.Equal("*****5432", dto.IdValue);
        Assert.EndsWith("5555", dto.NRIC);
        Assert.DoesNotContain("900101", dto.NRIC);
        Assert.EndsWith("5432", dto.Passport);
        Assert.DoesNotContain("A9876", dto.Passport);
        Assert.EndsWith("4567", dto.AccountNumber);
        Assert.DoesNotContain("ACC-", dto.AccountNumber);
    }

    [Fact]
    public async Task ASpecificFieldRule_OverridesTheGeneralOne_ForThatPropertyOnly()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(Field("idValue", "HideFirstShowLast", 4), Field("passport", "FullMask", 0));
        await db.SaveChangesAsync();

        var dto = Customer();
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(dto);

        Assert.Equal(new string('*', 9), dto.Passport);   // specific rule wins
        Assert.EndsWith("5432", dto.IdValue);             // general rule still applies elsewhere
    }

    [Fact]
    public async Task Email_FullName_AndDate_AreMaskedWhenConfiguredSensitive()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(Field("email", "HideMiddle", 2), Field("fullName", "HideFirstShowLast", 3), Field("dateOfBirth", "FullMask", 0));
        await db.SaveChangesAsync();

        var dto = Customer();
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(dto);

        Assert.StartsWith("ah", dto.Email);
        Assert.Contains("*", dto.Email);
        Assert.EndsWith("zak", dto.FullName);
        Assert.DoesNotContain("Ahmad", dto.FullName);
        Assert.Null(dto.DateOfBirth);
    }

    [Fact]
    public async Task AMaskingRule_AppliesOnItsOwn_AndNoMaskingMeansNoMasking()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(Field("phoneNumber", "FullMask", 0), Field("email", "None", 4));
        await db.SaveChangesAsync();

        var dto = Customer();
        var original = dto.Email;
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(dto);

        Assert.Equal(new string('*', "+60123456789".Length), dto.PhoneNumber);   // no "sensitive" flag needed
        Assert.Equal(original, dto.Email);                                      // "No masking" masks nothing
    }

    [Fact]
    public async Task WhenRowsDisagree_TheMostRestrictiveRuleWins()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            Field("phoneNumber", "HideFirstShowLast", 8, section: "AddNewCustomer"),
            Field("phoneNumber", "HideFirstShowLast", 2, section: "ExistingCustomer"));
        await db.SaveChangesAsync();

        var dto = Customer();
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(dto);

        Assert.EndsWith("89", dto.PhoneNumber);
        Assert.Equal(12, dto.PhoneNumber.Length);
        Assert.DoesNotContain("6789", dto.PhoneNumber);   // only 2 characters visible, not 8
    }

    [Fact]
    public async Task CustomAttributes_AreMaskedByTheirOwnKey()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("taxId", "FullMask", 0));
        await db.SaveChangesAsync();

        var dto = Customer();
        dto.CustomAttributes = new List<CustomerCustomAttributeDto>
        {
            new() { FieldKey = "taxId", FieldValue = "TX-998877" },
            new() { FieldKey = "favouriteColour", FieldValue = "blue" },
        };
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(dto);

        Assert.Equal(new string('*', "TX-998877".Length), dto.CustomAttributes[0].FieldValue);
        Assert.Equal("blue", dto.CustomAttributes[1].FieldValue);
    }

    [Fact]
    public async Task Unmasking_NeedsTheExplicitPermission_TheWildcardIsNotEnough()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("phoneNumber", "FullMask", 0));
        await db.SaveChangesAsync();

        var wildcard = Customer();
        await new PiiMaskingService(db, NewCache(), User("*")).MaskAsync(wildcard);
        Assert.DoesNotContain("123456789", wildcard.PhoneNumber);

        var granted = Customer();
        await new PiiMaskingService(db, NewCache(), User("pii.unmask")).MaskAsync(granted);
        Assert.Equal("+60123456789", granted.PhoneNumber);

        var noCaller = Customer();   // e.g. background work: no accessor at all => masked
        await new PiiMaskingService(db, NewCache(), null).MaskAsync(noCaller);
        Assert.DoesNotContain("123456789", noCaller.PhoneNumber);
    }

    [Fact]
    public async Task ChangingTheConfiguration_TakesEffectImmediately_ThroughTheCache()
    {
        using var db = NewDb();
        var cache = NewCache();
        var service = new PiiMaskingService(db, cache, User());

        var before = Customer();
        await service.MaskAsync(before);                       // nothing configured yet; result is cached
        Assert.Equal("+60123456789", before.PhoneNumber);

        db.FieldConfigurations.Add(Field("phoneNumber", "FullMask", 0));
        await db.SaveChangesAsync();
        cache.InvalidateAll();                                 // what ConfigChangeInterceptor does on save

        var after = Customer();
        await service.MaskAsync(after);
        Assert.DoesNotContain("123456789", after.PhoneNumber);
    }

    [Fact]
    public void HideMiddle_NeverRevealsAShortValue()
    {
        // The browser-side version returned short values UNMASKED; the single server implementation masks them fully.
        using var db = NewDb();
        var service = new PiiMaskingService(db, NewCache());
        Assert.Equal("****", service.Mask("1234", "HideMiddle", 2));
        Assert.Equal("******", service.Mask("123456", "HideMiddle", 3));
    }

    // ------------------------------------------------------------------------------------------ Phase 6 (CM-10)

    private static FieldConfiguration CaseField(string apiField, string rule, int visible, string section = "CreateCase") => new()
    {
        Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = section, ApiField = apiField, DisplayLabel = apiField,
        MaskingRule = rule, VisibleChars = visible, CreatedAt = DateTime.UtcNow
    };

    private static CaseDetailDto CaseWithCustomer() => new()
    {
        Id = Guid.NewGuid(), CaseNumber = "C-1", CustomerName = "Ahmad Razak", Customer = Customer(),
        CustomAttributes = new() { new CustomerCustomAttributeDto { FieldKey = "occupation", FieldValue = "Government Officer" } },
        Subcases = new() { new CaseSummaryDto { Id = Guid.NewGuid(), CaseNumber = "C-1-R01", CustomerName = "Ahmad Razak" } },
    };

    [Fact]
    public async Task ACaseFormRule_MasksTheCasesCustomAttribute_WhichUsedToBeSentRaw()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(CaseField("occupation", "HideFirstShowLast", 5));
        await db.SaveChangesAsync();

        var dto = CaseWithCustomer();
        await new PiiMaskingService(db, NewCache(), User()).MaskCaseAsync(dto);

        Assert.Equal("*************ficer", dto.CustomAttributes[0].FieldValue);
    }

    [Fact]
    public async Task AMaskedCustomerName_IsMaskedOnTheCase_ItsSubcases_AndItsCustomer()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("fullName", "HideMiddle", 2));
        await db.SaveChangesAsync();

        var dto = CaseWithCustomer();
        await new PiiMaskingService(db, NewCache(), User()).MaskCaseAsync(dto);

        Assert.Equal("Ah*******ak", dto.CustomerName);
        Assert.Equal("Ah*******ak", dto.Subcases[0].CustomerName);
        Assert.Equal("Ah*******ak", dto.Customer.FullName);

        var list = new List<CaseSummaryDto> { new() { CustomerName = "Ahmad Razak" } };
        await new PiiMaskingService(db, NewCache(), User()).MaskCasesAsync(list);
        Assert.Equal("Ah*******ak", list[0].CustomerName);
    }

    [Fact]
    public async Task Rules_StayInTheirModule_AndFiltersNeverMask()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            CaseField("occupation", "FullMask", 0),                        // a CASE rule: must not mask a customer's attribute
            Field("phoneNumber", "FullMask", 0, section: "Filters"),       // a FILTER: masks nothing
            CaseField("customerSegment", "FullMask", 0, section: "Filters"));
        await db.SaveChangesAsync();

        var customer = Customer();
        customer.CustomAttributes = new() { new CustomerCustomAttributeDto { FieldKey = "occupation", FieldValue = "Teacher" } };
        await new PiiMaskingService(db, NewCache(), User()).MaskAsync(customer);

        Assert.Equal("Teacher", customer.CustomAttributes[0].FieldValue);
        Assert.Equal("+60123456789", customer.PhoneNumber);
    }

    [Fact]
    public async Task PeopleWithPiiUnmask_SeeCaseDataRaw()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(CaseField("occupation", "FullMask", 0), Field("fullName", "FullMask", 0));
        await db.SaveChangesAsync();

        var dto = CaseWithCustomer();
        await new PiiMaskingService(db, NewCache(), User(Permissions.PiiUnmask)).MaskCaseAsync(dto);

        Assert.Equal("Government Officer", dto.CustomAttributes[0].FieldValue);
        Assert.Equal("Ahmad Razak", dto.CustomerName);
    }
}
