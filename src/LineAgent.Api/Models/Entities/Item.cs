namespace LineAgent.Api.Models.Entities;

public class Item
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Priority { get; set; } = 2;           // 1=High, 2=Medium, 3=Low
    public int Status { get; set; } = 0;              // 0=Pending, 1=InProgress, 2=Done, 3=Cancelled
    public string? Category { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class ItemStatus
{
    public const int Pending = 0;
    public const int InProgress = 1;
    public const int Done = 2;
    public const int Cancelled = 3;

    public static string ToLabel(int status) => status switch
    {
        0 => "Pending", 1 => "InProgress", 2 => "Done", 3 => "Cancelled", _ => "Unknown"
    };
}
