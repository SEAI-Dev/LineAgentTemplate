using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Models.Entities;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

/// <summary>
/// Multi-channel LINE Webhook receiver with employee registration flow.
/// Route: /api/line/webhook/{path} — path identifies which LINE OA channel.
/// </summary>
[ApiController]
[Route("api/line")]
public class LineWebhookController : ControllerBase
{
    private readonly IChannelService _channelService;
    private readonly IItemService _itemService;
    private readonly ILineMessagingService _lineService;
    private readonly IRegistrationService _registrationService;
    private readonly IEmployeeSyncService _employeeSyncService;
    private readonly ILogger<LineWebhookController> _logger;

    // Fallback channel secret for legacy single-channel mode
    private readonly string? _legacyChannelSecret;

    public LineWebhookController(
        IChannelService channelService,
        IConfiguration config,
        IItemService itemService,
        ILineMessagingService lineService,
        IRegistrationService registrationService,
        IEmployeeSyncService employeeSyncService,
        ILogger<LineWebhookController> logger)
    {
        _channelService = channelService;
        _itemService = itemService;
        _lineService = lineService;
        _registrationService = registrationService;
        _employeeSyncService = employeeSyncService;
        _logger = logger;
        _legacyChannelSecret = config["Line:ChannelSecret"]
            ?? Environment.GetEnvironmentVariable("LINE_CHANNEL_SECRET");
    }

    /// <summary>
    /// Multi-channel webhook endpoint.
    /// Each LINE OA sets webhook to: https://host/api/line/webhook/{webhookPath}
    /// </summary>
    [HttpPost("webhook/{path}")]
    public async Task<IActionResult> WebhookMultiChannel(string path)
    {
        var channel = await _channelService.GetByWebhookPathAsync(path);
        if (channel == null || !channel.IsActive)
            return NotFound(new { error = $"Channel '{path}' not found" });

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var signature = Request.Headers["X-Line-Signature"].FirstOrDefault();
        if (!VerifySignature(body, signature, channel.ChannelSecret))
            return Unauthorized();

        await ProcessWebhookEventsAsync(body, channel);
        return Ok();
    }

    /// <summary>
    /// Legacy single-channel webhook (backward compatible).
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> WebhookLegacy()
    {
        if (string.IsNullOrEmpty(_legacyChannelSecret))
            return BadRequest(new { error = "Legacy webhook not configured. Use /api/line/webhook/{path} instead." });

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var signature = Request.Headers["X-Line-Signature"].FirstOrDefault();
        if (!VerifySignature(body, signature, _legacyChannelSecret))
            return Unauthorized();

        var doc = JsonDocument.Parse(body);
        foreach (var evt in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            if (evt.GetProperty("type").GetString() != "message") continue;
            if (evt.GetProperty("message").GetProperty("type").GetString() != "text") continue;

            var text = evt.GetProperty("message").GetProperty("text").GetString()!;
            var replyToken = evt.GetProperty("replyToken").GetString()!;
            await HandleCommandAsync(text.Trim(), replyToken, _lineService.GetChannelAccessToken());
        }

        return Ok();
    }

    // ==================== Event Processing ====================

    private async Task ProcessWebhookEventsAsync(string body, LineChannel channel)
    {
        var doc = JsonDocument.Parse(body);
        foreach (var evt in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var eventType = evt.GetProperty("type").GetString();
            var replyToken = evt.TryGetProperty("replyToken", out var rt) ? rt.GetString() : null;

            // Extract source user ID
            string? lineUserId = null;
            if (evt.TryGetProperty("source", out var source) && source.TryGetProperty("userId", out var uid))
                lineUserId = uid.GetString();

            switch (eventType)
            {
                case "follow":
                    // User added the OA as friend — prompt registration
                    if (replyToken != null)
                        await HandleFollowAsync(replyToken, lineUserId, channel);
                    break;

                case "message":
                    if (evt.GetProperty("message").GetProperty("type").GetString() != "text") continue;
                    var text = evt.GetProperty("message").GetProperty("text").GetString()!.Trim();

                    if (lineUserId != null)
                        await HandleMessageAsync(text, replyToken!, lineUserId, channel);
                    break;
            }
        }
    }

    private async Task HandleFollowAsync(string replyToken, string? lineUserId, LineChannel channel)
    {
        await _lineService.ReplyTextMessageAsync(channel.ChannelAccessToken, replyToken,
            $"歡迎加入 {channel.ChannelName}！\n\n" +
            "請輸入「註冊」開始員工身份綁定，\n" +
            "或輸入 /help 查看可用指令。");
    }

    private async Task HandleMessageAsync(string text, string replyToken, string lineUserId, LineChannel channel)
    {
        var accessToken = channel.ChannelAccessToken;

        // Check if user is in registration flow
        var session = _registrationService.GetSession(lineUserId);
        if (session != null)
        {
            await HandleRegistrationFlowAsync(text, replyToken, lineUserId, accessToken, session, channel);
            return;
        }

        // Check if user wants to start registration
        if (text == "註冊" || text.Equals("/register", StringComparison.OrdinalIgnoreCase))
        {
            // Check if already registered
            var existing = await _registrationService.GetByLineUserIdAsync(lineUserId, channel.Id);
            if (existing != null)
            {
                await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                    $"您已完成註冊（員工編號：{existing.EmployeeUserId}）。\n如需重新綁定，請聯繫管理員。");
                return;
            }

            var newSession = _registrationService.StartSession(lineUserId, channel.Id);
            if (newSession.LockedUntil.HasValue)
            {
                var remaining = (newSession.LockedUntil.Value - DateTime.UtcNow).Minutes;
                await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                    $"驗證失敗次數過多，請 {remaining} 分鐘後再試。");
                return;
            }

            await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                "開始員工身份驗證\n━━━━━━━━━━━━\n請輸入您的手機號碼\n（例：0912345678）");
            return;
        }

        // Check if registered user — update interaction time
        var reg = await _registrationService.GetByLineUserIdAsync(lineUserId, channel.Id);
        if (reg != null)
            _ = _registrationService.UpdateLastInteractionAsync(lineUserId, channel.Id);

        // Normal command routing
        await HandleCommandAsync(text, replyToken, accessToken);
    }

    // ==================== Registration Flow ====================

    private async Task HandleRegistrationFlowAsync(
        string text, string replyToken, string lineUserId,
        string accessToken, RegistrationSession session, LineChannel channel)
    {
        // Allow cancel at any point
        if (text == "取消" || text.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _registrationService.RemoveSession(lineUserId);
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken, "已取消註冊流程。");
            return;
        }

        // Locked check
        if (session.LockedUntil.HasValue && DateTime.UtcNow < session.LockedUntil.Value)
        {
            var remaining = (session.LockedUntil.Value - DateTime.UtcNow).Minutes;
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                $"驗證失敗次數過多，請 {remaining} 分鐘後再試。");
            return;
        }

        switch (session.State)
        {
            case RegistrationState.AwaitingPhone:
                await HandlePhoneInputAsync(text, replyToken, lineUserId, accessToken, session);
                break;

            case RegistrationState.AwaitingPassword:
                await HandlePasswordInputAsync(text, replyToken, lineUserId, accessToken, session, channel);
                break;
        }
    }

    private async Task HandlePhoneInputAsync(
        string text, string replyToken, string lineUserId,
        string accessToken, RegistrationSession session)
    {
        // Basic phone validation: starts with 0, 10 digits
        var phone = text.Replace(" ", "").Replace("-", "");
        if (!System.Text.RegularExpressions.Regex.IsMatch(phone, @"^0\d{9}$"))
        {
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                "手機號碼格式不正確，請輸入 10 位數字\n（例：0912345678）\n\n輸入「取消」可退出註冊");
            return;
        }

        session.Phone = phone;
        session.State = RegistrationState.AwaitingPassword;

        await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
            $"手機號碼：{phone[..4]}****{phone[8..]}\n\n請輸入您的系統密碼");
    }

    private async Task HandlePasswordInputAsync(
        string text, string replyToken, string lineUserId,
        string accessToken, RegistrationSession session, LineChannel channel)
    {
        var employee = await _employeeSyncService.VerifyCredentialsAsync(session.Phone!, text);

        if (employee == null)
        {
            session.FailCount++;
            var remaining = 3 - session.FailCount;

            if (remaining <= 0)
            {
                session.LockedUntil = DateTime.UtcNow + TimeSpan.FromMinutes(30);
                await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                    "驗證失敗次數過多，帳號已鎖定 30 分鐘。\n如有問題請聯繫管理員。");
                _logger.LogWarning("Registration locked: {LineUserId} after 3 failures", lineUserId);
            }
            else
            {
                await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                    $"驗證失敗，請確認手機號碼與密碼。\n剩餘嘗試次數：{remaining}\n\n輸入「取消」可退出註冊");
            }
            return;
        }

        // Success — create registration
        // Try to get LINE display name from profile
        string? displayName = null;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var profileJson = await http.GetStringAsync($"https://api.line.me/v2/bot/profile/{lineUserId}");
            var profile = JsonDocument.Parse(profileJson);
            displayName = profile.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
        }
        catch { }

        await _registrationService.RegisterAsync(lineUserId, displayName, employee.UserId, channel.Id);
        _registrationService.RemoveSession(lineUserId);

        await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
            $"註冊成功！\n━━━━━━━━━━━━\n姓名：{employee.FullNameInChinese}\n員工編號：{employee.UserId}\n分館：{employee.AssignBranchNo ?? "未指定"}\n\n輸入 /help 查看可用指令");

        _logger.LogInformation("Registration success: LINE {LineUserId} = Employee {EmployeeUserId} on channel {ChannelId}",
            lineUserId, employee.UserId, channel.Id);
    }

    // ==================== Command Router ====================

    private async Task HandleCommandAsync(string text, string replyToken, string accessToken)
    {
        if (text.StartsWith("/add ", StringComparison.OrdinalIgnoreCase))
            await HandleAddAsync(replyToken, text[5..].Trim(), accessToken);

        else if (text.Equals("/list", StringComparison.OrdinalIgnoreCase))
            await HandleListAsync(replyToken, accessToken);

        else if (text.StartsWith("/done ", StringComparison.OrdinalIgnoreCase))
            await HandleDoneAsync(replyToken, text[6..].Trim(), accessToken);

        else if (text.Equals("/today", StringComparison.OrdinalIgnoreCase))
            await HandleTodayAsync(replyToken, accessToken);

        else if (text.Equals("/quota", StringComparison.OrdinalIgnoreCase))
            await HandleQuotaAsync(replyToken, accessToken);

        else if (text.Equals("/help", StringComparison.OrdinalIgnoreCase))
            await HandleHelpAsync(replyToken, accessToken);

        else if (text.StartsWith("/"))
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
                $"未知指令：{text.Split(' ')[0]}\n輸入 /help 查看可用指令");
    }

    // ==================== Command Handlers ====================

    private async Task HandleAddAsync(string replyToken, string input, string accessToken)
    {
        var priority = 2;
        string? category = null;
        var title = input;

        if (title.StartsWith("!1 ")) { priority = 1; title = title[3..]; }
        else if (title.StartsWith("!2 ")) { priority = 2; title = title[3..]; }
        else if (title.StartsWith("!3 ")) { priority = 3; title = title[3..]; }

        var atIdx = title.IndexOf('@');
        if (atIdx >= 0)
        {
            var end = title.IndexOf(' ', atIdx + 1);
            category = end >= 0 ? title[(atIdx + 1)..end] : title[(atIdx + 1)..];
            title = end >= 0 ? (title[..atIdx] + title[end..]).Trim() : title[..atIdx].Trim();
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken, "用法：/add [!1|!2|!3] 標題 [@分類]");
            return;
        }

        var item = await _itemService.CreateAsync(new CreateItemDto
        {
            Title = title, Priority = priority, Category = category, DueDate = DateTime.Today
        });

        var pLabel = priority == 1 ? "🔴" : priority == 2 ? "🟡" : "⚪";
        var cLabel = category != null ? $" @{category}" : "";
        await _lineService.ReplyTextMessageAsync(accessToken, replyToken, $"✅ #{item.Id} {pLabel} {title}{cLabel}");
    }

    private async Task HandleListAsync(string replyToken, string accessToken)
    {
        var items = (await _itemService.GetAllAsync(new ItemFilterDto()))
            .Where(i => i.Status < 2).ToList();

        if (!items.Any())
        {
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken, "📋 沒有待辦事項！");
            return;
        }

        var lines = new List<string> { $"📋 待辦事項 ({items.Count})", "━━━━━━━━━━━━" };
        foreach (var t in items.Take(20))
        {
            var p = t.Priority == 1 ? "🔴" : t.Priority == 2 ? "🟡" : "⚪";
            var s = t.Status == 1 ? "🔄" : "⬜";
            lines.Add($"{s}{p} #{t.Id} {t.Title}");
        }
        if (items.Count > 20) lines.Add($"... +{items.Count - 20} more");

        await _lineService.ReplyTextMessageAsync(accessToken, replyToken, string.Join("\n", lines));
    }

    private async Task HandleDoneAsync(string replyToken, string input, string accessToken)
    {
        var ids = input.Split(',', ' ')
            .Select(s => s.Trim().TrimStart('#'))
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse).ToList();

        if (!ids.Any())
        {
            await _lineService.ReplyTextMessageAsync(accessToken, replyToken, "用法：/done 1 或 /done 1,2,3");
            return;
        }

        var results = new List<string>();
        foreach (var id in ids)
        {
            var ok = await _itemService.UpdateStatusAsync(id, 2);
            var item = ok ? await _itemService.GetByIdAsync(id) : null;
            results.Add(ok ? $"  ✅ #{id} {item?.Title}" : $"  ❌ #{id} 找不到");
        }
        await _lineService.ReplyTextMessageAsync(accessToken, replyToken, string.Join("\n", results));
    }

    private async Task HandleTodayAsync(string replyToken, string accessToken)
    {
        var s = await _itemService.GetDailySummaryAsync();
        var lines = new List<string> { $"📋 {s.Date:M/d} 摘要", "━━━━━━━━━━━━" };
        if (s.HighPriorityItems.Any())
        {
            lines.Add("🔴 高優先");
            foreach (var t in s.HighPriorityItems) lines.Add($"  • {t.Title}");
        }
        if (s.DueTodayItems.Any())
        {
            lines.Add("📅 今日到期");
            foreach (var t in s.DueTodayItems) lines.Add($"  • {t.Title}");
        }
        lines.Add($"\n待處理: {s.TotalPending} | 進行中: {s.InProgress} | 已逾期: {s.Overdue}");
        await _lineService.ReplyTextMessageAsync(accessToken, replyToken, string.Join("\n", lines));
    }

    private async Task HandleQuotaAsync(string replyToken, string accessToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var qr = JsonDocument.Parse(await http.GetStringAsync("https://api.line.me/v2/bot/message/quota"));
        var ur = JsonDocument.Parse(await http.GetStringAsync("https://api.line.me/v2/bot/message/quota/consumption"));

        var type = qr.RootElement.GetProperty("type").GetString();
        var limit = type == "limited" ? qr.RootElement.GetProperty("value").GetInt64() : -1;
        var used = ur.RootElement.GetProperty("totalUsage").GetInt64();

        var lines = new List<string> { "📨 LINE 訊息額度", "━━━━━━━━━━━━" };
        if (limit > 0)
        {
            var pct = Math.Round((double)used / limit * 100, 1);
            lines.Add($"額度: {limit}/月");
            lines.Add($"已用: {used} ({pct}%)");
            lines.Add($"剩餘: {limit - used}");
            lines.Add($"[{new string('█', (int)(pct / 5))}{new string('░', 20 - (int)(pct / 5))}]");
            lines.Add("📌 僅排程推播計額度，指令回覆免費。");
        }
        else
        {
            lines.Add($"方案: 無限制");
            lines.Add($"已用: {used}");
        }
        await _lineService.ReplyTextMessageAsync(accessToken, replyToken, string.Join("\n", lines));
    }

    private async Task HandleHelpAsync(string replyToken, string accessToken)
    {
        await _lineService.ReplyTextMessageAsync(accessToken, replyToken,
            "🤖 LINE Agent 指令\n" +
            "━━━━━━━━━━━━━━━\n" +
            "註冊              — 員工身份綁定\n" +
            "/add 標題          — 新增事項\n" +
            "/add !1 標題       — 新增高優先\n" +
            "/add 標題 @分類     — 新增含分類\n" +
            "/list              — 列出待辦\n" +
            "/done 1            — 完成 #1\n" +
            "/done 1,2,3        — 批次完成\n" +
            "/today             — 今日摘要\n" +
            "/quota             — 訊息額度\n" +
            "/help              — 此說明\n" +
            "\nℹ️ 指令回覆免費（Reply API）\n" +
            "僅排程推播計額度（Push API）");
    }

    // ==================== Signature Verification ====================

    private static bool VerifySignature(string body, string? signature, string channelSecret)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))) == signature;
    }
}
