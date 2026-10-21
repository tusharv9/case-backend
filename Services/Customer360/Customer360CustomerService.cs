namespace CaseManagement.Api.Services.Customer360;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaseManagement.Api.DTOs;
using CaseManagement.Api.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>An existing customer chosen for a new case, confirmed with Customer 360 — what the case may store.</summary>
public sealed record ResolvedCustomer360(
    string CustomerType,
    string ExternalCustomerId,
    string LookupType,
    string LookupKey,
    string? CustomerSegment,
    string FullName);

/// <summary>Existing customers, read from Customer 360 — the source of truth — and never copied into Case Management.</summary>
public interface ICustomer360CustomerService
{
    Task<IReadOnlyList<Customer360SearchOptionDto>> GetSearchOptionsAsync(CancellationToken ct);
    Task<Customer360SearchResultDto> SearchAsync(string? customerType, string? searchBy, string? value, CancellationToken ct);
    Task<ResolvedCustomer360> ResolveForNewCaseAsync(Customer360SelectionDto selection, CancellationToken ct);
    Task<CaseCustomer360Dto> GetForCaseAsync(Case caseItem, CancellationToken ct);

    /// <summary>
    /// Every customer Customer 360 has now for one stored identifier — for naming customers in a case list. One
    /// profile lookup (no contact details) and NOT masked: the caller masks, so several of these may run at once.
    /// Throws <see cref="Customer360Exception"/> when Customer 360 cannot answer.
    /// </summary>
    Task<IReadOnlyList<Customer360CustomerDto>> FindForListAsync(string customerType, string lookupType, string lookupKey, CancellationToken ct);

    /// <summary>What "Open Customer 360" needs for a case's customer: the stored identifier, decrypted. No Customer 360 call.</summary>
    Customer360HandoffTarget HandoffForCase(Case caseItem);

    /// <summary>The same for a customer just chosen on Create Case (the search the person ran). No Customer 360 call.</summary>
    Task<Customer360HandoffTarget> HandoffForSelectionAsync(Customer360SelectionDto selection, CancellationToken ct);
}

/// <summary>A search value that cannot be used — refused before Customer 360 is called.</summary>
public sealed class Customer360SearchException(string message) : Exception(message);

/// <summary>
/// The Customer 360 integration Lead Management uses (leadmbackend's CustomerLookupService), for cases.
/// </summary>
/// <remarks>
/// <para><b>Same contract as Lead Management:</b> the person's own token, <c>v1/indprofile</c> then
/// <c>v1/contactinfo</c> keyed on the NRIC the profile returned, values validated before any call (a typo must
/// not become an audited lookup against a real person), contact details optional. <b>Beyond it:</b>
/// Non-Individual customers (<c>v1/corpprofile</c> by BRN / Old BRN), and every match is returned so the
/// person picks the right one — Lead Management silently takes the first.</para>
///
/// <para><b>Search types come from Customer 360</b> (<c>v1/lookups</c>), limited to identifiers: NRIC and the
/// secondary IDs for individuals, BRN and Old BRN for companies. Name and phone searches are not identifiers
/// and are not offered.</para>
///
/// <para><b>What a case keeps</b> (see <see cref="ResolveForNewCaseAsync"/>): the CRM key and the identifier
/// that finds the customer again — Customer 360 cannot look a customer up by its CRM key, only by a search.
/// Preferably the NRIC / BRN the profile carries, otherwise the identifier the person searched by.</para>
/// </remarks>
public sealed class Customer360CustomerService(
    Customer360Client client,
    ICustomerIdentifierProtector protector,
    IPiiMaskingService masking,
    IMemoryCache cache,
    IOptions<Customer360Options> options) : ICustomer360CustomerService
{
    private const int MaxMatches = 20;
    private const int MaxContactReads = 10;

    // Customer 360's wire values for the searches offered (Customer360.tsx / CRM vocabulary).
    private const string Nric = "NRIC";
    private const string SecondaryPrefix = "SecondaryID:";
    private static readonly string[] CorporateIdentifiers = ["BRN", "OLDBRN"];

    private static readonly Regex IdentifierCharacters = new(@"^[A-Za-z0-9\-/. ()]+$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<Customer360SearchOptionDto>> GetSearchOptionsAsync(CancellationToken ct)
    {
        const string cacheKey = "customer360:search-options";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<Customer360SearchOptionDto>? cached) && cached != null) return cached;

        using var doc = await client.GetAsync("v1/lookups", ct)
            ?? throw new Customer360Exception(Customer360Failure.Unavailable, "Customer 360 did not return its search types.");
        var data = Property(doc.RootElement, "data");
        var result = new List<Customer360SearchOptionDto>();

        foreach (var (value, label) in Options(data, "idTypes"))
            if (string.Equals(value, Nric, StringComparison.OrdinalIgnoreCase))
                result.Add(new() { Key = Nric, Label = label, CustomerType = CustomerTypes.Individual });
        foreach (var (value, label) in Options(data, "secondaryIdTypes"))
            result.Add(new() { Key = SecondaryPrefix + value.ToUpperInvariant(), Label = label, CustomerType = CustomerTypes.Individual });
        foreach (var (value, label) in Options(data, "corpSearchTypes"))
            if (CorporateIdentifiers.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(new() { Key = value.ToUpperInvariant(), Label = label, CustomerType = CustomerTypes.NonIndividual });

        cache.Set(cacheKey, (IReadOnlyList<Customer360SearchOptionDto>)result, TimeSpan.FromSeconds(Math.Max(0, options.Value.LookupsCacheSeconds)));
        return result;
    }

    public async Task<Customer360SearchResultDto> SearchAsync(string? customerType, string? searchBy, string? value, CancellationToken ct)
    {
        var (option, normalized) = await NormalizeAsync(customerType, searchBy, value, ct);
        var matches = await FindAsync(option.CustomerType, option.Key, normalized, option.Label, ct);

        var result = new Customer360SearchResultDto
        {
            Matches = matches.Take(MaxMatches).Select(m => m.Match).ToList(),
            Truncated = matches.Count > MaxMatches,
        };
        foreach (var match in result.Matches) await masking.MaskAsync(match, ct);
        return result;
    }

    public async Task<ResolvedCustomer360> ResolveForNewCaseAsync(Customer360SelectionDto selection, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(selection.ExternalCustomerId))
            throw new Customer360SearchException("Choose the customer from the Customer 360 search results.");

        var (option, normalized) = await NormalizeAsync(selection.CustomerType, selection.SearchBy, selection.Value, ct);
        var matches = await FindAsync(option.CustomerType, option.Key, normalized, option.Label, ct);
        var chosen = matches.FirstOrDefault(m => string.Equals(m.Match.ExternalCustomerId, selection.ExternalCustomerId.Trim(), StringComparison.Ordinal))
            ?? throw new Customer360SearchException("That customer was not found in Customer 360 any more. Search again and choose the customer.");

        // Keep the strongest identifier the profile carries, so the case finds the customer again however they were found.
        var (lookupType, lookupKey) = option.CustomerType == CustomerTypes.Individual
            ? (IsUsableNric(chosen.NationalId) ? (Nric, NormalizeNric(chosen.NationalId)) : (option.Key, normalized))
            : (!string.IsNullOrWhiteSpace(chosen.Brn) ? ("BRN", NormalizeIdentifier(chosen.Brn)) : (option.Key, normalized));

        return new ResolvedCustomer360(option.CustomerType, chosen.Match.ExternalCustomerId, lookupType, lookupKey,
            chosen.Match.CustomerSegment, chosen.Match.FullName);
    }

    public async Task<CaseCustomer360Dto> GetForCaseAsync(Case caseItem, CancellationToken ct)
    {
        var result = new CaseCustomer360Dto { CustomerType = caseItem.CustomerType ?? string.Empty };
        if (caseItem.CustomerSource != CustomerSources.Customer360
            || caseItem.CustomerLookupType == null || caseItem.ExternalCustomerId == null || caseItem.CustomerType == null)
        {
            result.Status = "NotFound";
            result.Message = "This case is not linked to a Customer 360 customer.";
            return result;
        }

        string lookupKey;
        try { lookupKey = protector.Unprotect(caseItem.CustomerLookupKeyEncrypted!); }
        catch (Exception)
        {
            result.Status = "Unavailable";
            result.Message = "The customer's identifier on this case could not be read. Contact your administrator.";
            return result;
        }

        try
        {
            var label = (await GetSearchOptionsAsync(ct)).FirstOrDefault(o => o.Key == caseItem.CustomerLookupType)?.Label ?? caseItem.CustomerLookupType;
            var matches = await FindAsync(caseItem.CustomerType, caseItem.CustomerLookupType, lookupKey, label, ct);
            var match = matches.FirstOrDefault(m => string.Equals(m.Match.ExternalCustomerId, caseItem.ExternalCustomerId, StringComparison.Ordinal));
            if (match == null)
            {
                result.Status = "NotFound";
                result.Message = "This customer is no longer found in Customer 360.";
                return result;
            }
            await masking.MaskAsync(match.Match, ct);
            result.Status = "Available";
            result.Customer = match.Match;
            return result;
        }
        catch (Customer360Exception ex)
        {
            result.Status = ex.Failure == Customer360Failure.NotPermitted ? "NotPermitted" : "Unavailable";
            result.Message = ex.Message;
            return result;
        }
    }

    public async Task<IReadOnlyList<Customer360CustomerDto>> FindForListAsync(string customerType, string lookupType, string lookupKey, CancellationToken ct)
    {
        var label = (await GetSearchOptionsAsync(ct)).FirstOrDefault(o => o.Key == lookupType)?.Label ?? lookupType;
        var matches = await FindAsync(customerType, lookupType, lookupKey, label, ct, withContact: false);
        return matches.Select(m => m.Match).ToList();
    }

    public Customer360HandoffTarget HandoffForCase(Case caseItem)
    {
        if (caseItem.CustomerSource != CustomerSources.Customer360 || caseItem.CustomerType == null
            || caseItem.CustomerLookupType == null || caseItem.CustomerLookupKeyEncrypted == null || caseItem.ExternalCustomerId == null)
            throw new Customer360SearchException("This case is not linked to a Customer 360 customer.");
        return Handoff(caseItem.CustomerType, caseItem.CustomerLookupType, protector.Unprotect(caseItem.CustomerLookupKeyEncrypted), caseItem.ExternalCustomerId);
    }

    public async Task<Customer360HandoffTarget> HandoffForSelectionAsync(Customer360SelectionDto selection, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(selection.ExternalCustomerId))
            throw new Customer360SearchException("Choose the customer from the Customer 360 search results.");
        var (option, normalized) = await NormalizeAsync(selection.CustomerType, selection.SearchBy, selection.Value, ct);
        return Handoff(option.CustomerType, option.Key, normalized, selection.ExternalCustomerId.Trim());
    }

    /// <summary>Case Management's lookup type in Customer 360's own search vocabulary ("SecondaryID:PASSPORT" → SECONDARYID + PASSPORT).</summary>
    private static Customer360HandoffTarget Handoff(string customerType, string lookupType, string value, string externalCustomerId) =>
        lookupType.StartsWith(SecondaryPrefix, StringComparison.Ordinal)
            ? new(customerType, "SECONDARYID", lookupType[SecondaryPrefix.Length..], value, externalCustomerId)
            : new(customerType, lookupType.ToUpperInvariant(), null, value, externalCustomerId);

    // ------------------------------------------------------------------------------------------------ searching

    private sealed record Found(Customer360CustomerDto Match, string? NationalId, string? Brn);

    private async Task<(Customer360SearchOptionDto Option, string Normalized)> NormalizeAsync(
        string? customerType, string? searchBy, string? value, CancellationToken ct)
    {
        var type = string.Equals(customerType, CustomerTypes.NonIndividual, StringComparison.OrdinalIgnoreCase) ? CustomerTypes.NonIndividual
            : string.Equals(customerType, CustomerTypes.Individual, StringComparison.OrdinalIgnoreCase) ? CustomerTypes.Individual
            : throw new Customer360SearchException("Choose Individual or Non-Individual.");

        var option = (await GetSearchOptionsAsync(ct)).FirstOrDefault(o =>
                o.CustomerType == type && string.Equals(o.Key, searchBy?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new Customer360SearchException($"Choose how to search for the {(type == CustomerTypes.Individual ? "individual" : "company")}.");

        if (string.IsNullOrWhiteSpace(value)) throw new Customer360SearchException($"Enter the {option.Label}.");
        if (!IdentifierCharacters.IsMatch(value.Trim()))
            throw new Customer360SearchException($"{option.Label} can contain only letters, digits, spaces and - / . ( ).");

        if (option.Key == Nric)
        {
            var nric = NormalizeNric(value);
            if (!IsUsableNric(nric)) throw new Customer360SearchException("Enter a valid NRIC (6 to 20 letters and digits, including at least one digit).");
            return (option, nric);
        }

        var normalized = NormalizeIdentifier(value);
        if (normalized.Length is < 3 or > 40) throw new Customer360SearchException($"{option.Label} must be 3 to 40 characters.");
        return (option, normalized);
    }

    private async Task<List<Found>> FindAsync(string customerType, string searchKey, string value, string label, CancellationToken ct, bool withContact = true)
    {
        var escaped = Uri.EscapeDataString(value);
        string path = customerType == CustomerTypes.Individual
            ? searchKey.StartsWith(SecondaryPrefix, StringComparison.Ordinal)
                ? $"v1/indprofile?type=SECONDARYID&subtype={Uri.EscapeDataString(searchKey[SecondaryPrefix.Length..])}&id={escaped}"
                : $"v1/indprofile?type={Uri.EscapeDataString(searchKey)}&id={escaped}"
            : $"v1/corpprofile?type={Uri.EscapeDataString(searchKey)}&id={escaped}";

        using var doc = await client.GetAsync(path, ct);
        if (doc == null) return new();
        var data = Property(doc.RootElement, "data");
        if (data is not { ValueKind: JsonValueKind.Array } array) return new();

        var found = new List<Found>();
        foreach (var profile in array.EnumerateArray())
        {
            if (profile.ValueKind != JsonValueKind.Object) continue;
            var item = customerType == CustomerTypes.Individual
                ? MapIndividual(profile, searchKey, value, label)
                : MapCorporate(profile, searchKey, value, label);
            if (item != null) found.Add(item);
        }

        // Contact details are a second, optional call per customer (Customer 360 gates them separately).
        if (withContact)
            foreach (var item in found.Take(MaxContactReads))
                await AddContactAsync(item, customerType, ct);
        return found;
    }

    private static Found? MapIndividual(JsonElement p, string searchKey, string value, string label)
    {
        var externalId = Text(p, "phprId");
        if (string.IsNullOrWhiteSpace(externalId)) return null;   // cannot be told apart from another match, so cannot be linked
        var nationalId = Text(p, "nationalId");
        var idValue = searchKey switch
        {
            Nric => nationalId ?? value,
            "SecondaryID:PASSPORT" => Text(p, "passport") ?? value,
            "SecondaryID:OLDID" => Text(p, "oldId") ?? value,
            "SecondaryID:POLICENUMBER" => Text(p, "policeNumber") ?? value,
            _ => value,
        };
        return new Found(new Customer360CustomerDto
        {
            CustomerType = CustomerTypes.Individual,
            ExternalCustomerId = externalId,
            FullName = CollapseWhitespace(Text(p, "fullName")) ?? string.Empty,
            Salutation = Text(p, "salutation"),
            IdType = label,
            IdValue = idValue,
            DateOfBirth = ParseDate(Text(p, "birthDate")),
            Branch = Text(p, "branch"),
            CustomerSegment = Text(p, "segmentation"),
            PreferredLanguage = Text(p, "languagePreferred"),
            PreferredCommunicationChannel = Text(p, "preferComChnl"),
        }, nationalId, null);
    }

    private static Found? MapCorporate(JsonElement p, string searchKey, string value, string label)
    {
        var externalId = Text(p, "customerId") ?? Text(p, "cifNumber");
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        var brn = Text(p, "brn");
        return new Found(new Customer360CustomerDto
        {
            CustomerType = CustomerTypes.NonIndividual,
            ExternalCustomerId = externalId,
            FullName = CollapseWhitespace(Text(p, "organizationName")) ?? string.Empty,
            Salutation = Text(p, "salutation"),
            IdType = label,
            IdValue = searchKey == "BRN" ? brn ?? value : value,
            RegistrationDate = ParseDate(Text(p, "businessRegDate")),
            CustomerSegment = Text(p, "segmentation"),
        }, null, brn);
    }

    private async Task AddContactAsync(Found item, string customerType, CancellationToken ct)
    {
        var path = customerType == CustomerTypes.Individual
            ? IsUsableNric(item.NationalId) ? $"v1/contactinfo?id={Uri.EscapeDataString(NormalizeNric(item.NationalId))}" : null
            : !string.IsNullOrWhiteSpace(item.Brn) ? $"v1/contactinfo?type=CORPORATE&id={Uri.EscapeDataString(item.Brn!.Trim())}" : null;
        if (path == null) return;

        try
        {
            using var doc = await client.GetAsync(path, ct);
            if (doc == null) return;
            var contact = Property(doc.RootElement, "data");
            if (contact is not { ValueKind: JsonValueKind.Object } c) return;
            item.Match.PhoneNumber = Text(c, "contactNumber");
            item.Match.Email = (Text(c, "padrEmail1") ?? Text(c, "padrEmail2"))?.ToLowerInvariant();
            item.Match.ContactDetailsAvailable = true;
        }
        catch (Customer360Exception)
        {
            // Optional, as in Lead Management: the profile still identifies the customer; the card says contact details are unavailable.
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private static IEnumerable<(string Value, string Label)> Options(JsonElement? data, string name)
    {
        if (data is not { ValueKind: JsonValueKind.Object } d || Property(d, name) is not { ValueKind: JsonValueKind.Array } list) yield break;
        foreach (var o in list.EnumerateArray())
        {
            var value = Text(o, "value");
            if (string.IsNullOrWhiteSpace(value)) continue;
            yield return (value.Trim(), Text(o, "label") ?? value.Trim());
        }
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in element.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Text(JsonElement element, string name)
    {
        var value = Property(element, name);
        var text = value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? CollapseWhitespace(string? value) =>
        value == null ? null : Regex.Replace(value, @"\s+", " ").Trim();

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK", "dd/MM/yyyy", "d/M/yyyy", "yyyyMMdd", "dd-MM-yyyy"];

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : null;
    }

    /// <summary>Letters and digits, upper-case — the same normalisation as Lead Management's NationalId.</summary>
    internal static string NormalizeNric(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value) if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    internal static bool IsUsableNric(string? value)
    {
        var n = NormalizeNric(value);
        return n.Length is >= 6 and <= 20 && n.Any(char.IsDigit);
    }

    internal static string NormalizeIdentifier(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
}
