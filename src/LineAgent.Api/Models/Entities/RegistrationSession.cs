namespace LineAgent.Api.Models.Entities;

/// <summary>
/// In-memory registration session, tracks multi-step conversation state.
/// </summary>
public class RegistrationSession
{
    public string LineUserId { get; set; } = string.Empty;
    public int ChannelId { get; set; }
    public RegistrationState State { get; set; } = RegistrationState.AwaitingPhone;
    public string? Phone { get; set; }
    public int FailCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LockedUntil { get; set; }
}

public enum RegistrationState
{
    AwaitingPhone,
    AwaitingPassword
}
