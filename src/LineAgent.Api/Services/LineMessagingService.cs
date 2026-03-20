using System.Text;
using System.Text.Json;
using Dapper;
using LineAgent.Api.Models.Entities;

namespace LineAgent.Api.Services;

public interface ILineMessagingService
{
    // Multi-channel methods (use channel's access token)
    Task<bool> PushTextMessageAsync(string accessToken, string userId, string text);
    Task<bool> ReplyTextMessageAsync(string accessToken, string replyToken, string text);

    // Legacy single-channel methods (use default config token, for backward compat)
    Task<bool> PushTextMessageAsync(string userId, string text);
    Task<bool> ReplyTextMessageAsync(string replyToken, string text);

    Task SendDailyReminderAsync();
    Task<bool> SendTestMessageAsync();
    Task<(int Sent, int Failed)> BroadcastAsync(string text, int? channelId = null);
    string GetChannelAccessToken();
}

public class LineMessagingService : ILineMessagingService
{
    private const string PushUrl = "https://api.line.me/v2/bot/message/push";
    private const string ReplyUrl = "https://api.line.me/v2/bot/message/reply";
    private readonly HttpClient _httpClient;
    private readonly string _defaultAccessToken;
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
        _defaultAccessToken = config["Line:ChannelAccessToken"]
            ?? Environment.GetEnvironmentVariable("LINE_CHANNEL_ACCESS_TOKEN")
            ?? ""; // No longer throw — multi-channel mode may not have a default token
        _dbFactory = dbFactory;
        _itemService = itemService;
        _logger = logger;
    }

    public string GetChannelAccessToken() => _defaultAccessToken;

    // ===== Multi-channel =====

    public Task<bool> ReplyTextMessageAsync(string accessToken, string replyToken, string text)
    {
        var payload = new { replyToken, messages = new[] { new { type = "text", text } } };
        return SendAsync(ReplyUrl, payload, "Reply", accessToken);
    }

    public Task<bool> PushTextMessageAsync(string accessToken, string userId, string text)
    {
        var payload = new { to = userId, messages = new[] { new { type = "text", text } } };
        return SendAsync(PushUrl, payload, "Push", accessToken, userId);
    }

    // ===== Legacy single-channel (backward compat) =====

    public Task<bool> ReplyTextMessageAsync(string replyToken, string text)
        => ReplyTextMessageAsync(_defaultAccessToken, replyToken, text);

    public Task<bool> PushTextMessageAsync(string userId, string text)
        => PushTextMessageAsync(_defaultAccessToken, userId, text);

    // ===== Internal =====

    private async Task<bool> SendAsync(string url, object payload, string type, string accessToken, string? userId = null)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

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

    // ==================== Multi-channel Push Helpers ====================

    /// <summary>
    /// Get all registered users grouped by channel (for multi-channel push).
    /// Returns (LineUserId, ChannelAccessToken) pairs.
    /// </summary>
    private async Task<List<(string LineUserId, string AccessToken)>> GetRegisteredRecipientsAsync(int? channelId = null)
    {
        using var db = _dbFactory.CreateConnection();
        var sql = """
            SELECT r.LineUserId, c.ChannelAccessToken
            FROM LineRegistrations r
            INNER JOIN LineChannels c ON c.Id = r.ChannelId AND c.IsActive = 1
            WHERE r.IsActive = 1
            """;

        if (channelId.HasValue)
            sql += " AND r.ChannelId = @ChannelId";

        var rows = await db.QueryAsync<(string LineUserId, string AccessToken)>(sql,
            channelId.HasValue ? new { ChannelId = channelId.Value } : null);
        return rows.ToList();
    }

    /// <summary>
    /// Push a text message to all registered users (or filtered by channel).
    /// Returns (sent, failed) counts.
    /// </summary>
    public async Task<(int Sent, int Failed)> BroadcastAsync(string text, int? channelId = null)
    {
        var recipients = await GetRegisteredRecipientsAsync(channelId);
        int sent = 0, failed = 0;

        foreach (var (lineUserId, accessToken) in recipients)
        {
            var ok = await PushTextMessageAsync(accessToken, lineUserId, text);
            if (ok) sent++; else failed++;
        }

        _logger.LogInformation("Broadcast: {Sent} sent, {Failed} failed (channel={ChannelId})", sent, failed, channelId?.ToString() ?? "all");
        return (sent, failed);
    }

    // ==================== Scheduled Notifications (Push = costs quota) ====================

    public async Task SendDailyReminderAsync()
    {
        var summary = await _itemService.GetDailySummaryAsync();
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
        var (sent, _) = await BroadcastAsync(text);

        // Fallback: also send to legacy LineUsers if any
        if (sent == 0)
        {
            using var db = _dbFactory.CreateConnection();
            var legacyUsers = (await db.QueryAsync<LineUser>(
                "SELECT * FROM LineUsers WHERE IsActive = 1 AND NotifyTypes LIKE '%Daily%'")).ToList();
            foreach (var user in legacyUsers)
                await PushTextMessageAsync(user.LineUserId, text);
            if (legacyUsers.Any())
                _logger.LogInformation("Daily reminder fallback to legacy LineUsers: {Count}", legacyUsers.Count);
        }
    }

    public async Task<bool> SendTestMessageAsync()
    {
        var testMsg = $"🤖 LINE Agent Test\n\nConnection OK!\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

        // Try registered users first
        var recipients = await GetRegisteredRecipientsAsync();
        if (recipients.Any())
        {
            var (lineUserId, accessToken) = recipients.First();
            return await PushTextMessageAsync(accessToken, lineUserId, testMsg);
        }

        // Fallback to legacy
        using var db = _dbFactory.CreateConnection();
        var legacyUser = await db.QueryFirstOrDefaultAsync<LineUser>("SELECT * FROM LineUsers WHERE IsActive = 1 LIMIT 1");
        if (legacyUser != null)
            return await PushTextMessageAsync(legacyUser.LineUserId, testMsg);

        return false;
    }
}
