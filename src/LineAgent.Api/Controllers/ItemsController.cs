using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Models.DTOs;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ItemsController : ControllerBase
{
    private readonly IItemService _itemService;
    public ItemsController(IItemService itemService) => _itemService = itemService;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] ItemFilterDto filter) => Ok(await _itemService.GetAllAsync(filter));

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var item = await _itemService.GetByIdAsync(id);
        return item == null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateItemDto dto)
    {
        var item = await _itemService.CreateAsync(dto);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateItemDto dto)
    {
        var item = await _itemService.UpdateAsync(id, dto);
        return item == null ? NotFound() : Ok(item);
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateItemDto dto)
    {
        if (!dto.Status.HasValue) return BadRequest("Status required");
        return await _itemService.UpdateStatusAsync(id, dto.Status.Value) ? Ok() : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id) =>
        await _itemService.DeleteAsync(id) ? NoContent() : NotFound();

    [HttpGet("summary/daily")]
    public async Task<IActionResult> DailySummary([FromQuery] DateTime? date) =>
        Ok(await _itemService.GetDailySummaryAsync(date));
}
