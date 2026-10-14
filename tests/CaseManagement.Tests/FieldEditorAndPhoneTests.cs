using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CaseManagement.Tests;

/// <summary>Display order, type-aware field definitions, the batch save, list options and country-aware phone rules.</summary>
public class FieldEditorAndPhoneTests
{
    private const string Module = "Customer360", Section = "AddNewCustomer";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ConfigurableSettingsService Service(AppDbContext db) =>
        new(new ConfigurableSettingsRepository(db), db, new Mock<INotificationService>().Object, new Mock<IHttpContextAccessor>().Object);

    private static FieldConfiguration Row(string apiField, int order, string type = "Text") => new()
    {
        Id = Guid.NewGuid(), ModuleKey = Module, SectionKey = Section, ApiField = apiField, DisplayLabel = apiField,
        DisplayOrder = order, FieldType = type, IsVisible = true, CreatedAt = DateTime.UtcNow
    };

    private static FieldConfigurationDto Dto(FieldConfiguration f, int? order = null) => FieldConfigurationDto.From(f).Also(d => { if (order.HasValue) d.DisplayOrder = order.Value; });

    private static Task<IReadOnlyList<FieldConfigurationDto>> Save(ConfigurableSettingsService s, IEnumerable<FieldConfigurationDto> update, IEnumerable<CreateCustomFieldDto>? create = null) =>
        s.SaveFieldConfigurationsAsync(new UpdateFieldConfigurationsRequest { ModuleKey = Module, SectionKey = Section, Update = update.ToList(), Create = (create ?? Array.Empty<CreateCustomFieldDto>()).ToList() });

    // ------------------------------------------------------------------------------------------ display order

    [Fact]
    public async Task ADuplicateDisplayOrder_IsRejected_WithAClearMessage_AndNothingIsSaved()
    {
        using var db = NewDb();
        var dob = Row("dateOfBirth", 3); var id = Row("idValue", 4);
        db.FieldConfigurations.AddRange(dob, id); await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), new[] { Dto(dob), Dto(id, 3) }));

        Assert.Contains(ex.Errors, e => e.Message == "Display order 3 is already assigned to another field. Please choose a different display order.");
        Assert.Equal(4, (await db.FieldConfigurations.AsNoTracking().SingleAsync(f => f.ApiField == "idValue")).DisplayOrder);
    }

    [Fact]
    public async Task SwappingTwoOrders_InOneSave_IsAllowed_AndSavedFieldsComeBackSorted()
    {
        using var db = NewDb();
        var a = Row("a", 1); var b = Row("b", 2);
        db.FieldConfigurations.AddRange(a, b); await db.SaveChangesAsync();

        var saved = await Save(Service(db), new[] { Dto(a, 2), Dto(b, 1) });

        Assert.Equal(new[] { "b", "a" }, saved.OrderBy(f => f.DisplayOrder).Select(f => f.ApiField));
    }

    [Fact]
    public async Task PreExistingDuplicates_AmongUntouchedFields_DoNotBlockAnUnrelatedSave()
    {
        using var db = NewDb();
        var a = Row("a", 9); var b = Row("b", 9); var c = Row("c", 5);
        db.FieldConfigurations.AddRange(a, b, c); await db.SaveChangesAsync();

        var changed = Dto(c); changed.DisplayLabel = "Renamed";
        await Save(Service(db), new[] { Dto(a), Dto(b), changed });

        Assert.Equal("Renamed", (await db.FieldConfigurations.AsNoTracking().SingleAsync(f => f.ApiField == "c")).DisplayLabel);
    }

    [Fact]
    public async Task ANewField_HasNoIdFromTheClient_TheServerIssuesIt_AndItsOrderMustBeFree()
    {
        using var db = NewDb();
        db.FieldConfigurations.Add(Row("fullName", 1)); await db.SaveChangesAsync();
        var service = Service(db);

        var clash = await Assert.ThrowsAsync<FieldValidationException>(() => Save(service, Array.Empty<FieldConfigurationDto>(),
            new[] { new CreateCustomFieldDto { ModuleKey = Module, SectionKey = Section, DisplayLabel = "Occupation", FieldType = "Dropdown", DisplayOrder = 1 } }));
        Assert.Contains("Display order 1", clash.Message);

        var saved = await Save(service, Array.Empty<FieldConfigurationDto>(),
            new[] { new CreateCustomFieldDto { ModuleKey = Module, SectionKey = Section, DisplayLabel = "Occupation", FieldType = "Text", DisplayOrder = 2, IsRequired = true } });
        var created = saved.Single(f => f.DisplayLabel == "Occupation");
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.True(created.IsCustomField && created.IsRequired);
    }

    // ------------------------------------------------------------------------------------- type-aware definitions

    [Fact]
    public async Task SettingsThatDoNotApplyToTheType_AreClearedOnSave()
    {
        using var db = NewDb();
        var f = Row("age", 1); f.IsCustomField = true; f.ValidationRegex = "^abc"; f.ValidationMessage = "m"; f.MaxLength = 5;
        db.FieldConfigurations.Add(f); await db.SaveChangesAsync();

        var dto = Dto(f); dto.FieldType = "Number"; dto.MinValue = "18"; dto.MaxValue = "99";
        var saved = (await Save(Service(db), new[] { dto })).Single();

        Assert.Null(saved.ValidationRegex); Assert.Null(saved.ValidationMessage); Assert.Null(saved.MaxLength);
        Assert.Equal(("18", "99"), (saved.MinValue, saved.MaxValue));
    }

    [Theory]
    [InlineData("Number", "abc", null, "minValue")]
    [InlineData("Number", "10", "5", "maxValue")]
    [InlineData("Date", "not-a-date", null, "minValue")]
    public async Task InvalidRanges_AreRejected(string type, string min, string? max, string field)
    {
        using var db = NewDb();
        var f = Row("x", 1); db.FieldConfigurations.Add(f); await db.SaveChangesAsync();
        var dto = Dto(f); dto.FieldType = type; dto.MinValue = min; dto.MaxValue = max;

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), new[] { dto }));
        Assert.Contains(ex.Errors, e => e.Field == f.ApiField);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        _ = field;
    }

    [Fact]
    public async Task NumberAndDateRanges_AreEnforcedOnValues()
    {
        using var db = NewDb();
        db.FieldConfigurations.AddRange(
            Row("age", 1, "Number").Also(f => { f.MinValue = "18"; f.MaxValue = "65"; }),
            Row("joined", 2, "Date").Also(f => { f.MinValue = "2020-01-01"; f.MaxValue = "2030-12-31"; }));
        await db.SaveChangesAsync();
        var engine = new FieldValidationEngine(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())), NullLogger<FieldValidationEngine>.Instance);

        IReadOnlyDictionary<string, string?> V(string age, string joined) => new Dictionary<string, string?> { ["age"] = age, ["joined"] = joined };

        Assert.True((await engine.ValidateAsync(Module, Section, V("30", "2024-05-01"))).IsValid);
        var bad = await engine.ValidateAsync(Module, Section, V("12", "2019-12-31"));
        Assert.Equal(2, bad.Errors.Count);
        Assert.Contains("at least 18", bad.Errors.First(e => e.Field == "age").Message);
        Assert.Contains("cannot be before 2020-01-01", bad.Errors.First(e => e.Field == "joined").Message);
        Assert.Contains("cannot exceed 65", (await engine.ValidateAsync(Module, Section, V("70", "2024-05-01"))).Errors.Single().Message);
    }

    // ------------------------------------------------------------------------------------------- list options

    private static async Task SeedList(AppDbContext db, bool allowAdd = true)
    {
        var type = new LookupType { Id = Guid.NewGuid(), Code = "LANG", Name = "Language", AllowAdd = allowAdd, CreatedAt = DateTime.UtcNow };
        db.LookupTypes.Add(type);
        db.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), LookupTypeId = type.Id, TypeCode = "LANG", Value = "English", Label = "English", DisplayOrder = 1, IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task OptionsAddedFromTheFieldDrawer_ReachEveryFormThatUsesTheList()
    {
        using var db = NewDb(); await SeedList(db);
        db.FieldConfigurations.AddRange(
            Row("preferredLanguage", 1, "Dropdown").Also(f => f.LookupTypeCode = "LANG"),
            new FieldConfiguration { Id = Guid.NewGuid(), ModuleKey = "CaseManagement", SectionKey = "CreateCase", ApiField = "preferredLanguage", DisplayLabel = "Lang", FieldType = "Dropdown", LookupTypeCode = "LANG", DisplayOrder = 1, IsVisible = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var english = await db.LookupValues.AsNoTracking().SingleAsync();

        var all = await Service(db).SaveLookupValuesAsync("LANG", new SaveLookupValuesRequest
        {
            Values = new()
            {
                new LookupValueDraftDto { Id = english.Id, Value = "English", Label = "English", DisplayOrder = 1, IsActive = true },
                new LookupValueDraftDto { Value = "Hindi", Label = "Hindi", DisplayOrder = 2, IsActive = true }
            }
        });
        Assert.Equal(2, all.Count);

        var meta = new MetadataService(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())));
        Assert.Contains("Hindi", (await meta.GetCustomerFormAsync()).Lookups["LANG"].Select(o => o.Value));
        Assert.Contains("Hindi", (await meta.GetCaseFormAsync()).Lookups["LANG"].Select(o => o.Value));
    }

    [Fact]
    public async Task ADisabledOption_DisappearsFromForms_ButTheRowAndStoredValuesRemain()
    {
        using var db = NewDb(); await SeedList(db);
        db.FieldConfigurations.Add(Row("preferredLanguage", 1, "Dropdown").Also(f => f.LookupTypeCode = "LANG")); await db.SaveChangesAsync();
        var english = await db.LookupValues.AsNoTracking().SingleAsync();

        await Service(db).SaveLookupValuesAsync("LANG", new SaveLookupValuesRequest
        {
            Values = new() { new LookupValueDraftDto { Id = english.Id, Value = "English", Label = "English", DisplayOrder = 1, IsActive = false } }
        });

        Assert.Empty((await new MetadataService(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions()))).GetCustomerFormAsync()).Lookups["LANG"]);
        Assert.Equal(1, await db.LookupValues.CountAsync());
    }

    [Fact]
    public async Task AStoredValue_CannotBeRenamed_ButItsLabelCanChange()
    {
        using var db = NewDb(); await SeedList(db);
        var english = await db.LookupValues.AsNoTracking().SingleAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).UpdateLookupValueAsync(english.Id,
            new UpdateLookupValueDto { Value = "Inggeris", Label = "Inggeris", DisplayOrder = 1, IsActive = true }));

        var updated = await Service(db).UpdateLookupValueAsync(english.Id,
            new UpdateLookupValueDto { Value = "English", Label = "English (UK)", DisplayOrder = 1, IsActive = true });
        Assert.Equal("English (UK)", updated!.Label);
    }

    [Fact]
    public async Task AFixedList_CannotGetNewOptions()
    {
        using var db = NewDb(); await SeedList(db, allowAdd: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).AddLookupValueAsync(new CreateLookupValueDto { TypeCode = "LANG", Value = "Klingon" }));
        await Assert.ThrowsAsync<FieldValidationException>(() => Service(db).SaveLookupValuesAsync("LANG",
            new SaveLookupValuesRequest { Values = new() { new LookupValueDraftDto { Value = "Klingon", Label = "Klingon" } } }));
        Assert.Equal(1, await db.LookupValues.CountAsync());
    }

    [Fact]
    public async Task OptionBatch_IsAllOrNothing()
    {
        using var db = NewDb(); await SeedList(db);

        await Assert.ThrowsAsync<FieldValidationException>(() => Service(db).SaveLookupValuesAsync("LANG", new SaveLookupValuesRequest
        {
            Values = new()
            {
                new LookupValueDraftDto { Value = "Hindi", Label = "Hindi" },
                new LookupValueDraftDto { Value = "hindi", Label = "Hindi again" }   // duplicate -> whole batch refused
            }
        }));
        Assert.Equal(1, await db.LookupValues.CountAsync());
    }

    // ----------------------------------------------------------------------------------------------------- phone

    private static async Task<CountryService> Countries(AppDbContext db)
    {
        db.Countries.AddRange(
            new Country { Id = Guid.NewGuid(), Iso2 = "MY", Iso3 = "MYS", Name = "Malaysia", DialCode = "60", MinNationalDigits = 10, MaxNationalDigits = 10, IsActive = true, CreatedAt = DateTime.UtcNow },
            new Country { Id = Guid.NewGuid(), Iso2 = "IN", Iso3 = "IND", Name = "India", DialCode = "91", MinNationalDigits = 10, MaxNationalDigits = 10, NationalPattern = "^[6-9]", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Country { Id = Guid.NewGuid(), Iso2 = "SG", Iso3 = "SGP", Name = "Singapore", DialCode = "65", MinNationalDigits = 8, MaxNationalDigits = 8, IsActive = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return new CountryService(db, new ConfigCache(new MemoryCache(new MemoryCacheOptions())));
    }

    [Theory]
    [InlineData("MY", "+60 123456789 0", "+60 1234567890")]
    [InlineData("MY", "012-345 67890", "+60 1234567890")]
    [InlineData("MY", "601234567890", "+60 1234567890")]
    [InlineData("IN", "+91 98765 43210", "+91 9876543210")]
    [InlineData("SG", "9123 4567", "+65 91234567")]
    [InlineData(null, "1234567890", "+60 1234567890")]   // default country: all existing data is Malaysian
    public async Task ValidNumbers_AreAcceptedPerCountry_AndStoredAsDialCodePlusDigits(string? iso, string input, string expected)
    {
        using var db = NewDb();
        var result = await (await Countries(db)).NormalizePhoneAsync(iso, input, "Phone Number");
        Assert.Null(result.Error);
        Assert.Equal(expected, result.Normalized);
    }

    [Theory]
    [InlineData("IN", "98765", "must have 10 digits")]                 // too short
    [InlineData("IN", "5876543210", "not a valid India")]               // country pattern
    [InlineData("IN", "+60 1234567890", "must start with +91")]         // wrong dial code for the chosen country
    [InlineData("SG", "9123456789012", "must have 8 digits")]
    [InlineData("MY", "12ab567890", "must be a valid phone number")]
    [InlineData("XX", "1234567890", "not a supported country")]
    public async Task InvalidNumbers_AreRejected_WithTheCountrysRuleInTheMessage(string iso, string input, string expectedFragment)
    {
        using var db = NewDb();
        var result = await (await Countries(db)).NormalizePhoneAsync(iso, input, "Phone Number");
        Assert.Contains(expectedFragment, result.Error);
        Assert.Contains("Phone Number", result.Error);   // the configured label, not a hard-coded one
    }

    [Fact]
    public void CountryDefaults_AreWellFormed()
    {
        var all = CountryDefaults.All().ToList();
        Assert.Equal(all.Count, all.Select(c => c.Iso2).Distinct().Count());
        Assert.All(all, c => { Assert.Equal(2, c.Iso2.Length); Assert.Equal(3, c.Iso3.Length); Assert.InRange(c.Min, 4, c.Max); Assert.InRange(c.Max, 4, 15 - c.Dial.Length); });
        Assert.Contains(all, c => c.Iso2 == "MY" && c.Dial == "60" && c.Min == 10 && c.Max == 10);   // existing Malaysian rule preserved
    }
}

/// <summary>The migration and seed, on a real PostgreSQL database (skipped unless TEST_POSTGRES_ADMIN_CONNECTION is set).</summary>
public class FieldMetadataMigrationTests
{
    [PostgresFact]
    public async Task AFreshDatabase_HasCountries_FixedIdTypes_NoPhoneRegex_AndTheDisplayOrderBackstop()
    {
        await using var temp = await TempDatabase.CreateAsync();
        await using (var ctx = temp.NewContext())
        {
            await ctx.Database.MigrateAsync();
            DbSeeder.Run(ctx, SeedMode.Bootstrap, adoptedLegacyDatabase: false);
        }

        Assert.True(await temp.ScalarAsync<long>("SELECT COUNT(*) FROM \"Countries\" WHERE \"Iso2\" IN ('MY','IN','SG')") == 3);
        Assert.False(await temp.ScalarAsync<bool>("SELECT \"AllowAdd\" FROM \"LookupTypes\" WHERE \"Code\" = 'ID_TYPE'"));
        Assert.True(await temp.ScalarAsync<bool>("SELECT \"AllowAdd\" FROM \"LookupTypes\" WHERE \"Code\" = 'PREFERRED_LANGUAGE'"));
        Assert.Equal(0L, await temp.ScalarAsync<long>("SELECT COUNT(*) FROM \"FieldConfigurations\" WHERE \"ApiField\" = 'phoneNumber' AND \"ValidationRegex\" IS NOT NULL"));
        Assert.Equal(1L, await temp.ScalarAsync<long>("SELECT COUNT(*) FROM pg_constraint WHERE conname = 'UQ_FieldConfigurations_DisplayOrder'"));

        // The backstop really refuses two fields of one form with the same order.
        await using var db = temp.NewContext();
        var section = db.FieldConfigurations.First(f => f.ModuleKey == "Customer360" && f.SectionKey == "AddNewCustomer");
        db.FieldConfigurations.Add(new FieldConfiguration
        {
            Id = Guid.NewGuid(), ModuleKey = section.ModuleKey, SectionKey = section.SectionKey, ApiField = "dupOrder", DisplayLabel = "Dup",
            DisplayOrder = section.DisplayOrder, FieldType = "Text", CreatedAt = DateTime.UtcNow
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
