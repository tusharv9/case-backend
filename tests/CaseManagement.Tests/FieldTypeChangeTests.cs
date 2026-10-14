using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CaseManagement.Tests;

/// <summary>Changing the type of a field (built-in ones included): allowed only when storage can hold it and stored data still fits.</summary>
public class FieldTypeChangeTests
{
    private const string Incompatible = "Cannot change field type because existing customer data does not match the selected type.";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ConfigurableSettingsService Service(AppDbContext db) =>
        new(new ConfigurableSettingsRepository(db), db, new Mock<INotificationService>().Object, new Mock<IHttpContextAccessor>().Object);

    private static FieldConfiguration Field(string apiField, string type, bool custom = false, string module = "Customer360", string section = "AddNewCustomer") => new()
    {
        Id = Guid.NewGuid(), ModuleKey = module, SectionKey = section, ApiField = apiField, DisplayLabel = apiField, FieldType = type,
        DisplayOrder = Random.Shared.Next(1, 10_000), IsVisible = true, IsCustomField = custom, CreatedAt = DateTime.UtcNow
    };

    private static Customer Cust(string name, string? nric = null, string? passport = null, string email = "a@x.com", DateTime? dob = null) => new()
    {
        Id = Guid.NewGuid(), FullName = name, NRIC = nric, Passport = passport, Email = email, IdType = passport != null ? "Passport Number" : "NRIC Number",
        PhoneNumber = "+60 " + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999), DateOfBirth = dob, CreatedAt = DateTime.UtcNow
    };

    private static FieldConfigurationDto As(FieldConfiguration f, string type) => FieldConfigurationDto.From(f).Also(d => d.FieldType = type);

    private static Task Save(ConfigurableSettingsService s, FieldConfigurationDto dto) =>
        s.SaveFieldConfigurationsAsync(new UpdateFieldConfigurationsRequest { ModuleKey = dto.ModuleKey, SectionKey = dto.SectionKey, Update = new() { dto } });

    [Fact]
    public async Task ABuiltInField_CanChangeType_WhenEveryStoredValueFitsTheNewType()
    {
        using var db = NewDb();
        var idValue = Field("idValue", "Text"); db.FieldConfigurations.Add(idValue);
        db.Customers.AddRange(Cust("A", nric: "900101141234"), Cust("B", nric: "950512105671"));   // digits only: fits Number
        await db.SaveChangesAsync();

        await Save(Service(db), As(idValue, "Number"));

        Assert.Equal("Number", (await db.FieldConfigurations.AsNoTracking().SingleAsync()).FieldType);
    }

    [Fact]
    public async Task IfAnyStoredValueDoesNotFit_TheChangeIsRefused_WithTheMessage_AndNothingChanges()
    {
        using var db = NewDb();
        var idValue = Field("idValue", "Text"); db.FieldConfigurations.Add(idValue);
        db.Customers.AddRange(Cust("A", nric: "900101141234"), Cust("B", passport: "A98765432"));   // a passport has letters
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), As(idValue, "Number")));

        Assert.StartsWith(Incompatible, ex.Message);
        Assert.Equal("Text", (await db.FieldConfigurations.AsNoTracking().SingleAsync()).FieldType);
        Assert.DoesNotContain("A98765432", ex.Message);   // counts only: no personal data in the message
    }

    [Fact]
    public async Task TheRulesOfTheNewType_AreAppliedToStoredValues()
    {
        using var db = NewDb();
        var name = Field("fullName", "Text"); db.FieldConfigurations.Add(name);
        db.Customers.Add(Cust("Ahmad Razak")); await db.SaveChangesAsync();

        // "Ahmad Razak" is not an email address, so the stored value blocks the change...
        await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), As(name, "Email")));

        // ...and a length rule that the stored value breaks blocks even a same-category change.
        var tight = As(name, "Number"); tight.MaxLength = 3;
        await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), tight));
    }

    [Fact]
    public async Task ADropdown_AcceptsStoredValuesOfEveryOption_EvenInactiveOnes_ButNotValuesOutsideTheList()
    {
        using var db = NewDb();
        var type = new LookupType { Id = Guid.NewGuid(), Code = "LANG", Name = "L", CreatedAt = DateTime.UtcNow };
        db.LookupTypes.Add(type);
        db.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), LookupTypeId = type.Id, TypeCode = "LANG", Value = "English", Label = "English", IsActive = false, CreatedAt = DateTime.UtcNow });
        var lang = Field("preferredLanguage", "Text"); db.FieldConfigurations.Add(lang);
        db.Customers.Add(Cust("Y").Also(c => c.PreferredLanguage = "English")); await db.SaveChangesAsync();

        var toDropdown = As(lang, "Dropdown"); toDropdown.LookupTypeCode = "LANG";
        await Save(Service(db), toDropdown);                                                   // "English" is inactive but valid history
        Assert.Equal("Dropdown", (await db.FieldConfigurations.AsNoTracking().SingleAsync()).FieldType);

        var other = Field("branch", "Text"); db.FieldConfigurations.Add(other);
        db.Customers.Add(Cust("X").Also(c => c.Branch = "Klingon HQ")); await db.SaveChangesAsync();
        var branchDropdown = As(other, "Dropdown"); branchDropdown.LookupTypeCode = "LANG";    // "Klingon HQ" is not in the list
        await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), branchDropdown));
    }

    [Fact]
    public async Task StorageLimitsWhatABuiltInFieldCanBecome_AndTheFieldAdvertisesIt()
    {
        using var db = NewDb();
        var dob = Field("dateOfBirth", "Date"); var phone = Field("phoneNumber", "Phone"); var email = Field("email", "Email");
        db.FieldConfigurations.AddRange(dob, phone, email); await db.SaveChangesAsync();
        var service = Service(db);

        Assert.Equal(new[] { "Date" }, FieldConfigurationDto.From(dob).AllowedFieldTypes);
        Assert.Contains("Text", FieldConfigurationDto.From(phone).AllowedFieldTypes!);
        Assert.DoesNotContain("Date", FieldConfigurationDto.From(email).AllowedFieldTypes!);

        var ex = await Assert.ThrowsAsync<FieldValidationException>(() => Save(service, As(dob, "Number")));
        Assert.Contains("only supports Date", ex.Message);
    }

    [Fact]
    public async Task ACustomField_IsCheckedAgainstItsStoredAttributes()
    {
        using var db = NewDb();
        var f = Field("employeeId", "Text", custom: true); db.FieldConfigurations.Add(f);
        var c = Cust("A"); c.CustomAttributes.Add(new CustomerCustomAttribute { Id = Guid.NewGuid(), CustomerId = c.Id, FieldKey = "employeeId", FieldValue = "E-1001" });
        db.Customers.Add(c); await db.SaveChangesAsync();

        await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), As(f, "Number")));
        await Save(Service(db), As(f, "Text"));    // unchanged type: nothing to prove
    }

    [Fact]
    public async Task TheDryRun_AnswersWithoutChangingAnything()
    {
        using var db = NewDb();
        var idValue = Field("idValue", "Text"); db.FieldConfigurations.Add(idValue);
        db.Customers.Add(Cust("B", passport: "A98765432")); await db.SaveChangesAsync();

        var bad = await Service(db).CheckTypeChangeAsync(idValue.Id, As(idValue, "Number"));
        var ok = await Service(db).CheckTypeChangeAsync(idValue.Id, As(idValue, "Text"));

        Assert.False(bad!.Ok); Assert.StartsWith(Incompatible, bad.Message); Assert.True(bad.Mismatched > 0);
        Assert.True(ok!.Ok);
        Assert.Equal("Text", (await db.FieldConfigurations.AsNoTracking().SingleAsync()).FieldType);
    }

    [Fact]
    public async Task OtherBuiltInFormsKeepTheirTypes()
    {
        using var db = NewDb();
        var title = Field("title", "Text", module: "CaseManagement", section: "CreateCase"); db.FieldConfigurations.Add(title); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<FieldValidationException>(() => Save(Service(db), As(title, "Number")));
    }
}

public class PatternParityTests
{
    [Theory]
    [InlineData("^\\d+$", "123", true)]
    [InlineData("^\\d+$", "١٢٣", false)]            // Arabic-Indic digits: JavaScript's \d refuses them, so the server must too
    [InlineData("^[A-Z0-9]{6,12}$", "A1234567", true)]
    [InlineData("^[A-Z0-9]{6,12}$", "abc", false)]
    [InlineData("^(?<=x)y$", "y", false)]            // look-behind is .NET-only syntax: it still works (falls back) instead of throwing
    public void ServerAndBrowserAgree_OnWhatAPatternMatches(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, PatternMatcher.IsMatch(value, pattern));
    }

    [Fact]
    public void ATrulyInvalidPattern_StillThrows()
    {
        Assert.ThrowsAny<System.ArgumentException>(() => PatternMatcher.IsMatch("x", "([unclosed"));
    }
}
