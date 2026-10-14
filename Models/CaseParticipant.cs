namespace CaseManagement.Api.Models;

public class CaseParticipant
{
    public Guid CaseId { get; set; }
    public Case Case { get; set; } = null!;
    
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    
    public ParticipantRole Role { get; set; }
}
