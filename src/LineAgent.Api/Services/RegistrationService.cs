using System.Collections.Concurrent;
using Dapper;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Models.Entities;

namespace LineAgent.Api.Services;

public interface IRegistrationService
{
    RegistrationSession? GetSession(string lineUserId);
    RegistrationSession StartSession(string lineUserId, int channelId);
    void RemoveSession(string lineUserId);
    Task<LineRegistration?> GetByLineUserIdAsync(string lineUserId, int channelId);
    Task<LineRegistration> RegisterAsync(string lineUserId, string? displayName, string employeeUserId, int channelId);
    Task<List<RegistrationInfoDto>> GetAllAsync(int? channelId = null);
    Task<bool> DeactivateAsync(int id);
    Task UpdateLastInteractionAsync(string lineUserId, int channelId);
}

public class RegistrationService : IRegistrationService
{
    private static readonly ConcurrentDictionary<string, RegistrationSession> _sessions = new();
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(30);
    private const int MaxFailCount = 3;

    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<RegistrationService> _logger;

    public RegistrationService(IDbConnectionFactory dbFactory, ILogger<RegistrationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    // ===== Session Management =====

    public RegistrationSession? GetSession(string lineUserId)
    {
        if (!_sessions.TryGetValue(lineUserId, out var session)) return null;

        // Expired
        if (DateTime.UtcNow - session.CreatedAt > SessionTimeout)
        {
            _sessions.TryRemove(lineUserId, out _);
            return null;
        }

        // Locked
        if (session.LockedUntil.HasValue && DateTime.UtcNow < session.LockedUntil.Value)
            return session; // Return locked session so caller can show lock message

        return session;
    }

    public RegistrationSession StartSession(string lineUserId, int channelId)
    {
        // Check existing lock
        if (_sessions.TryGetValue(lineUserId, out var existing) &&
            existing.LockedUntil.HasValue && DateTime.UtcNow < existing.LockedUntil.Value)
            return existing;

        var session = new RegistrationSession
        {
            LineUserId = lineUserId,
            ChannelId = channelId,
            State = RegistrationState.AwaitingPhone,
            CreatedAt = DateTime.UtcNow
        };
        _sessions[lineUserId] = session;
        return session;
    }

    public void RemoveSession(string lineUserId) => _sessions.TryRemove(lineUserId, out _);

    public void RecordFail(string lineUserId)
    {
        if (!_sessions.TryGetValue(lineUserId, out var session)) return;
        session.FailCount++;
        if (session.FailCount >= MaxFailCount)
        {
            session.LockedUntil = DateTime.UtcNow + LockDuration;
            _logger.LogWarning("Registration locked for {LineUserId} after {Count} failures", lineUserId, session.FailCount);
        }
    }

    // ===== Persistent Registration =====

    public async Task<LineRegistration?> GetByLineUserIdAsync(string lineUserId, int channelId)
    {
        using var db = _dbFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<LineRegistration>(
            "SELECT * FROM LineRegistrations WHERE LineUserId = @LineUserId AND ChannelId = @ChannelId AND IsActive = 1",
            new { LineUserId = lineUserId, ChannelId = channelId });
    }

    public async Task<LineRegistration> RegisterAsync(string lineUserId, string? displayName, string employeeUserId, int channelId)
    {
        using var db = _dbFactory.CreateConnection();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // Deactivate existing registration for this LINE user on this channel
        await db.ExecuteAsync(
            "UPDATE LineRegistrations SET IsActive = 0 WHERE LineUserId = @LineUserId AND ChannelId = @ChannelId",
            new { LineUserId = lineUserId, ChannelId = channelId });

        await db.ExecuteAsync(
            """
            INSERT INTO LineRegistrations (LineUserId, EmployeeUserId, ChannelId, DisplayName, IsActive, RegisteredAt, LastInteractionAt)
            VALUES (@LineUserId, @EmployeeUserId, @ChannelId, @DisplayName, 1, @Now, @Now)
            """,
            new { LineUserId = lineUserId, EmployeeUserId = employeeUserId, ChannelId = channelId, DisplayName = displayName, Now = now });

        var id = await db.QuerySingleAsync<int>("SELECT last_insert_rowid()");
        _logger.LogInformation("Registered LINE user {LineUserId} as employee {EmployeeUserId} on channel {ChannelId}", lineUserId, employeeUserId, channelId);
        return (await db.QueryFirstAsync<LineRegistration>("SELECT * FROM LineRegistrations WHERE Id = @Id", new { Id = id }));
    }

    public async Task<List<RegistrationInfoDto>> GetAllAsync(int? channelId = null)
    {
        using var db = _dbFactory.CreateConnection();
        var sql = """
            SELECT r.Id, r.LineUserId, r.DisplayName, r.EmployeeUserId, r.ChannelId, r.IsActive, r.RegisteredAt, r.LastInteractionAt,
                   e.FullNameInChinese AS EmployeeName, e.AssignBranchNo AS BranchNo,
                   c.ChannelName
            FROM LineRegistrations r
            LEFT JOIN Employees e ON e.UserId = r.EmployeeUserId
            LEFT JOIN LineChannels c ON c.Id = r.ChannelId
            WHERE 1=1
            """;
        var p = new DynamicParameters();

        if (channelId.HasValue)
        {
            sql += " AND r.ChannelId = @ChannelId";
            p.Add("ChannelId", channelId.Value);
        }

        sql += " ORDER BY r.RegisteredAt DESC";
        return (await db.QueryAsync<RegistrationInfoDto>(sql, p)).ToList();
    }

    public async Task<bool> DeactivateAsync(int id)
    {
        using var db = _dbFactory.CreateConnection();
        return await db.ExecuteAsync("UPDATE LineRegistrations SET IsActive = 0 WHERE Id = @Id", new { Id = id }) > 0;
    }

    public async Task UpdateLastInteractionAsync(string lineUserId, int channelId)
    {
        using var db = _dbFactory.CreateConnection();
        await db.ExecuteAsync(
            "UPDATE LineRegistrations SET LastInteractionAt = @Now WHERE LineUserId = @LineUserId AND ChannelId = @ChannelId AND IsActive = 1",
            new { LineUserId = lineUserId, ChannelId = channelId, Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
    }
}
