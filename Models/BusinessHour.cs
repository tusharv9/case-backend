namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(DayOfWeek), IsUnique = true)]
public class BusinessHour : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // 0 = Sunday, 1 = Monday, 2 = Tuesday, 3 = Wednesday, 4 = Thursday, 5 = Friday, 6 = Saturday
    public DayOfWeek DayOfWeek { get; set; }
    public string DayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
    public TimeSpan StartTime { get; set; } = new TimeSpan(9, 0, 0); // 09:00:00
    public TimeSpan EndTime { get; set; } = new TimeSpan(17, 0, 0);   // 17:00:00
}
