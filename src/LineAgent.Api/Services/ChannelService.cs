using System.Collections.Concurrent;
using Dapper;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Models.Entities;

namespace LineAgent.Api.Services;

public interface IChannelService
{
    Task<List<LineChannel>> GetAllAsync();
    Task<LineChannel?> GetByIdAsync(int id);
    Task<LineChannel?> GetByWebhookPathAsync(string path);
    Task<LineChannel> CreateAsync(CreateChannelDto dto);
    Task<LineChannel?> UpdateAsync(int id, UpdateChannelDto dto);
    Task<bool> DeleteAsync(int id);
    Task<List<ChannelInfoDto>> GetChannelInfosAsync();
    void InvalidateCache();
}

public class ChannelService : IChannelService
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<ChannelService> _logger;
    private readonly ConcurrentDictionary<string, LineChannel> _pathCache = new();
    private bool _cacheLoaded;

    public ChannelService(IDbConnectionFactory dbFactory, ILogger<ChannelService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<LineChannel>> GetAllAsync()
    {
        using var db = _dbFactory.CreateConnection();
        return (await db.QueryAsync<LineChannel>("SELECT * FROM LineChannels ORDER BY CreatedAt")).ToList();
    }

    public async Task<LineChannel?> GetByIdAsync(int id)
    {
        using var db = _dbFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<LineChannel>("SELECT * FROM LineChannels WHERE Id = @Id", new { Id = id });
    }

    public async Task<LineChannel?> GetByWebhookPathAsync(string path)
    {
        if (_pathCache.TryGetValue(path, out var cached)) return cached;

        await EnsureCacheLoadedAsync();
        _pathCache.TryGetValue(path, out cached);
        return cached;
    }

    public async Task<LineChannel> CreateAsync(CreateChannelDto dto)
    {
        using var db = _dbFactory.CreateConnection();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await db.ExecuteAsync(
            """
            INSERT INTO LineChannels (ChannelName, ChannelId, ChannelSecret, ChannelAccessToken, WebhookPath, BranchNo, IsActive, CreatedAt, UpdatedAt)
            VALUES (@ChannelName, @ChannelId, @ChannelSecret, @ChannelAccessToken, @WebhookPath, @BranchNo, 1, @Now, @Now)
            """,
            new { dto.ChannelName, dto.ChannelId, dto.ChannelSecret, dto.ChannelAccessToken, dto.WebhookPath, dto.BranchNo, Now = now });

        InvalidateCache();
        var id = await db.QuerySingleAsync<int>("SELECT last_insert_rowid()");
        return (await GetByIdAsync(id))!;
    }

    public async Task<LineChannel?> UpdateAsync(int id, UpdateChannelDto dto)
    {
        using var db = _dbFactory.CreateConnection();
        var existing = await GetByIdAsync(id);
        if (existing == null) return null;

        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var sets = new List<string> { "UpdatedAt = @Now" };
        var p = new DynamicParameters();
        p.Add("Id", id);
        p.Add("Now", now);

        if (dto.ChannelName != null) { sets.Add("ChannelName = @ChannelName"); p.Add("ChannelName", dto.ChannelName); }
        if (dto.ChannelSecret != null) { sets.Add("ChannelSecret = @ChannelSecret"); p.Add("ChannelSecret", dto.ChannelSecret); }
        if (dto.ChannelAccessToken != null) { sets.Add("ChannelAccessToken = @ChannelAccessToken"); p.Add("ChannelAccessToken", dto.ChannelAccessToken); }
        if (dto.BranchNo != null) { sets.Add("BranchNo = @BranchNo"); p.Add("BranchNo", dto.BranchNo); }
        if (dto.IsActive.HasValue) { sets.Add("IsActive = @IsActive"); p.Add("IsActive", dto.IsActive.Value); }

        await db.ExecuteAsync($"UPDATE LineChannels SET {string.Join(", ", sets)} WHERE Id = @Id", p);
        InvalidateCache();
        return await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        using var db = _dbFactory.CreateConnection();
        var result = await db.ExecuteAsync("UPDATE LineChannels SET IsActive = 0, UpdatedAt = @Now WHERE Id = @Id",
            new { Id = id, Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
        InvalidateCache();
        return result > 0;
    }

    public async Task<List<ChannelInfoDto>> GetChannelInfosAsync()
    {
        using var db = _dbFactory.CreateConnection();
        return (await db.QueryAsync<ChannelInfoDto>(
            """
            SELECT c.Id, c.ChannelName, c.ChannelId, c.WebhookPath, c.BranchNo, c.IsActive, c.CreatedAt,
                   (SELECT COUNT(*) FROM LineRegistrations r WHERE r.ChannelId = c.Id AND r.IsActive = 1) AS RegisteredUserCount
            FROM LineChannels c ORDER BY c.CreatedAt
            """)).ToList();
    }

    public void InvalidateCache()
    {
        _pathCache.Clear();
        _cacheLoaded = false;
    }

    private async Task EnsureCacheLoadedAsync()
    {
        if (_cacheLoaded) return;
        var channels = await GetAllAsync();
        foreach (var ch in channels.Where(c => c.IsActive))
            _pathCache[ch.WebhookPath] = ch;
        _cacheLoaded = true;
    }
}
