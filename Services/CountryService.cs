namespace CaseManagement.Api.Services;

using System.Text.RegularExpressions;
using CaseManagement.Api.Data;
using CaseManagement.Api.DTOs;
using Microsoft.EntityFrameworkCore;

public sealed record PhoneNumberResult(string Iso2, string Normalized, string? Error);

public interface ICountryService
{
    Task<IReadOnlyList<CountryDto>> GetActiveAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks a phone number against its country's rules (data in the Countries table, not code) and returns the stored
    /// form "+&lt;dial&gt; &lt;national digits&gt;". <paramref name="countryIso2"/> blank = the default country (existing data is all Malaysian).
    /// </summary>
    Task<PhoneNumberResult> NormalizePhoneAsync(string? countryIso2, string? phone, string label, CancellationToken ct = default);
}

public class CountryService : ICountryService
{
    public const string DefaultIso2 = "MY";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly AppDbContext _context;
    private readonly IConfigCache _cache;

    public CountryService(AppDbContext context, IConfigCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public Task<IReadOnlyList<CountryDto>> GetActiveAsync(CancellationToken ct = default) =>
        _cache.GetOrCreateAsync<IReadOnlyList<CountryDto>>("countries:active", async () =>
            await _context.Countries.AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.Name)
                .Select(c => new CountryDto
                {
                    Iso2 = c.Iso2, Iso3 = c.Iso3, Name = c.Name, DialCode = c.DialCode,
                    MinNationalDigits = c.MinNationalDigits, MaxNationalDigits = c.MaxNationalDigits, NationalPattern = c.NationalPattern
                })
                .ToListAsync(ct));

    public async Task<PhoneNumberResult> NormalizePhoneAsync(string? countryIso2, string? phone, string label, CancellationToken ct = default)
    {
        var iso2 = string.IsNullOrWhiteSpace(countryIso2) ? DefaultIso2 : countryIso2.Trim().ToUpperInvariant();
        var countries = await GetActiveAsync(ct);
        var country = countries.FirstOrDefault(c => c.Iso2 == iso2);
        if (country == null) return new PhoneNumberResult(iso2, string.Empty, $"'{iso2}' is not a supported country for {label}.");

        var raw = phone?.Trim() ?? string.Empty;
        if (raw.Any(ch => !(char.IsDigit(ch) || " +-()".Contains(ch))))
            return new PhoneNumberResult(iso2, string.Empty, $"{label} must be a valid phone number.");

        var digits = new string(raw.Where(char.IsDigit).ToArray());
        var dial = country.DialCode;
        bool InRange(int n) => n >= country.MinNationalDigits && n <= country.MaxNationalDigits;

        if (raw.StartsWith('+'))
        {
            if (!digits.StartsWith(dial))
                return new PhoneNumberResult(iso2, string.Empty, $"{label} must start with +{dial} for {country.Name}.");
            digits = digits[dial.Length..];
        }
        else if (digits.StartsWith(dial) && !InRange(digits.Length) && InRange(digits.Length - dial.Length))
        {
            digits = digits[dial.Length..];   // "60123456789" typed without the plus
        }

        if (digits.StartsWith('0') && !InRange(digits.Length) && InRange(digits.Length - 1))
            digits = digits[1..];             // national trunk prefix ("012-345 6789")

        if (!InRange(digits.Length))
        {
            var span = country.MinNationalDigits == country.MaxNationalDigits
                ? $"{country.MinNationalDigits} digits"
                : $"{country.MinNationalDigits} to {country.MaxNationalDigits} digits";
            return new PhoneNumberResult(iso2, string.Empty, $"{label} must have {span} after the +{dial} {country.Name} country code (currently {digits.Length}).");
        }

        if (!string.IsNullOrWhiteSpace(country.NationalPattern))
        {
            try
            {
                if (!Regex.IsMatch(digits, country.NationalPattern, RegexOptions.None, RegexTimeout))
                    return new PhoneNumberResult(iso2, string.Empty, $"{label} is not a valid {country.Name} phone number.");
            }
            catch (ArgumentException) { /* an unusable pattern must not lock everyone out; the length rule still applies */ }
            catch (RegexMatchTimeoutException) { return new PhoneNumberResult(iso2, string.Empty, $"{label} could not be validated."); }
        }

        return new PhoneNumberResult(iso2, $"+{dial} {digits}", null);
    }
}
