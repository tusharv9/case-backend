namespace CaseManagement.Api.Services.Customer360;

/// <summary>
/// Finding Customer 360 cases by the identifier someone types into a search box.
/// </summary>
/// <remarks>
/// A Customer 360 case stores its customer's identifier encrypted, so it cannot be matched with LIKE. Instead the
/// typed value is hashed the way it would have been stored under every identifier type a case can keep, and cases
/// are matched on the indexed hash — an exact match on a whole NRIC / BRN / passport number, never a partial one,
/// and without decrypting anything.
/// </remarks>
public static class CustomerIdentifierSearch
{
    // Every lookup type Customer360CustomerService can store (it normalises the value the same way per type).
    private static readonly string[] IdentifierTypes = ["BRN", "OLDBRN", "SecondaryID:PASSPORT", "SecondaryID:OLDID", "SecondaryID:POLICENUMBER"];

    /// <summary>The hashes to match, or none when the term cannot be a whole identifier or protection is not configured.</summary>
    public static IReadOnlyList<string> Hashes(ICustomerIdentifierProtector? protector, string? term)
    {
        if (protector is not { IsConfigured: true } || string.IsNullOrWhiteSpace(term)) return [];
        var trimmed = term.Trim();
        if (trimmed.Length is < 6 or > 40 || !trimmed.Any(char.IsDigit)) return [];

        var hashes = new List<string>();
        if (Customer360CustomerService.IsUsableNric(trimmed))
            hashes.Add(protector.Hash("NRIC", Customer360CustomerService.NormalizeNric(trimmed)));
        var identifier = Customer360CustomerService.NormalizeIdentifier(trimmed);
        foreach (var type in IdentifierTypes) hashes.Add(protector.Hash(type, identifier));
        return hashes;
    }
}
