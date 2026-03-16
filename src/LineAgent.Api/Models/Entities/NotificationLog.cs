namespace LineAgent.Api.Models.Entities;

public class NotificationLog
{
    public int Id { get; set; }
    public string NotificationType { get; set; } = string.Empty;
    public string? RecipientUserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public DateTime SentAt { get; set; }
}
