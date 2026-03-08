using Microsoft.AspNetCore.Mvc;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api")]
public class VersionController : ControllerBase
{
    // Increment this on each deploy to trigger update notification
    private const string AppVersion = "1.0.11";

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        return Ok(new { version = AppVersion });
    }
}
