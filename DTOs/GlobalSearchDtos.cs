namespace CaseManagement.Api.DTOs;

/// <summary>
/// Result of the header "smart search". Deliberately narrow: it carries only the fields the
/// suggestion dropdown renders plus the id needed to navigate, so a keystroke costs a few
/// hundred bytes instead of the whole customer and case tables.
/// </summary>
public class GlobalSearchResultDto
{
    public List<SearchCustomerHitDto> Customers { get; set; } = new();
    public List<SearchCaseHitDto> Cases { get; set; } = new();
}

public class SearchCustomerHitDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string NRIC { get; set; } = string.Empty;
}

public class SearchCaseHitDto
{
    public Guid Id { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}
