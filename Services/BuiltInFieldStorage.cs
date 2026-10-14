namespace CaseManagement.Api.Services;

using CaseManagement.Api.Data;
using CaseManagement.Api.Models;

/// <summary>
/// Where the value of a field lives, which decides what its TYPE may become. A built-in Customer 360 field is a fixed column of
/// the customer record (the type is then only how the value is entered and validated, not how it is stored); a custom field is
/// text in the attributes table and can take any type its existing values fit. This describes storage, not business rules.
/// </summary>
public static class BuiltInFieldStorage
{
    private static readonly string[] TextLike = { "Text", "Email", "Number", "Dropdown" };

    private sealed record Binding(string[] AllowedTypes, Func<AppDbContext, IQueryable<string?>>? Values);

    private static readonly Dictionary<string, Binding> Customer360 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fullName"]          = new(TextLike, c => c.Customers.Select(x => (string?)x.FullName)),
        ["idType"]            = new(TextLike, c => c.Customers.Select(x => (string?)x.IdType)),
        ["idValue"]           = new(TextLike, c => c.Customers.Select(x => x.NRIC ?? x.Passport ?? x.AccountNumber)),
        ["email"]             = new(TextLike, c => c.Customers.Select(x => x.Email)),
        ["preferredLanguage"] = new(TextLike, c => c.Customers.Select(x => (string?)x.PreferredLanguage)),
        ["branch"]            = new(TextLike, c => c.Customers.Select(x => x.Branch)),
        ["customerSegment"]   = new(TextLike, c => c.Customers.Select(x => x.CustomerSegment)),
        ["phoneNumber"]       = new(new[] { "Phone", "Text" }, c => c.Customers.Select(x => (string?)x.PhoneNumber)),
        ["dateOfBirth"]       = new(new[] { "Date" }, null),   // a real date column: only a date fits
    };

    /// <summary>The types the field may become (null = unrestricted, i.e. a custom field).</summary>
    public static string[]? AllowedTypes(FieldConfiguration f)
    {
        if (f.IsCustomField) return null;
        if (f.ModuleKey == "Customer360" && Customer360.TryGetValue(f.ApiField, out var b))
            return b.AllowedTypes.Contains(f.FieldType, StringComparer.OrdinalIgnoreCase) ? b.AllowedTypes : b.AllowedTypes.Append(f.FieldType).ToArray();
        return new[] { f.FieldType };   // any other built-in field is tied to what the application does with it
    }

    /// <summary>The distinct stored values of the field, to check against a new type. Null when nothing is stored to check.</summary>
    public static IQueryable<string?>? StoredValues(AppDbContext db, FieldConfiguration f)
    {
        if (f.IsCustomField)
        {
            return f.ModuleKey == "Customer360"
                ? db.CustomerCustomAttributes.Where(a => a.FieldKey == f.ApiField).Select(a => (string?)a.FieldValue)
                : f.ModuleKey == "CaseManagement"
                    ? db.CaseCustomAttributes.Where(a => a.FieldKey == f.ApiField).Select(a => (string?)a.FieldValue)
                    : null;
        }
        return f.ModuleKey == "Customer360" && Customer360.TryGetValue(f.ApiField, out var b) ? b.Values?.Invoke(db) : null;
    }

    public static string RecordsName(FieldConfiguration f) => f.ModuleKey == "Customer360" ? "customer" : "case";
}
