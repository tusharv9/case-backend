namespace CaseManagement.Api.Services;

using System.Text;

/// <summary>
/// RFC 4180 CSV for report exports: every field that holds a comma, quote or line break is quoted (quotes doubled), and a
/// value a spreadsheet would run as a formula (starting =, +, -, @, tab or carriage return) is prefixed with an apostrophe
/// so an exported case title can never execute in Excel.
/// </summary>
public static class ReportCsv
{
    public static string Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        AppendLine(sb, header);
        foreach (var row in rows) AppendLine(sb, row);
        return sb.ToString();
    }

    /// <summary>The bytes to send: UTF-8 with a byte-order mark, so Excel reads non-ASCII names correctly.</summary>
    public static byte[] ToBytes(string csv) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();

    public static string Field(string? value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static void AppendLine(StringBuilder sb, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Field(fields[i]));
        }
        sb.Append("\r\n");
    }
}
