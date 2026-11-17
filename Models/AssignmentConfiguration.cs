namespace CaseManagement.Api.Models;

public class AssignmentConfiguration : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    // If DepartmentId is null, this is the global fallback assignment configuration
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }
    
    // "RoundRobin", "LeastOccupancy", "Manual"
    public string Algorithm { get; set; } = "RoundRobin";

    /// <summary>
    /// <see cref="AssignmentCaseSources"/>: which cases the row is for. <c>Default</c> rows are the global default and the teams'
    /// own settings; the one global <c>Email</c> row (never per team) is how email cases are given out, overriding the algorithm only.
    /// </summary>
    public string CaseSource { get; set; } = AssignmentCaseSources.Default;
    
    public int MaxConcurrentCapacity { get; set; } = 5;
    public bool IsActive { get; set; } = true;
}

public static class AssignmentCaseSources
{
    public const string Default = "Default";
    /// <summary>Cases whose source is Email — every case made from an email ticket.</summary>
    public const string Email = "Email";
}

public static class AssignmentAlgorithms
{
    public const string RoundRobin = "RoundRobin";
    public const string LeastOccupancy = "LeastOccupancy";
    /// <summary>No automatic choice: a person picks the agent (on ticket conversion, the converter must), otherwise the team lead holds the case.</summary>
    public const string Manual = "Manual";
    public static readonly string[] All = [RoundRobin, LeastOccupancy, Manual];
}
