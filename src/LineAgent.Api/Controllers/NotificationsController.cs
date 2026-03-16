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
        return ok ? Ok(new { message = "Test sent" }) : BadRequest(new { message = "Failed. Check LINE config." });
    }

    [HttpPost("trigger/daily")]
    public async Task<IActionResult> TriggerDaily()
    {
        await _lineService.SendDailyReminderAsync();
        return Ok(new { message = "Daily reminder triggered" });
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
