using System.Text.Json;

namespace CaseManagement.Api.Services;

/// <summary>
/// A MultiSelect field's value: a JSON array of option values (["Email","Chat"]), so an option may itself contain commas.
/// Parsing is strict — anything that is not an array of strings is not a value — and duplicates and blanks are dropped.
/// </summary>
public static class MultiSelectValues
{
    public static List<string>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new List<string>();
        try
        {
            var items = JsonSerializer.Deserialize<List<string?>>(value);
            return items?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Format(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToList());
}
