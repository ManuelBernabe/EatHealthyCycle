using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly AppDbContext _db;

    public AuthController(IAuthService auth, AppDbContext db)
    {
        _auth = auth;
        _db = db;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        try
        {
            var result = await _auth.RegisterAsync(request);
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        try
        {
            var result = await _auth.LoginAsync(request);
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("activate")]
    public async Task<IActionResult> Activate([FromQuery] string token)
    {
        var html = await _auth.ActivateAsync(token);
        return Content(html, "text/html");
    }

    [HttpPost("resend-activation")]
    public async Task<IActionResult> ResendActivation(ResendActivationRequest request)
    {
        await _auth.ResendActivationAsync(request.Email);
        return Ok(new { message = "Si el email existe, se ha enviado un nuevo enlace de activación." });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshRequest request)
    {
        try
        {
            var result = await _auth.RefreshTokenAsync(request);
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("verify-2fa")]
    public async Task<IActionResult> Verify2FA(Verify2FARequest request)
    {
        try
        {
            var result = await _auth.Verify2FAAsync(request);
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = GetUserId();
        var info = await _auth.GetUserInfoAsync(userId);
        return Ok(info);
    }

    [Authorize(Policy = "SuperUserMasterOnly")]
    [HttpPost("impersonate/{userId}")]
    public async Task<IActionResult> Impersonate(int userId)
    {
        try
        {
            var result = await _auth.ImpersonateAsync(userId);
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private int GetUserId() =>
        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}

[ApiController]
[Route("users")]
[Authorize(Policy = "AdminOrAbove")]
public class UsersController : ControllerBase
{
    private readonly IAuthService _auth;

    public UsersController(IAuthService auth)
    {
        _auth = auth;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var users = await _auth.GetAllUsersAsync();
        return Ok(users);
    }

    [Authorize(Policy = "SuperUserMasterOnly")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request)
    {
        try
        {
            var user = await _auth.CreateUserAsync(request);
            return CreatedAtAction(nameof(GetAll), user);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [Authorize(Policy = "SuperUserMasterOnly")]
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateUserRequest request)
    {
        try
        {
            await _auth.UpdateUserAsync(id, request);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [Authorize(Policy = "SuperUserMasterOnly")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var currentUserId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            await _auth.DeleteUserAsync(id, currentUserId);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [Authorize(Policy = "SuperUserMasterOnly")]
    [HttpPut("{id}/password")]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordRequest request)
    {
        try
        {
            await _auth.ResetPasswordAsync(id, request.NewPassword);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}

[ApiController]
[Route("me")]
[Authorize]
public class ProfileController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly AppDbContext _db;

    public ProfileController(IAuthService auth, AppDbContext db)
    {
        _auth = auth;
        _db = db;
    }

    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request)
    {
        try
        {
            await _auth.UpdateProfileAsync(GetUserId(), request);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword(ChangeMyPasswordRequest request)
    {
        try
        {
            await _auth.ChangePasswordAsync(GetUserId(), request);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("2fa/status")]
    public async Task<IActionResult> Get2FAStatus()
    {
        var user = await _db.Usuarios.FindAsync(GetUserId());
        return Ok(new TwoFactorStatusResponse(user!.TwoFactorEnabled));
    }

    [HttpPost("2fa/setup")]
    public async Task<IActionResult> Setup2FA()
    {
        var user = await _db.Usuarios.FindAsync(GetUserId());
        var result = _auth.Setup2FA(user!);
        await _db.SaveChangesAsync();
        return Ok(result);
    }

    [HttpPost("2fa/confirm")]
    public async Task<IActionResult> Confirm2FA(Confirm2FARequest request)
    {
        try
        {
            var user = await _db.Usuarios.FindAsync(GetUserId());
            var result = _auth.Confirm2FA(user!, request.Code);
            await _db.SaveChangesAsync();
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("2fa/disable")]
    public async Task<IActionResult> Disable2FA(Disable2FARequest request)
    {
        var user = await _db.Usuarios.FindAsync(GetUserId());
        if (!_auth.Disable2FA(user!, request.Password))
            return BadRequest(new { error = "Contraseña incorrecta" });

        await _db.SaveChangesAsync();
        return Ok(new { message = "2FA desactivado" });
    }

    private int GetUserId() =>
        int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
