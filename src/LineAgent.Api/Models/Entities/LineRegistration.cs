namespace LineAgent.Api.Models.Entities;

public class LineRegistration
{
    public int Id { get; set; }
    public string LineUserId { get; set; } = string.Empty;         // LINE UID (Uxxxx)
    public string EmployeeUserId { get; set; } = string.Empty;     // FK → Employee.UserId
    public int ChannelId { get; set; }                              // FK → LineChannel.Id
    public string? DisplayName { get; set; }                        // LINE display name
    public bool IsActive { get; set; } = true;
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastInteractionAt { get; set; }
}
