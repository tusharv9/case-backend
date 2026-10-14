namespace CaseManagement.Api.DTOs;

/// <summary>
/// Server-side aggregated dashboard KPIs — replaces the full case download.
/// </summary>
public class DashboardSummaryDto
{
    public int TotalCases { get; set; }
    public int OpenCases { get; set; }
    public int InProgressCases { get; set; }
    public int WaitingOnCustomerCases { get; set; }
    public int EscalatedCases { get; set; }
    public int ResolvedCases { get; set; }
    public int UnassignedCases { get; set; }
    public int SlaBreachedCases { get; set; }
    public int SlaAtRiskCases { get; set; }
    public int SlaHealthyCases { get; set; }
    public decimal SlaAdherencePercent { get; set; }

    // Priority breakdown, in the configured display order (most urgent first). Priorities are
    // administrator-defined, so this is a list rather than one property per name.
    public List<SeverityCaseCount> CasesBySeverity { get; set; } = new();

    // Cases by department
    public List<DepartmentCaseCount> CasesByDepartment { get; set; } = new();

    // Cases by case type
    public List<CaseTypeCaseCount> CasesByType { get; set; } = new();

    // Resolved over time — last 7 days
    public List<ResolvedTimePoint> ResolvedDaily { get; set; } = new();
    // Resolved over time — last 4 weeks
    public List<ResolvedTimePoint> ResolvedWeekly { get; set; } = new();
    // Resolved over time — last 6 months
    public List<ResolvedTimePoint> ResolvedMonthly { get; set; } = new();

    // Recent cases needing attention (SLA breached/at-risk, high severity, unassigned)
    public List<AttentionCaseSummary> AttentionCases { get; set; } = new();

    // Recent cases overview (top 15 recent cases)
    public List<AttentionCaseSummary> RecentCases { get; set; } = new();

    // Recent activity
    public List<RecentActivityItem> RecentActivities { get; set; } = new();
}

public class DepartmentCaseCount
{
    public string DepartmentName { get; set; } = string.Empty;
    public Guid DepartmentId { get; set; }
    public int Count { get; set; }
}

public class CaseTypeCaseCount
{
    public string CaseType { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class ResolvedTimePoint
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class AttentionCaseSummary
{
    public Guid Id { get; set; }
    public string CaseNumber { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
    public string DepartmentName { get; set; } = string.Empty;
    public DateTime SlaStartTime { get; set; }
    public int SlaTargetHours { get; set; }
    public DateTime? SlaBreachedAt { get; set; }
    public DateTime? SlaPausedAt { get; set; }
    public int SlaTotalPausedMinutes { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>The SLA clock's verdict for this case, so the dashboard reads exactly like the board.</summary>
    public CaseSlaDto? Sla { get; set; }

    // The raw inputs the front end may want next to the verdict.
    public bool IsHolidayToday { get; set; }
    public string? HolidayName { get; set; }
}

public class RecentActivityItem
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // resolved, created, escalated
    public string Title { get; set; } = string.Empty;
    public string Sub { get; set; } = string.Empty;
    public Guid CaseId { get; set; }
    public DateTime Timestamp { get; set; }
}

public class SeverityCaseCount
{
    public string Severity { get; set; } = string.Empty;
    public int Count { get; set; }
    public int DisplayOrder { get; set; }
}

/// <summary>Everything the dashboard's filter bar offers, in one response (it used to take seven requests).</summary>
public class DashboardFiltersDto
{
    public List<NamedOptionDto> Departments { get; set; } = new();
    public List<FilterOptionDto> CaseTypes { get; set; } = new();
    public List<FilterOptionDto> Statuses { get; set; } = new();
    public List<string> Priorities { get; set; } = new();
    public List<FilterOptionDto> SlaStatuses { get; set; } = new();
    public List<FilterOptionDto> DateRanges { get; set; } = new();
    public List<FilterOptionDto> QuickActions { get; set; } = new();
}

public class FilterOptionDto
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
