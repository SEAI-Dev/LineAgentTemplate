using Microsoft.AspNetCore.Mvc;
using LineAgent.Api.Services;

namespace LineAgent.Api.Controllers;

[ApiController]
[Route("api/admin/registrations")]
public class AdminRegistrationController : ControllerBase
{
    private readonly IRegistrationService _registrationService;

    public AdminRegistrationController(IRegistrationService registrationService) =>
        _registrationService = registrationService;

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? channelId) =>
        Ok(await _registrationService.GetAllAsync(channelId));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Deactivate(int id) =>
        await _registrationService.DeactivateAsync(id)
            ? Ok(new { message = "Registration deactivated" })
            : NotFound();
}
