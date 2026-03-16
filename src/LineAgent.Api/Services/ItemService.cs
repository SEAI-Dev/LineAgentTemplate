using Dapper;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Models.Entities;

namespace LineAgent.Api.Services;

public interface IItemService
{
    Task<IEnumerable<Item>> GetAllAsync(ItemFilterDto? filter = null);
    Task<Item?> GetByIdAsync(int id);
    Task<Item> CreateAsync(CreateItemDto dto);
    Task<Item?> UpdateAsync(int id, UpdateItemDto dto);
    Task<bool> UpdateStatusAsync(int id, int status);
    Task<bool> DeleteAsync(int id);
    Task<DailySummaryDto> GetDailySummaryAsync(DateTime? date = null);
}

public class ItemService : IItemService
{
    private readonly IDbConnectionFactory _dbFactory;
    private bool IsSqlite => _dbFactory.DbType == "sqlite";

    public ItemService(IDbConnectionFactory dbFactory) => _dbFactory = dbFactory;

    public async Task<IEnumerable<Item>> GetAllAsync(ItemFilterDto? filter = null)
    {
        using var db = _dbFactory.CreateConnection();
        var sql = "SELECT * FROM Items WHERE 1=1";
        var p = new DynamicParameters();

        if (filter?.Status.HasValue == true) { sql += " AND Status = @Status"; p.Add("Status", filter.Status.Value); }
        if (filter?.Priority.HasValue == true) { sql += " AND Priority = @Priority"; p.Add("Priority", filter.Priority.Value); }
        if (!string.IsNullOrEmpty(filter?.Category)) { sql += " AND Category = @Category"; p.Add("Category", filter.Category); }

        sql += " ORDER BY Priority ASC, DueDate ASC, CreatedAt DESC";
        return await db.QueryAsync<Item>(sql, p);
    }

    public async Task<Item?> GetByIdAsync(int id)
    {
        using var db = _dbFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Item>("SELECT * FROM Items WHERE Id = @Id", new { Id = id });
    }

    public async Task<Item> CreateAsync(CreateItemDto dto)
    {
        using var db = _dbFactory.CreateConnection();
        var dueDate = dto.DueDate?.ToString("yyyy-MM-dd");
        if (IsSqlite)
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await db.ExecuteAsync(
                "INSERT INTO Items (Title, Description, Priority, Category, DueDate, CreatedAt, UpdatedAt) VALUES (@Title, @Description, @Priority, @Category, @DueDate, @Now, @Now)",
                new { dto.Title, dto.Description, dto.Priority, dto.Category, DueDate = dueDate, Now = now });
            var id = await db.QuerySingleAsync<int>("SELECT last_insert_rowid()");
            return (await GetByIdAsync(id))!;
        }
        else
        {
            var id = await db.QuerySingleAsync<int>(
                "INSERT INTO Items (Title, Description, Priority, Category, DueDate) OUTPUT INSERTED.Id VALUES (@Title, @Description, @Priority, @Category, @DueDate)",
                new { dto.Title, dto.Description, dto.Priority, dto.Category, DueDate = dueDate });
            return (await GetByIdAsync(id))!;
        }
    }

    public async Task<Item?> UpdateAsync(int id, UpdateItemDto dto)
    {
        using var db = _dbFactory.CreateConnection();
        var nowExpr = IsSqlite ? "@Now" : "GETDATE()";
        var sets = new List<string> { $"UpdatedAt = {nowExpr}" };
        var p = new DynamicParameters();
        p.Add("Id", id);
        if (IsSqlite) p.Add("Now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        if (dto.Title != null) { sets.Add("Title = @Title"); p.Add("Title", dto.Title); }
        if (dto.Description != null) { sets.Add("Description = @Description"); p.Add("Description", dto.Description); }
        if (dto.Priority.HasValue) { sets.Add("Priority = @Priority"); p.Add("Priority", dto.Priority.Value); }
        if (dto.Category != null) { sets.Add("Category = @Category"); p.Add("Category", dto.Category); }
        if (dto.DueDate.HasValue) { sets.Add("DueDate = @DueDate"); p.Add("DueDate", dto.DueDate.Value.ToString("yyyy-MM-dd")); }
        if (dto.Status.HasValue)
        {
            sets.Add("Status = @Status"); p.Add("Status", dto.Status.Value);
            if (dto.Status.Value == ItemStatus.Done) sets.Add($"CompletedAt = {nowExpr}");
        }

        await db.ExecuteAsync($"UPDATE Items SET {string.Join(", ", sets)} WHERE Id = @Id", p);
        return await GetByIdAsync(id);
    }

    public async Task<bool> UpdateStatusAsync(int id, int status)
    {
        using var db = _dbFactory.CreateConnection();
        if (IsSqlite)
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var completedAt = status == ItemStatus.Done ? now : null;
            return await db.ExecuteAsync("UPDATE Items SET Status=@Status, CompletedAt=@CompletedAt, UpdatedAt=@Now WHERE Id=@Id",
                new { Id = id, Status = status, CompletedAt = completedAt, Now = now }) > 0;
        }
        else
        {
            var ca = status == ItemStatus.Done ? "GETDATE()" : "NULL";
            return await db.ExecuteAsync($"UPDATE Items SET Status=@Status, CompletedAt={ca}, UpdatedAt=GETDATE() WHERE Id=@Id",
                new { Id = id, Status = status }) > 0;
        }
    }

    public async Task<bool> DeleteAsync(int id)
    {
        using var db = _dbFactory.CreateConnection();
        return await db.ExecuteAsync("DELETE FROM Items WHERE Id = @Id", new { Id = id }) > 0;
    }

    public async Task<DailySummaryDto> GetDailySummaryAsync(DateTime? date = null)
    {
        var d = (date ?? DateTime.Today).ToString("yyyy-MM-dd");
        using var db = _dbFactory.CreateConnection();

        var high = await db.QueryAsync<ItemBriefDto>(
            "SELECT Id, Title, Priority, Status, Category, DueDate FROM Items WHERE Priority = 1 AND Status < 2 ORDER BY DueDate ASC");
        var due = await db.QueryAsync<ItemBriefDto>(
            "SELECT Id, Title, Priority, Status, Category, DueDate FROM Items WHERE DueDate = @Date AND Status < 2 ORDER BY Priority ASC",
            new { Date = d });
        var overdue = await db.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM Items WHERE DueDate < @Date AND Status < 2", new { Date = d });

        return new DailySummaryDto
        {
            Date = date ?? DateTime.Today,
            TotalPending = due.Count(),
            InProgress = (await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM Items WHERE Status = 1")),
            Overdue = overdue,
            HighPriorityItems = high.ToList(),
            DueTodayItems = due.ToList()
        };
    }
}
