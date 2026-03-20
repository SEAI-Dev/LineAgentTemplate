using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Models.Entities;

namespace LineAgent.Api.Services;

public interface IEmployeeSyncService
{
    Task<int> SyncFromSeosApiAsync();
    Task<List<EmployeeInfoDto>> GetAllAsync(string? branchNo = null, int? departmentId = null);
    Task<Employee?> VerifyCredentialsAsync(string mobile, string password);
}

public class EmployeeSyncService : IEmployeeSyncService
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<EmployeeSyncService> _logger;

    public EmployeeSyncService(
        IDbConnectionFactory dbFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<EmployeeSyncService> logger)
    {
        _dbFactory = dbFactory;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<int> SyncFromSeosApiAsync()
    {
        var baseUrl = _config["SeosApi:BaseUrl"] ?? "https://api.sainteir.com";
        var authToken = _config["SeosApi:AuthToken"] ?? "";

        if (string.IsNullOrEmpty(authToken))
            throw new InvalidOperationException("SeosApi:AuthToken not configured");

        var client = _httpClientFactory.CreateClient("SEOS");
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // 1. Fetch department list for ID → Name mapping
        var deptMap = new Dictionary<int, string>();
        try
        {
            var deptRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/Organization/Departments");
            deptRequest.Headers.Add("Cookie", $"AuthToken={authToken}");
            var deptResponse = await client.SendAsync(deptRequest);
            if (deptResponse.IsSuccessStatusCode)
            {
                var deptJson = await deptResponse.Content.ReadAsStringAsync();
                var departments = JsonSerializer.Deserialize<List<SeosDepartment>>(deptJson, jsonOpts);
                if (departments != null)
                    foreach (var d in departments)
                        if (d.DepartmentId > 0 && !string.IsNullOrEmpty(d.DepartmentName))
                            deptMap[d.DepartmentId] = d.DepartmentName;
            }
            _logger.LogInformation("Fetched {Count} departments from SEOS API", deptMap.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch departments, continuing without department names");
        }

        // 2. Fetch all users
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/Organization/AllUsers");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        request.Headers.Add("Cookie", $"AuthToken={authToken}");

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var apiUsers = JsonSerializer.Deserialize<List<SeosApiUser>>(json, jsonOpts);

        if (apiUsers == null || apiUsers.Count == 0)
        {
            _logger.LogWarning("SEOS API returned empty user list");
            return 0;
        }

        // 3. Upsert employees
        using var db = _dbFactory.CreateConnection();
        db.Open();
        var count = 0;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        foreach (var u in apiUsers)
        {
            if (string.IsNullOrEmpty(u.UserId)) continue;

            var passwordHash = !string.IsNullOrEmpty(u.Password) ? HashPassword(u.Password) : null;
            var deptName = u.DepartmentId.HasValue && deptMap.TryGetValue(u.DepartmentId.Value, out var name) ? name : null;

            await db.ExecuteAsync(
                """
                INSERT INTO Employees (UserId, FullNameInChinese, Mobile, PasswordHash, AssignBranchNo, JobTitle, DepartmentId, DepartmentName, IsAlive, LastSyncAt, CreatedAt)
                VALUES (@UserId, @Name, @Mobile, @PasswordHash, @Branch, @JobTitle, @DepartmentId, @DepartmentName, @IsAlive, @Now, @Now)
                ON CONFLICT(UserId) DO UPDATE SET
                    FullNameInChinese = @Name, Mobile = @Mobile, PasswordHash = @PasswordHash,
                    AssignBranchNo = @Branch, JobTitle = @JobTitle, DepartmentId = @DepartmentId, DepartmentName = @DepartmentName,
                    IsAlive = @IsAlive, LastSyncAt = @Now
                """,
                new
                {
                    u.UserId,
                    Name = u.FullNameInChinese ?? "",
                    Mobile = NormalizeMobile(u.Mobile),
                    PasswordHash = passwordHash,
                    Branch = u.AssignBranchNo,
                    u.JobTitle,
                    u.DepartmentId,
                    DepartmentName = deptName,
                    IsAlive = u.IsAlive == 1,
                    Now = now
                });
            count++;
        }

        _logger.LogInformation("Synced {Count} employees ({Depts} departments) from SEOS API", count, deptMap.Count);
        return count;
    }

    public async Task<List<EmployeeInfoDto>> GetAllAsync(string? branchNo = null, int? departmentId = null)
    {
        using var db = _dbFactory.CreateConnection();
        var sql = """
            SELECT e.UserId, e.FullNameInChinese, e.Mobile, e.AssignBranchNo, e.JobTitle,
                   e.DepartmentId, e.DepartmentName, e.IsAlive, e.LastSyncAt,
                   CASE WHEN r.Id IS NOT NULL THEN 1 ELSE 0 END AS IsRegistered
            FROM Employees e
            LEFT JOIN LineRegistrations r ON r.EmployeeUserId = e.UserId AND r.IsActive = 1
            WHERE 1=1
            """;
        var p = new DynamicParameters();

        if (!string.IsNullOrEmpty(branchNo))
        {
            sql += " AND e.AssignBranchNo = @BranchNo";
            p.Add("BranchNo", branchNo);
        }
        if (departmentId.HasValue)
        {
            sql += " AND e.DepartmentId = @DepartmentId";
            p.Add("DepartmentId", departmentId.Value);
        }

        sql += " ORDER BY e.DepartmentName, e.AssignBranchNo, e.FullNameInChinese";
        return (await db.QueryAsync<EmployeeInfoDto>(sql, p)).ToList();
    }

    public async Task<Employee?> VerifyCredentialsAsync(string mobile, string password)
    {
        var normalizedMobile = NormalizeMobile(mobile);
        var passwordHash = HashPassword(password);

        using var db = _dbFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Employee>(
            "SELECT * FROM Employees WHERE Mobile = @Mobile AND PasswordHash = @Hash AND IsAlive = 1",
            new { Mobile = normalizedMobile, Hash = passwordHash });
    }

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLower();
    }

    private static string? NormalizeMobile(string? mobile)
    {
        if (string.IsNullOrWhiteSpace(mobile)) return null;
        // Strip spaces, dashes, country code prefix
        var clean = mobile.Replace(" ", "").Replace("-", "").Replace("+886", "0");
        return clean.StartsWith("886") ? "0" + clean[3..] : clean;
    }
}

// Internal models matching SEOS API response
file class SeosApiUser
{
    public string? UserId { get; set; }
    public string? LoginName { get; set; }
    public string? Password { get; set; }
    public string? FullNameInChinese { get; set; }
    public string? Mobile { get; set; }
    public string? AssignBranchNo { get; set; }
    public string? JobTitle { get; set; }
    public int? DepartmentId { get; set; }
    public int IsAlive { get; set; }
}

file class SeosDepartment
{
    public int DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
}
