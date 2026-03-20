using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

[ApiController]
[Route("api/admin/channels")]
public class AdminChannelController : ControllerBase
{
    private readonly IChannelService _channelService;

    public AdminChannelController(IChannelService channelService) => _channelService = channelService;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _channelService.GetChannelInfosAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var channel = await _channelService.GetByIdAsync(id);
        return channel == null ? NotFound() : Ok(channel);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateChannelDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.WebhookPath))
            return BadRequest(new { error = "WebhookPath is required" });

        // Check duplicate path
        var existing = await _channelService.GetByWebhookPathAsync(dto.WebhookPath);
        if (existing != null)
            return Conflict(new { error = $"WebhookPath '{dto.WebhookPath}' already exists" });

        var channel = await _channelService.CreateAsync(dto);
        return CreatedAtAction(nameof(GetById), new { id = channel.Id }, channel);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateChannelDto dto)
    {
        var channel = await _channelService.UpdateAsync(id, dto);
        return channel == null ? NotFound() : Ok(channel);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id) =>
        await _channelService.DeleteAsync(id) ? Ok(new { message = "Channel deactivated" }) : NotFound();
}
