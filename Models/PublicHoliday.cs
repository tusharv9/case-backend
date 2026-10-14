namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(HolidayDate), IsUnique = true)]
public class PublicHoliday : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime HolidayDate { get; set; } // Stored as UTC date (e.g. 2026-12-25 00:00:00Z)
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
