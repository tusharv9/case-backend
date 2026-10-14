namespace CaseManagement.Api.DTOs;

public class CustomerSearchDto
{
    public string? IdType { get; set; }
    public string? IdValue { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTime? DateOfBirth { get; set; }
}
