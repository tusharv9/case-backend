namespace CaseManagement.Api.Models;

public enum UserStatus
{
    Available,
    Busy,
    Away,
    /// <summary>Not working. Appended last: statuses are stored as integers, so existing values must not shift.</summary>
    Offline
}
