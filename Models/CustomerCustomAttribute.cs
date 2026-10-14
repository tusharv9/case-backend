namespace CaseManagement.Api.Models;

public class CustomerCustomAttribute : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string FieldValue { get; set; } = string.Empty;

    public Customer? Customer { get; set; }
}
