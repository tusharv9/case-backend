namespace CaseManagement.Api.Models;

public class LinkedCase
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;
    
    public Guid TargetCaseId { get; set; }
    public Case TargetCase { get; set; } = null!;
}
