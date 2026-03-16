using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

/// <summary>
/// LINE Webhook receiver + command router.
/// All command responses use Reply API (free, no quota cost).
/// Add your own commands by adding cases to HandleTextMessageAsync.
/// </summary>
[ApiController]
[Route("api/line")]
public class LineWebhookController : ControllerBase
{
    private readonly string _channelSecret;
    private readonly IItemService _itemService;
    private readonly ILineMessagingService _lineService;
    private readonly ILogger<LineWebhookController> _logger;

    public LineWebhookController(
        IConfiguration config,
        IItemService itemService,
        ILineMessagingService lineService,
        ILogger<LineWebhookController> logger)
    {
        _channelSecret = config["Line:ChannelSecret"]
            ?? Environment.GetEnvironmentVariable("LINE_CHANNEL_SECRET")
            ?? throw new InvalidOperationException("LINE Channel Secret not configured");
        _itemService = itemService;
        _lineService = lineService;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var signature = Request.Headers["X-Line-Signature"].FirstOrDefault();
        if (!VerifySignature(body, signature))
            return Unauthorized();

        var doc = JsonDocument.Parse(body);
        foreach (var evt in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            if (evt.GetProperty("type").GetString() != "message") continue;
            if (evt.GetProperty("message").GetProperty("type").GetString() != "text") continue;

            var text = evt.GetProperty("message").GetProperty("text").GetString()!;
            var replyToken = evt.GetProperty("replyToken").GetString()!;
            await HandleTextMessageAsync(text.Trim(), replyToken);
        }

        return Ok();
    }

    // ==================== Command Router ====================
    // Add your own commands here. All responses use Reply (free).

    private async Task HandleTextMessageAsync(string text, string replyToken)
    {
        if (text.StartsWith("/add ", StringComparison.OrdinalIgnoreCase))
            await HandleAddAsync(replyToken, text[5..].Trim());

        else if (text.Equals("/list", StringComparison.OrdinalIgnoreCase))
            await HandleListAsync(replyToken);

        else if (text.StartsWith("/done ", StringComparison.OrdinalIgnoreCase))
            await HandleDoneAsync(replyToken, text[6..].Trim());

        else if (text.Equals("/today", StringComparison.OrdinalIgnoreCase))
            await HandleTodayAsync(replyToken);

        else if (text.Equals("/quota", StringComparison.OrdinalIgnoreCase))
            await HandleQuotaAsync(replyToken);

        else if (text.Equals("/help", StringComparison.OrdinalIgnoreCase))
            await HandleHelpAsync(replyToken);

        else if (text.StartsWith("/"))
            await _lineService.ReplyTextMessageAsync(replyToken,
                $"Unknown command: {text.Split(' ')[0]}\nType /help for available commands");
    }

    // ==================== Command Handlers ====================

    // /add [!1|!2|!3] title [@category]
    private async Task HandleAddAsync(string replyToken, string input)
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
            await _lineService.ReplyTextMessageAsync(replyToken, "Usage: /add [!1|!2|!3] title [@category]");
            return;
        }

        var item = await _itemService.CreateAsync(new CreateItemDto
        {
            Title = title, Priority = priority, Category = category, DueDate = DateTime.Today
        });

        var pLabel = priority == 1 ? "🔴" : priority == 2 ? "🟡" : "⚪";
        var cLabel = category != null ? $" @{category}" : "";
        await _lineService.ReplyTextMessageAsync(replyToken, $"✅ #{item.Id} {pLabel} {title}{cLabel}");
    }

    private async Task HandleListAsync(string replyToken)
    {
        var items = (await _itemService.GetAllAsync(new ItemFilterDto()))
            .Where(i => i.Status < 2).ToList();

        if (!items.Any())
        {
            await _lineService.ReplyTextMessageAsync(replyToken, "📋 No pending items!");
            return;
        }

        var lines = new List<string> { $"📋 Items ({items.Count})", "━━━━━━━━━━━━" };
        foreach (var t in items.Take(20))
        {
            var p = t.Priority == 1 ? "🔴" : t.Priority == 2 ? "🟡" : "⚪";
            var s = t.Status == 1 ? "🔄" : "⬜";
            lines.Add($"{s}{p} #{t.Id} {t.Title}");
        }
        if (items.Count > 20) lines.Add($"... +{items.Count - 20} more");

        await _lineService.ReplyTextMessageAsync(replyToken, string.Join("\n", lines));
    }

    private async Task HandleDoneAsync(string replyToken, string input)
    {
        var ids = input.Split(',', ' ')
            .Select(s => s.Trim().TrimStart('#'))
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse).ToList();

        if (!ids.Any())
        {
            await _lineService.ReplyTextMessageAsync(replyToken, "Usage: /done 1 or /done 1,2,3");
            return;
        }

        var results = new List<string>();
        foreach (var id in ids)
        {
            var ok = await _itemService.UpdateStatusAsync(id, 2);
            var item = ok ? await _itemService.GetByIdAsync(id) : null;
            results.Add(ok ? $"  ✅ #{id} {item?.Title}" : $"  ❌ #{id} not found");
        }
        await _lineService.ReplyTextMessageAsync(replyToken, string.Join("\n", results));
    }

    private async Task HandleTodayAsync(string replyToken)
    {
        var s = await _itemService.GetDailySummaryAsync();
        var lines = new List<string> { $"📋 {s.Date:M/d} Summary", "━━━━━━━━━━━━" };
        if (s.HighPriorityItems.Any())
        {
            lines.Add("🔴 High Priority");
            foreach (var t in s.HighPriorityItems) lines.Add($"  • {t.Title}");
        }
        if (s.DueTodayItems.Any())
        {
            lines.Add("📅 Due Today");
            foreach (var t in s.DueTodayItems) lines.Add($"  • {t.Title}");
        }
        lines.Add($"\nPending: {s.TotalPending} | InProgress: {s.InProgress} | Overdue: {s.Overdue}");
        await _lineService.ReplyTextMessageAsync(replyToken, string.Join("\n", lines));
    }

    private async Task HandleQuotaAsync(string replyToken)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _lineService.GetChannelAccessToken());

        var qr = JsonDocument.Parse(await http.GetStringAsync("https://api.line.me/v2/bot/message/quota"));
        var ur = JsonDocument.Parse(await http.GetStringAsync("https://api.line.me/v2/bot/message/quota/consumption"));

        var type = qr.RootElement.GetProperty("type").GetString();
        var limit = type == "limited" ? qr.RootElement.GetProperty("value").GetInt64() : -1;
        var used = ur.RootElement.GetProperty("totalUsage").GetInt64();

        var lines = new List<string> { "📨 LINE Message Quota", "━━━━━━━━━━━━" };
        if (limit > 0)
        {
            var pct = Math.Round((double)used / limit * 100, 1);
            lines.Add($"Limit: {limit}/month");
            lines.Add($"Used: {used} ({pct}%)");
            lines.Add($"Remaining: {limit - used}");
            lines.Add($"[{new string('█', (int)(pct / 5))}{new string('░', 20 - (int)(pct / 5))}]");
            lines.Add("📌 Only scheduled push counts. Command replies are free.");
        }
        else
        {
            lines.Add($"Plan: Unlimited");
            lines.Add($"Used: {used}");
        }
        await _lineService.ReplyTextMessageAsync(replyToken, string.Join("\n", lines));
    }

    private async Task HandleHelpAsync(string replyToken)
    {
        await _lineService.ReplyTextMessageAsync(replyToken, """
            🤖 LINE Agent Commands
            ━━━━━━━━━━━━━━━
            /add title          — Add item
            /add !1 title       — Add high priority
            /add title @cat     — Add with category
            /list               — List pending items
            /done 1             — Complete #1
            /done 1,2,3         — Complete multiple
            /today              — Today's summary
            /quota              — Message quota
            /help               — This help

            ℹ️ Command replies are free (Reply API).
            Only scheduled notifications cost quota (Push API).
            """);
    }

    private bool VerifySignature(string body, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_channelSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))) == signature;
    }
}
