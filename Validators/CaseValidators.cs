namespace CaseManagement.Api.Validators;

using CaseManagement.Api.DTOs;
using FluentValidation;

/// <summary>
/// Request-level checks that do not depend on configuration. Whether a field is required, which dropdown values are valid,
/// lengths, patterns and the format of an ID value are the administrator's configuration, applied by the validation engine
/// and <c>IdFormatRules</c>. The static <c>BeValid…</c> helpers below are the checkers those rules use.
/// </summary>
public class CreateCustomerDtoValidator : AbstractValidator<CreateCustomerDto>
{
    public CreateCustomerDtoValidator()
    {
        RuleFor(x => x.FullName).MaximumLength(100);

        // The format of an ID value (and whether it must match the date of birth) is configuration: the format rule of the
        // ID type, applied by CustomerService (see IdFormatRules). Only the type-independent check remains here.
        RuleFor(x => x.DateOfBirth)
            .LessThan(DateTime.UtcNow).WithMessage("Date of Birth must be in the past.")
            .When(x => x.DateOfBirth.HasValue);
    }

    public static bool BeValidNric(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var clean = id.Trim();

        // Expected structure: YYMMDD-PB-###G (14 chars)
        if (!System.Text.RegularExpressions.Regex.IsMatch(clean, @"^\d{6}-\d{2}-\d{4}$"))
            return false;

        var parts = clean.Split('-');
        var datePart = parts[0];
        var pbPart = parts[1];
        var serialGenderPart = parts[2];

        if (!int.TryParse(datePart[..2], out var yy) ||
            !int.TryParse(datePart[2..4], out var mm) ||
            !int.TryParse(datePart[4..6], out var dd))
            return false;

        if (mm < 1 || mm > 12) return false;

        int maxDays;
        if (mm == 2)
        {
            // February leap year check: YY divisible by 4 allows 29, otherwise 28
            maxDays = (yy % 4 == 0) ? 29 : 28;
        }
        else if (mm == 4 || mm == 6 || mm == 9 || mm == 11)
        {
            maxDays = 30;
        }
        else
        {
            maxDays = 31;
        }

        if (dd < 1 || dd > maxDays) return false;

        // PB — Place of Birth code: numeric in range 01–99
        if (!int.TryParse(pbPart, out var pb) || pb < 1 || pb > 99)
            return false;

        // Serial number (3 digits) + Gender indicator (1 digit) = 4 digits
        if (serialGenderPart.Length != 4 || !serialGenderPart.All(char.IsDigit))
            return false;

        return true;
    }

    public static bool BeValidPassport(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var clean = id.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[A-Za-z0-9]{6,12}$");
    }

    public static bool BeValidAccountNumber(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var clean = id.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(clean, @"^[A-Za-z0-9\-]{4,25}$");
    }

    public static bool BeValidMalaysiaPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        var clean = phone.Trim().Replace(" ", "").Replace("-", "");
        string digits;
        if (clean.StartsWith("+60")) digits = clean[3..];
        else if (clean.StartsWith("60")) digits = clean[2..];
        else digits = clean;

        return digits.Length == 10 && digits.All(char.IsDigit);
    }
}

/// <summary>
/// Sanity limits only. Whether a case field is REQUIRED, how long it may be and what it must match are the
/// administrator's decisions (field configuration), applied by the validation engine — not fixed here.
/// </summary>
public class CreateCaseDtoValidator : AbstractValidator<CreateCaseDto>
{
    public CreateCaseDtoValidator()
    {
        RuleFor(x => x.Title).MaximumLength(500);
        RuleFor(x => x.Description).MaximumLength(20000);
        RuleFor(x => x.Severity).MaximumLength(50);
    }
}

public class AddNoteDtoValidator : AbstractValidator<AddNoteDto>
{
    public AddNoteDtoValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(1000);
    }
}
