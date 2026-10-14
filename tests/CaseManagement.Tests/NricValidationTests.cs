using CaseManagement.Api.DTOs;
using CaseManagement.Api.Services;
using CaseManagement.Api.Validators;
using System;
using Xunit;

namespace CaseManagement.Tests;

public class NricValidationTests
{
    [Theory]
    [InlineData("900101-14-1234")] // Valid male (gender digit 4? wait, 4 is female, still numeric)
    [InlineData("950512-10-5671")] // Valid male (odd gender 1)
    [InlineData("040229-01-9992")] // Valid leap year Feb 29, 2004
    [InlineData("000229-08-3333")] // Valid leap year Feb 29, 2000
    [InlineData("881231-99-4444")] // Valid max PB (99), Dec 31
    [InlineData("990715-01-0015")] // Valid min PB (01)
    public void BeValidNric_ValidCases_ReturnsTrue(string nric)
    {
        var isValid = CreateCustomerDtoValidator.BeValidNric(nric);
        Assert.True(isValid);
    }

    [Theory]
    [InlineData("")] // Empty
    [InlineData("   ")] // Whitespace
    [InlineData("900101-14-123")] // Fewer characters (serial 2 digits)
    [InlineData("900101-14-12345")] // More characters
    [InlineData("900101141234")] // Missing hyphens
    [InlineData("9001-0114-1234")] // Wrong hyphen position
    [InlineData("900101-1A-1234")] // Alphabetic in PB
    [InlineData("90010A-14-1234")] // Alphabetic in date
    [InlineData("901301-14-1234")] // Invalid month 13
    [InlineData("900001-14-1234")] // Invalid month 00
    [InlineData("900100-14-1234")] // Invalid day 00
    [InlineData("900132-14-1234")] // Invalid day 32 (Jan max 31)
    [InlineData("900431-14-1234")] // Impossible date (Apr has 30 days)
    [InlineData("910229-14-1234")] // Impossible date (1991 is not a leap year, Feb 29 invalid)
    [InlineData("930229-14-1234")] // Impossible date (1993 is not a leap year)
    [InlineData("900101-00-1234")] // PB 00 out of range (01-99)
    [InlineData("900101-1-1234")]  // Invalid PB length (1 digit)
    [InlineData("900101-14-123A")] // Non-numeric gender indicator
    public void BeValidNric_InvalidCases_ReturnsFalse(string nric)
    {
        var isValid = CreateCustomerDtoValidator.BeValidNric(nric);
        Assert.False(isValid);
    }

    [Fact]
    public void NricRule_InvalidNric_YieldsCorrectErrorMessage()
    {
        var error = IdFormatRules.Check(new IdFormat(IdFormatRules.MyNric, null, null), "900101-14-123");   // malformed
        Assert.Equal("Please enter in correct format", error);
    }

    [Fact]
    public void NricRule_DateOfBirthMismatch_YieldsClearErrorMessage()
    {
        var nric = new IdFormat(IdFormatRules.MyNric, null, null);
        var error = IdFormatRules.CheckAgainstDateOfBirth(nric, "900101-14-1234", new DateTime(1995, 5, 20, 0, 0, 0, DateTimeKind.Utc));   // encodes 1990-01-01
        Assert.Equal("Date of Birth does not match the date in the NRIC number.", error);
        Assert.Null(IdFormatRules.CheckAgainstDateOfBirth(nric, "900101-14-1234", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void TheNricRule_IsConfiguration_NotAFixedProperty()
    {
        // Same value, same ID type: it is the configured rule that decides. An organisation that moves to alphanumeric IDs changes the rule only.
        Assert.NotNull(IdFormatRules.Check(new IdFormat(IdFormatRules.MyNric, null, null), "AB12345678"));
        Assert.Null(IdFormatRules.Check(new IdFormat(IdFormatRules.Alphanumeric, null, null), "AB12345678"));
        Assert.Null(IdFormatRules.CheckAgainstDateOfBirth(new IdFormat(IdFormatRules.Alphanumeric, null, null), "900101-14-1234", new DateTime(1995, 5, 20)));   // no DOB cross-check without the NRIC rule
        Assert.Null(IdFormatRules.Check(new IdFormat(IdFormatRules.Any, null, null), "anything"));
    }

    [Fact]
    public void CustomPatternRule_UsesItsPatternAndMessage_AndIsCheckedWhenSaved()
    {
        var format = new IdFormat(IdFormatRules.Regex, "^[A-Z]{2}\\d{6}$", "Two letters then six digits.");
        Assert.Null(IdFormatRules.Check(format, "AB123456"));
        Assert.Equal("Two letters then six digits.", IdFormatRules.Check(format, "123"));
        Assert.NotNull(IdFormatRules.CheckDefinition(IdFormatRules.Regex, "([unclosed"));
        Assert.NotNull(IdFormatRules.CheckDefinition(IdFormatRules.Regex, null));
        Assert.NotNull(IdFormatRules.CheckDefinition("NOPE", null));
        Assert.Null(IdFormatRules.CheckDefinition(IdFormatRules.Passport, null));
    }
}
