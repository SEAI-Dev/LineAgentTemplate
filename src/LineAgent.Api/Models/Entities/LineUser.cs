namespace LineAgent.Api.Models.Entities;

public class LineUser
{
    public int Id { get; set; }
    public string LineUserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public string NotifyTypes { get; set; } = "Daily,Weekly,Monthly";
    public DateTime CreatedAt { get; set; }
}
