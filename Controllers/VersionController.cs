using Microsoft.AspNetCore.Mvc;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("api")]
public class VersionController : ControllerBase
{
    // Increment this on each deploy to trigger update notification
    private const string AppVersion = "1.0.27";

    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var env = Environment.GetEnvironmentVariable("APP_ENV") ?? "production";
        return Ok(new { version = AppVersion, env });
    }
}
