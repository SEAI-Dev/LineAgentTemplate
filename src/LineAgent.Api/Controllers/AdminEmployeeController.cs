using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

[ApiController]
[Route("api/admin/employees")]
public class AdminEmployeeController : ControllerBase
{
    private readonly IEmployeeSyncService _syncService;

    public AdminEmployeeController(IEmployeeSyncService syncService) => _syncService = syncService;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? branchNo, [FromQuery] int? departmentId) =>
        Ok(await _syncService.GetAllAsync(branchNo, departmentId));

    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
    {
        try
        {
            var count = await _syncService.SyncFromSeosApiAsync();
            return Ok(new { message = $"Synced {count} employees", count });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
