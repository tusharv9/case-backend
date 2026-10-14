namespace CaseManagement.Api.Models;

public enum CaseStatus
{
    Open,
    InProgress,
    WaitingOnCustomer,
    Escalated,
    Resolved,
    Accepted,
    Rejected,
    Closed,
    Pending,
    Cancelled
}

// Case priority ("severity") is not an enum: it is administrator-configured master data (PrioritySlaRules),
// and Case.Severity stores the configured name.

public enum EventType
{
    Create,
    Assign,
    Note,
    Cowork,
    Transfer,
    Escalate,
    Resolve,
    Accept,
    Reject,
    Comment,
    View,
    Update,
    Close,
    Other
}

public enum ParticipantRole
{
    CoWorker,
    Watcher
}
