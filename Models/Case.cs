namespace CaseManagement.Api.Models;

using Microsoft.EntityFrameworkCore;

[Index(nameof(CaseNumber), IsUnique = true)]
public class Case : AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CaseNumber { get; set; } = string.Empty; // e.g., C-10472, S-00044, I-00045
    public string CaseType { get; set; } = "Complaint"; // Inquiry, Complaint, Service
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    public CaseStatus Status { get; set; }
    public string Severity { get; set; } = string.Empty; // Name of a configured priority (PrioritySlaRules); always set by case creation
    
    public string SourceChannel { get; set; } = "Voice";
    public string PreferredCommunicationChannel { get; set; } = "Phone";
    public string CommunicationChannel { get; set; } = "Voice";
    public string? Subcategory { get; set; }
    
    // SLA Tracking
    public DateTime SlaStartTime { get; set; }
    public int SlaTargetHours { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? SlaPausedAt { get; set; }
    /// <summary>Total time spent paused (Waiting on Customer), in BUSINESS minutes — the same unit the targets are in.</summary>
    public int SlaTotalPausedMinutes { get; set; } = 0;

    // Resolution SLA Targets (Snapshot)
    public int InternalResolutionTargetMinutes { get; set; }
    public int ExternalResolutionTargetMinutes { get; set; }
    public DateTime? InternalResolutionDueAt { get; set; }
    public DateTime? ExternalResolutionDueAt { get; set; }
    public int SlaConfigVersion { get; set; } = 1;
    
    // First Response SLA Tracking
    public int FirstResponseTargetMinutes { get; set; }
    public DateTime? FirstResponseDueAt { get; set; }
    public DateTime? FirstResponseActualAt { get; set; }
    public string FirstResponseStatus { get; set; } = "Pending"; // Met, Breached, Pending

    // Escalation Matrix Tracking
    public int EscalationLevel { get; set; } = 1; // 1 = Assigned Agent, 2 = Team Lead, 3 = CX Supervisor, 4 = Head of CX

    /// <summary>True once the "approaching SLA" reminder (the first escalation level's trigger) has been sent for this case.</summary>
    public bool SlaReminderSent { get; set; } = false;

    /// <summary>When the INTERNAL resolution target was first found breached (the clock escalation runs on).</summary>
    public DateTime? SlaBreachedAt { get; set; }

    /// <summary>
    /// How the case ended against its external target: "Met" or "Breached". Set once, when the case is resolved (null while open).
    /// Recorded so reports can COUNT outcomes in the database instead of re-running the SLA clock over every historical case.
    /// </summary>
    public string? SlaOutcome { get; set; }
    
    // Resolution Details
    public string? Disposition { get; set; }
    public string? ResolutionNote { get; set; }
    
    // Relationships
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    
    public ICollection<CaseEvent> Events { get; set; } = new List<CaseEvent>();
    public ICollection<CaseParticipant> Participants { get; set; } = new List<CaseParticipant>();
    public ICollection<CaseAttachment> Attachments { get; set; } = new List<CaseAttachment>();
    public ICollection<CaseCustomAttribute> CustomAttributes { get; set; } = new List<CaseCustomAttribute>();
    
    public ICollection<LinkedCase> LinkedCases { get; set; } = new List<LinkedCase>();
    public ICollection<LinkedCase> LinkedToCases { get; set; } = new List<LinkedCase>();
    
    // Sub-case relationships (L01, L02, R01, R02)
    public ICollection<CaseChildRelation> ChildRelations { get; set; } = new List<CaseChildRelation>();

    // Parent/Child Hierarchy
    public Guid? ParentCaseId { get; set; }
    public Case? ParentCase { get; set; }
    public ICollection<Case> Subcases { get; set; } = new List<Case>();

    public Guid? LinkedSourceCaseId { get; set; }
    public Case? LinkedSourceCase { get; set; }

    public string SubcaseType { get; set; } = "Original"; // Original, ReopenedSubcase, LinkedSubcase
}
