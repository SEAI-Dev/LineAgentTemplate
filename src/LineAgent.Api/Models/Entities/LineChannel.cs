namespace LineAgent.Api.Models.Entities;

public class LineChannel
{
    public int Id { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelSecret { get; set; } = string.Empty;
    public string ChannelAccessToken { get; set; } = string.Empty;
    public string WebhookPath { get; set; } = string.Empty;       // unique path segment, e.g. "se01"
    public string? BranchNo { get; set; }                          // optional link to SaintEir branch
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
