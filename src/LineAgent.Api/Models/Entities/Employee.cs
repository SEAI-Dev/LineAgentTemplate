namespace LineAgent.Api.Models.Entities;

public class Employee
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;            // SEOS UserId (e.g. se00375)
    public string FullNameInChinese { get; set; } = string.Empty;
    public string? Mobile { get; set; }
    public string? PasswordHash { get; set; }                      // SHA256 of SEOS Password
    public string? AssignBranchNo { get; set; }
    public string? JobTitle { get; set; }
    public int? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public bool IsAlive { get; set; } = true;
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
