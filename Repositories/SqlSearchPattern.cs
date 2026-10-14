namespace CaseManagement.Api.Repositories;

/// <summary>
/// Builds LIKE/ILIKE patterns from user input. Wildcards typed by the user are escaped so a
/// query of "%" matches a literal percent sign rather than every row in the table.
/// </summary>
internal static class SqlSearchPattern
{
    private const char EscapeChar = '\\';

    /// <summary>Pattern matching any value that contains <paramref name="value"/>.</summary>
    public static string Contains(string value) => $"%{Escape(value)}%";

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (ch == '%' || ch == '_' || ch == EscapeChar) builder.Append(EscapeChar);
            builder.Append(ch);
        }
        return builder.ToString();
    }
}
