namespace CaseManagement.Api.DTOs;

/// <summary>
/// Page sizes for every list: 10 by default, and at most <see cref="MaxPageSize"/> — what a list's "View all" asks for. Beyond it
/// the list says how many more there are, so no request pulls a whole table.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 1000;
}
