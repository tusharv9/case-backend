namespace CaseManagement.Api.Models;

public class LookupType : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty; // e.g. PREFERRED_LANGUAGE, HOME_BRANCH, ID_TYPE
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Whether administrators may add values (false for lists the system can only support a fixed set of, e.g. ID types).</summary>
    public bool AllowAdd { get; set; } = true;

    /// <summary>True for lists whose options carry a value-format rule (ID types: what an ID number of that type must look like).</summary>
    public bool UsesFormatRules { get; set; } = false;

    public ICollection<LookupValue> Values { get; set; } = new List<LookupValue>();
}
