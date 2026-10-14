namespace CaseManagement.Api.Configuration;

/// <summary>
/// Tunables for the global header search, bound from the "Search" configuration section so
/// they can be changed per environment without a code change.
/// </summary>
public class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>Shortest query that is worth a database round trip.</summary>
    public int MinQueryLength { get; set; } = 2;

    /// <summary>Suggestions returned per result group when the caller does not ask for a size.</summary>
    public int DefaultResultLimit { get; set; } = 10;

    /// <summary>Upper bound a caller may request per result group.</summary>
    public int MaxResultLimit { get; set; } = 50;
}
