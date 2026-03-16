namespace LineAgent.Api.Models.DTOs;

public class CreateItemDto
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Priority { get; set; } = 2;
    public string? Category { get; set; }
    public DateTime? DueDate { get; set; }
}

public class UpdateItemDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Priority { get; set; }
    public string? Category { get; set; }
    public DateTime? DueDate { get; set; }
    public int? Status { get; set; }
}

public class ItemFilterDto
{
    public int? Status { get; set; }
    public int? Priority { get; set; }
    public string? Category { get; set; }
}

public class DailySummaryDto
{
    public DateTime Date { get; set; }
    public int TotalPending { get; set; }
    public int InProgress { get; set; }
    public int Overdue { get; set; }
    public List<ItemBriefDto> HighPriorityItems { get; set; } = new();
    public List<ItemBriefDto> DueTodayItems { get; set; } = new();
}

public class ItemBriefDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int Status { get; set; }
    public string? Category { get; set; }
    public DateTime? DueDate { get; set; }
}
