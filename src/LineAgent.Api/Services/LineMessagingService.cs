using System.Text;
using System.Text.Json;
using Dapper;
using LineAgent.Api.Models.Entities;

namespace LineAgent.Api.Services;

public interface ILineMessagingService
{
    Task<bool> PushTextMessageAsync(string userId, string text);
    Task<bool> ReplyTextMessageAsync(string replyToken, string text);
    Task SendDailyReminderAsync();
    Task<bool> SendTestMessageAsync();
    string GetChannelAccessToken();
}

public class LineMessagingService : ILineMessagingService
{
    private const string PushUrl = "https://api.line.me/v2/bot/message/push";
    private const string ReplyUrl = "https://api.line.me/v2/bot/message/reply";
    private readonly HttpClient _httpClient;
    private readonly string _channelAccessToken;
    private readonly IDbConnectionFactory _dbFactory;
    private readonly IItemService _itemService;
    private readonly ILogger<LineMessagingService> _logger;

    public LineMessagingService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        IDbConnectionFactory dbFactory,
        IItemService itemService,
        ILogger<LineMessagingService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("LINE");
        _channelAccessToken = config["Line:ChannelAccessToken"]
            ?? Environment.GetEnvironmentVariable("LINE_CHANNEL_ACCESS_TOKEN")
            ?? throw new InvalidOperationException("LINE Channel Access Token not configured");
        _dbFactory = dbFactory;
        _itemService = itemService;
        _logger = logger;
    }

    public string GetChannelAccessToken() => _channelAccessToken;

    public async Task<bool> ReplyTextMessageAsync(string replyToken, string text)
    {
        var payload = new { replyToken, messages = new[] { new { type = "text", text } } };
        return await SendAsync(ReplyUrl, payload, "Reply");
    }

    public async Task<bool> PushTextMessageAsync(string userId, string text)
    {
        var payload = new { to = userId, messages = new[] { new { type = "text", text } } };
        return await SendAsync(PushUrl, payload, "Push", userId);
    }

    private async Task<bool> SendAsync(string url, object payload, string type, string? userId = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _channelAccessToken);

            var response = await _httpClient.SendAsync(request);
            var success = response.IsSuccessStatusCode;

            if (!success)
                _logger.LogError("LINE {Type} failed: {StatusCode} {Body}", type, response.StatusCode, await response.Content.ReadAsStringAsync());

            if (type == "Push" && userId != null)
                await LogNotificationAsync(type, userId, json, success, success ? null : await response.Content.ReadAsStringAsync());

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LINE {Type} exception", type);
            return false;
        }
    }

    private async Task LogNotificationAsync(string type, string userId, string content, bool success, string? error)
    {
        try
        {
            using var db = _dbFactory.CreateConnection();
            await db.ExecuteAsync(
                "INSERT INTO NotificationLogs (NotificationType, RecipientUserId, Content, Success, ErrorMessage) VALUES (@Type, @UserId, @Content, @Success, @Error)",
                new { Type = type, UserId = userId, Content = content, Success = success, Error = error });
        }
        catch { }
    }

    private async Task<List<LineUser>> GetActiveUsersAsync(string notifyType)
    {
        using var db = _dbFactory.CreateConnection();
        return (await db.QueryAsync<LineUser>(
            "SELECT * FROM LineUsers WHERE IsActive = 1 AND NotifyTypes LIKE @Pattern",
            new { Pattern = $"%{notifyType}%" })).ToList();
    }

    // ==================== Scheduled Notifications (Push = costs quota) ====================

    public async Task SendDailyReminderAsync()
    {
        var summary = await _itemService.GetDailySummaryAsync();
        var users = await GetActiveUsersAsync("Daily");
        var date = summary.Date.ToString("M/d (ddd)");

        var lines = new List<string> { $"📋 {date} Daily Reminder", "━━━━━━━━━━━━━━━" };

        if (summary.HighPriorityItems.Any())
        {
            lines.Add("🔴 High Priority");
            foreach (var t in summary.HighPriorityItems)
                lines.Add($"  • {t.Title}");
        }
        if (summary.DueTodayItems.Any())
        {
            lines.Add("📅 Due Today");
            foreach (var t in summary.DueTodayItems)
                lines.Add($"  • {t.Title}");
        }

        lines.Add("━━━━━━━━━━━━━━━");
        lines.Add($"Due: {summary.TotalPending} | InProgress: {summary.InProgress} | Overdue: {summary.Overdue}");

        var text = string.Join("\n", lines);
        foreach (var user in users)
            await PushTextMessageAsync(user.LineUserId, text);

        _logger.LogInformation("Daily reminder sent to {Count} users", users.Count);
    }

    public async Task<bool> SendTestMessageAsync()
    {
        var users = await GetActiveUsersAsync("Daily");
        if (!users.Any()) return false;
        return await PushTextMessageAsync(users.First().LineUserId,
            $"🤖 LINE Agent Test\n\nConnection OK! Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }
}
