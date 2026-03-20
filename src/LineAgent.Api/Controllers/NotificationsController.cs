using Dapper;
using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Models.Entities;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly ILineMessagingService _lineService;
    private readonly IDbConnectionFactory _dbFactory;

    public NotificationsController(ILineMessagingService lineService, IDbConnectionFactory dbFactory)
    {
        _lineService = lineService;
        _dbFactory = dbFactory;
    }

    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        var ok = await _lineService.SendTestMessageAsync();
        return ok ? Ok(new { message = "Test sent" }) : BadRequest(new { message = "No registered users or LINE config missing." });
    }

    [HttpPost("trigger/daily")]
    public async Task<IActionResult> TriggerDaily()
    {
        await _lineService.SendDailyReminderAsync();
        return Ok(new { message = "Daily reminder triggered" });
    }

    /// <summary>
    /// Broadcast a message to all registered users, optionally filtered by channel.
    /// </summary>
    [HttpPost("broadcast")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Text is required" });

        var (sent, failed) = await _lineService.BroadcastAsync(request.Text, request.ChannelId);
        return Ok(new { sent, failed, total = sent + failed });
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 50)
    {
        using var db = _dbFactory.CreateConnection();
        var logs = await db.QueryAsync<NotificationLog>(
            "SELECT * FROM NotificationLogs ORDER BY SentAt DESC LIMIT @Limit", new { Limit = limit });
        return Ok(logs);
    }
}

public class BroadcastRequest
{
    public string Text { get; set; } = string.Empty;
    public int? ChannelId { get; set; }
}
