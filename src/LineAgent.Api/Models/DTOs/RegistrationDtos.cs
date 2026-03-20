namespace LineAgent.Api.Models.DTOs;

public class RegistrationInfoDto
{
    public int Id { get; set; }
    public string LineUserId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string EmployeeUserId { get; set; } = string.Empty;
    public string? EmployeeName { get; set; }
    public string? BranchNo { get; set; }
    public string? ChannelName { get; set; }
    public int ChannelId { get; set; }
    public bool IsActive { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastInteractionAt { get; set; }
}

public class EmployeeInfoDto
{
    public string UserId { get; set; } = string.Empty;
    public string FullNameInChinese { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? AssignBranchNo { get; set; }
    public string? JobTitle { get; set; }
    public int? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public bool IsAlive { get; set; }
    public bool IsRegistered { get; set; }
    public DateTime? LastSyncAt { get; set; }
}
