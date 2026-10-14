using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using CaseManagement.Api.Validators;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CaseManagement.Tests;

public class FieldValidationEngineTests
{
    private static ConfigCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FieldValidationEngine Engine(AppDbContext db, IConfigCache? cache = null) =>
        new(db, cache ?? NewCache(), NullLogger<FieldValidationEngine>.Instance);

    private static FieldConfiguration Field(string apiField, Action<FieldConfiguration>? configure = null)
    {
        var f = new FieldConfiguration
        {
            Id = Guid.NewGuid(), ModuleKey = "M", SectionKey = "S", ApiField = apiField, DisplayLabel = apiField.ToUpperInvariant(),
            IsVisible = true, FieldType = "Text", CreatedAt = DateTime.UtcNow
        };
        configure?.Invoke(f);
        return f;
    }

    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static async Task<FieldValidationResult> Run(AppDbContext db, Dictionary<string, string?> values,
        IEnumerable<string>? custom = null, IReadOnlySet<string>? handledElsewhere = null, IConfigCache? cache = null) =>
        await Engine(db, cache).ValidateAsync("M", "S", values, custom, handledElsewhere);

    [Fact]
    public async Task Required_IsHonouredExactlyAsConfigured_NotAlwaysOn()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            Field("mustHave", f => f.IsRequired = true),
            Field("optional", f => f.IsRequired = false));
        await db.SaveChangesAsync();

        var result = await Run(db, Values(("mustHave", "  "), ("optional", "")));

        Assert.Single(result.Errors);
        Assert.Equal("mustHave", result.Errors[0].Field);
        Assert.Equal("MUSTHAVE is required.", result.Errors[0].Message);
    }

    [Fact]
    public async Task SystemRequiredFields_StayRequired_EvenIfTheStoredFlagWasSwitchedOff_OrTheyAreHidden()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("customer", f => { f.IsSystemRequired = true; f.IsRequired = false; f.IsVisible = false; }));
        await db.SaveChangesAsync();

        var result = await Run(db, Values());
        Assert.Contains(result.Errors, e => e.Field == "customer");
    }

    [Fact]
    public async Task HiddenFields_AreNotValidated()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("secret", f => { f.IsVisible = false; f.IsRequired = true; f.MinLength = 50; }));
        await db.SaveChangesAsync();

        Assert.True((await Run(db, Values(("secret", "x")))).IsValid);
    }

    [Fact]
    public async Task LengthAndPattern_AreEnforced_WithTheAdministratorsMessage()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            Field("short", f => f.MinLength = 5),
            Field("long", f => f.MaxLength = 3),
            Field("code", f => { f.ValidationRegex = "^[A-Z]{3}-\\d{2}$"; f.ValidationMessage = "Use the format ABC-12."; }),
            Field("plainRegex", f => f.ValidationRegex = "^\\d+$"));
        await db.SaveChangesAsync();

        var result = await Run(db, Values(("short", "abc"), ("long", "abcdef"), ("code", "abc-12"), ("plainRegex", "x1")));

        var byField = result.Errors.ToDictionary(e => e.Field, e => e.Message);
        Assert.Equal("SHORT must be at least 5 characters.", byField["short"]);
        Assert.Equal("LONG cannot exceed 3 characters.", byField["long"]);
        Assert.Equal("Use the format ABC-12.", byField["code"]);
        Assert.Equal("PLAINREGEX format is invalid.", byField["plainRegex"]);

        Assert.True((await Run(db, Values(("short", "abcde"), ("long", "abc"), ("code", "ABC-12"), ("plainRegex", "123")))).IsValid);
    }

    [Fact]
    public async Task AnUnusablePattern_NeverLocksEveryoneOut()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("broken", f => f.ValidationRegex = "([unclosed"));
        await db.SaveChangesAsync();

        Assert.True((await Run(db, Values(("broken", "anything")))).IsValid);
    }

    [Theory]
    [InlineData("Email", "a@b.co", true)]
    [InlineData("Email", "not-an-email", false)]
    [InlineData("Phone", "+60 12-345 6789", true)]
    [InlineData("Phone", "12345", false)]
    [InlineData("Phone", "call me maybe", false)]
    [InlineData("Date", "2026-03-15", true)]
    [InlineData("Date", "31/31/2026", false)]
    [InlineData("Number", "1234.50", true)]
    [InlineData("Number", "12a", false)]
    [InlineData("Checkbox", "true", true)]
    [InlineData("Checkbox", "FALSE", true)]
    [InlineData("Checkbox", "yes", false)]
    [InlineData("Text", "anything goes", true)]
    public async Task FieldType_DecidesWhatIsAcceptable(string fieldType, string value, bool expectedValid)
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Field("f", c => c.FieldType = fieldType));
        await db.SaveChangesAsync();

        Assert.Equal(expectedValid, (await Run(db, Values(("f", value)))).IsValid);
    }

    [Fact]
    public async Task Dropdown_AcceptsOnlyActiveConfiguredOptions_AnyCasing_AndReturnsTheConfiguredSpelling()
    {
        using var db = NewDb();
        var type = new LookupType { Id = Guid.NewGuid(), Code = "COLOURS", Name = "Colours", Description = "", CreatedAt = DateTime.UtcNow };
        db.LookupTypes.Add(type);
        db.LookupValues.AddRange(
            new LookupValue { Id = Guid.NewGuid(), LookupTypeId = type.Id, TypeCode = "COLOURS", Value = "Red", Label = "Red", IsActive = true, CreatedAt = DateTime.UtcNow },
            new LookupValue { Id = Guid.NewGuid(), LookupTypeId = type.Id, TypeCode = "COLOURS", Value = "Blue", Label = "Blue", IsActive = false, CreatedAt = DateTime.UtcNow });
        db.FieldConfigurations.Add(Field("colour", f => { f.FieldType = "Dropdown"; f.LookupTypeCode = "COLOURS"; }));
        await db.SaveChangesAsync();

        var ok = await Run(db, Values(("colour", "rEd")));
        Assert.True(ok.IsValid);
        Assert.Equal("Red", ok.Normalized["colour"]);

        Assert.False((await Run(db, Values(("colour", "Blue")))).IsValid);     // deactivated
        Assert.False((await Run(db, Values(("colour", "Green")))).IsValid);    // never configured
    }

    [Fact]
    public async Task CustomFields_MustBeConfigured_AndTheirRulesApplyLikeAnyOther()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            Field("taxId", f => { f.IsCustomField = true; f.IsRequired = true; f.MinLength = 6; }),
            Field("hiddenCustom", f => { f.IsCustomField = true; f.IsVisible = false; }));
        await db.SaveChangesAsync();

        var tooShort = await Run(db, Values(("taxId", "123")), custom: new[] { "taxId" });
        Assert.Contains(tooShort.Errors, e => e.Field == "taxId" && e.Message.Contains("at least 6"));

        var unknown = await Run(db, Values(("taxId", "123456"), ("surprise", "x")), custom: new[] { "taxId", "surprise" });
        Assert.Contains(unknown.Errors, e => e.Field == "surprise" && e.Message.Contains("not a configured field"));

        var hidden = await Run(db, Values(("taxId", "123456"), ("hiddenCustom", "x")), custom: new[] { "taxId", "hiddenCustom" });
        Assert.Contains(hidden.Errors, e => e.Field == "hiddenCustom");

        var badKey = await Run(db, Values(("taxId", "123456"), ("a b;--", "x")), custom: new[] { "taxId", "a b;--" });
        Assert.Contains(badKey.Errors, e => e.Field == "a b;--");

        Assert.True((await Run(db, Values(("taxId", "123456")), custom: new[] { "taxId" })).IsValid);
    }

    [Fact]
    public async Task EveryErrorIsReportedTogether_AndAFieldAnotherRuleOwnsIsNotReportedAsRequired()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            Field("a", f => f.IsRequired = true),
            Field("b", f => f.IsRequired = true),
            Field("priority", f => f.IsRequired = true));
        await db.SaveChangesAsync();

        var result = await Run(db, Values(), handledElsewhere: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "priority" });

        Assert.Equal(new[] { "a", "b" }, result.Errors.Select(e => e.Field).OrderBy(x => x));
        var ex = Assert.Throws<FieldValidationException>(() => result.ThrowIfInvalid());
        Assert.Equal(2, ex.Errors.Count);
        Assert.IsAssignableFrom<ArgumentException>(ex);   // existing 400 handling still applies
    }

    [Fact]
    public async Task ConfigurationChanges_AreSeenOnTheNextCall_OnceTheCacheIsInvalidated()
    {
        using var db = NewDb();
        var cache = NewCache();
        db.FieldConfigurations.Add(Field("note", f => f.IsRequired = false));
        await db.SaveChangesAsync();

        Assert.True((await Run(db, Values(("note", "")), cache: cache)).IsValid);

        var row = await db.FieldConfigurations.SingleAsync();
        row.IsRequired = true;
        await db.SaveChangesAsync();
        cache.InvalidateAll();   // ConfigChangeInterceptor does this on every real save

        Assert.False((await Run(db, Values(("note", "")), cache: cache)).IsValid);
    }
}

public class CustomerValidatorStructuralTests
{
    private static CreateCustomerDto Valid() => new()
    {
        FullName = "Ahmad Razak", IdType = "NRIC Number", IdValue = "900101-14-1234",
        DateOfBirth = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void OptionalFields_MayBeOmitted_RequirednessIsTheEnginesJob()
    {
        // Phone, email, language and branch used to be unconditionally required here, regardless of configuration.
        var result = new CreateCustomerDtoValidator().Validate(Valid());
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Theory]
    [InlineData("Passport Number", "AB12", false)]
    [InlineData("Passport Number", "A98765432", true)]
    [InlineData("Account Number", "AC", false)]
    [InlineData("Account Number", "ACC-12345", true)]
    [InlineData("NRIC Number", "900101-14-1234", true)]
    [InlineData("NRIC Number", "900101-14-123", false)]
    public void IdFormats_FollowTheRuleOfTheIdType_ByDefault(string idType, string value, bool valid)
    {
        var format = new IdFormat(IdFormatRules.DefaultFor(idType), null, null);
        Assert.Equal(valid, IdFormatRules.Check(format, value) == null);
    }
}

public class FieldSettingsGuardTests
{
    private static ConfigurableSettingsService Service(AppDbContext db) =>
        new(new ConfigurableSettingsRepository(db), db, new Mock<INotificationService>().Object, new Mock<IHttpContextAccessor>().Object);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static FieldConfiguration Row(string apiField, bool system = false, bool custom = false) => new()
    {
        Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = apiField, DisplayLabel = apiField,
        FieldType = "Text", IsVisible = true, IsRequired = system, IsSystemRequired = system, IsCustomField = custom, CreatedAt = DateTime.UtcNow
    };

    private static UpdateFieldConfigurationDto Update(Action<UpdateFieldConfigurationDto>? configure = null)
    {
        var dto = new UpdateFieldConfigurationDto { DisplayLabel = "Label", FieldType = "Text", IsVisible = true };
        configure?.Invoke(dto);
        return dto;
    }

    [Fact]
    public async Task AnInvalidPattern_IsRejectedWhenSaved_NotSilentlyIgnoredLater()
    {
        using var db = NewDb();
        var row = Row("notes"); db.FieldConfigurations.Add(row); await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Service(db).UpdateFieldConfigurationAsync(row.Id, Update(d => d.ValidationRegex = "([unclosed")));
        Assert.Contains("not a valid regular expression", ex.Message);
    }

    [Fact]
    public async Task NonsenseMetadata_IsRejected()
    {
        using var db = NewDb();
        var row = Row("notes"); db.FieldConfigurations.Add(row); await db.SaveChangesAsync();
        var service = Service(db);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UpdateFieldConfigurationAsync(row.Id, Update(d => d.FieldType = "Hologram")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UpdateFieldConfigurationAsync(row.Id, Update(d => { d.MinLength = 10; d.MaxLength = 5; })));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UpdateFieldConfigurationAsync(row.Id, Update(d => d.MinLength = -1)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.UpdateFieldConfigurationAsync(row.Id, Update(d => { d.FieldType = "Dropdown"; d.LookupTypeCode = "NO_SUCH_LIST"; })));
    }

    [Fact]
    public async Task ThePerFieldUpdate_NowPersistsRegexLengthsAndMessage()
    {
        // The single-field PUT used to drop these silently; only the bulk save kept them.
        using var db = NewDb();
        var row = Row("notes"); db.FieldConfigurations.Add(row); await db.SaveChangesAsync();

        var updated = await Service(db).UpdateFieldConfigurationAsync(row.Id, Update(d =>
        {
            d.ValidationRegex = "^[A-Z]"; d.ValidationMessage = "Start with a capital."; d.MinLength = 2; d.MaxLength = 40;
        }));

        Assert.Equal("^[A-Z]", updated!.ValidationRegex);
        var stored = await db.FieldConfigurations.AsNoTracking().SingleAsync();
        Assert.Equal(("^[A-Z]", "Start with a capital.", 2, 40), (stored.ValidationRegex, stored.ValidationMessage, stored.MinLength, stored.MaxLength));
    }

    [Fact]
    public async Task ASystemRequiredField_CannotBeMadeOptionalHiddenOrDeleted()
    {
        using var db = NewDb();
        var row = Row("title", system: true); db.FieldConfigurations.Add(row); await db.SaveChangesAsync();
        var service = Service(db);

        var updated = await service.UpdateFieldConfigurationAsync(row.Id, Update(d => { d.IsRequired = false; d.IsVisible = false; }));
        Assert.True(updated!.IsRequired);
        Assert.True(updated.IsVisible);
        Assert.True(updated.IsSystemRequired);

        // The bulk save the Settings screen uses honours the lock too.
        await service.SaveFieldConfigurationsAsync(new UpdateFieldConfigurationsRequest
        {
            ModuleKey = "CaseManagement", SectionKey = "CreateCase",
            Update = new() { new FieldConfigurationDto { Id = row.Id, ApiField = "title", DisplayLabel = "Title", FieldType = "Text", IsRequired = false, IsVisible = false } }
        });
        var stored = await db.FieldConfigurations.AsNoTracking().SingleAsync();
        Assert.True(stored.IsRequired && stored.IsVisible);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteFieldConfigurationAsync(row.Id));
        Assert.Contains("cannot be deleted", ex.Message);
    }

    [Theory]
    [InlineData("title")]            // built-in key
    [InlineData("selectCustomer")]
    [InlineData("1starts_with_digit")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public async Task CustomFieldKeys_MustBePlainIdentifiers_AndNeverShadowBuiltInFields(string key)
    {
        using var db = NewDb();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service(db).AddCustomFieldAsync(new CreateCustomFieldDto
        {
            ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = key, DisplayLabel = "Custom", FieldType = "Text"
        }));
    }

    [Fact]
    public async Task ACustomField_CanBeAddedWithItsValidationRules()
    {
        using var db = NewDb();
        var created = await Service(db).AddCustomFieldAsync(new CreateCustomFieldDto
        {
            ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "policyNumber", DisplayLabel = "Policy Number",
            FieldType = "Text", IsRequired = true, ValidationRegex = "^POL-\\d{6}$", ValidationMessage = "Format POL-123456", MinLength = 10, MaxLength = 10
        });

        Assert.True(created.IsCustomField);
        Assert.Equal("^POL-\\d{6}$", created.ValidationRegex);
        Assert.Equal(10, created.MinLength);
    }
}
