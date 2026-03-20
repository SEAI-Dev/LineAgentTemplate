namespace LineAgent.Api.Models.DTOs;

public class CreateChannelDto
{
    public string ChannelName { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelSecret { get; set; } = string.Empty;
    public string ChannelAccessToken { get; set; } = string.Empty;
    public string WebhookPath { get; set; } = string.Empty;
    public string? BranchNo { get; set; }
}

public class UpdateChannelDto
{
    public string? ChannelName { get; set; }
    public string? ChannelSecret { get; set; }
    public string? ChannelAccessToken { get; set; }
    public string? BranchNo { get; set; }
    public bool? IsActive { get; set; }
}

public class ChannelInfoDto
{
    public int Id { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string WebhookPath { get; set; } = string.Empty;
    public string? BranchNo { get; set; }
    public bool IsActive { get; set; }
    public int RegisteredUserCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
