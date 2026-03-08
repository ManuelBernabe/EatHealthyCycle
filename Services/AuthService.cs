using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using EatHealthyCycle.Data;
using EatHealthyCycle.DTOs;
using EatHealthyCycle.Models;
using EatHealthyCycle.Services.Interfaces;
using OtpNet;

namespace EatHealthyCycle.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly JwtSettings _jwt;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;

    public AuthService(AppDbContext db, JwtSettings jwt, IEmailService email, IConfiguration config)
    {
        _db = db;
        _jwt = jwt;
        _email = email;
        _config = config;
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request)
    {
        ValidatePasswordStrength(request.Password);

        if (request.Username.Length < 3)
            throw new ArgumentException("El nombre de usuario debe tener al menos 3 caracteres");

        if (await _db.Usuarios.AnyAsync(u => u.Username == request.Username))
            throw new ArgumentException("El nombre de usuario ya existe");

        if (await _db.Usuarios.AnyAsync(u => u.Email == request.Email))
            throw new ArgumentException("El email ya está registrado");

        var activationToken = GenerateRandomToken();

        var usuario = new Usuario
        {
            Username = request.Username,
            Nombre = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = UserRole.Standard,
            IsActive = false,
            ActivationToken = activationToken,
            ActivationTokenExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync();

        var baseUrl = _config["Email:AppBaseUrl"] ?? "http://localhost:8080";
        var activationUrl = $"{baseUrl}/auth/activate?token={activationToken}";

        _ = Task.Run(async () =>
        {
            try { await _email.SendActivationEmailAsync(request.Email, request.Username, activationUrl); }
            catch { /* log but don't fail registration */ }
        });

        return new RegisterResponse("Registro exitoso. Revisa tu email para activar la cuenta.", activationToken);
    }

    public async Task<object> LoginAsync(LoginRequest request)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Username == request.Username)
            ?? throw new ArgumentException("Credenciales inválidas");

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new ArgumentException("Credenciales inválidas");

        if (!user.IsActive)
            throw new ArgumentException("La cuenta no está activada. Revisa tu email.");

        if (user.TwoFactorEnabled)
        {
            var tempToken = GenerateTempToken(user);
            return new TwoFactorLoginResponse(true, tempToken);
        }

        return await GenerateAuthResponseAsync(user);
    }

    public async Task<AuthResponse> RefreshTokenAsync(RefreshRequest request)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.RefreshToken == request.RefreshToken)
            ?? throw new ArgumentException("Refresh token inválido");

        if (user.RefreshTokenExpiresAt < DateTime.UtcNow)
            throw new ArgumentException("Refresh token expirado");

        if (!user.IsActive)
            throw new ArgumentException("Cuenta desactivada");

        return await GenerateAuthResponseAsync(user);
    }

    public async Task<UserInfoDto> GetUserInfoAsync(int userId)
    {
        var user = await _db.Usuarios.FindAsync(userId)
            ?? throw new ArgumentException("Usuario no encontrado");
        return MapToUserInfo(user);
    }

    public async Task<List<UserInfoDto>> GetAllUsersAsync()
    {
        return await _db.Usuarios
            .OrderBy(u => u.Username)
            .Select(u => new UserInfoDto(u.Id, u.Username, u.Email, u.Role.ToString(), u.IsActive))
            .ToListAsync();
    }

    public async Task<UserInfoDto> CreateUserAsync(CreateUserRequest request)
    {
        ValidatePasswordStrength(request.Password);

        if (await _db.Usuarios.AnyAsync(u => u.Username == request.Username))
            throw new ArgumentException("El nombre de usuario ya existe");

        if (await _db.Usuarios.AnyAsync(u => u.Email == request.Email))
            throw new ArgumentException("El email ya está registrado");

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
            throw new ArgumentException("Rol inválido");

        var usuario = new Usuario
        {
            Username = request.Username,
            Nombre = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = role,
            IsActive = true
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync();

        return MapToUserInfo(usuario);
    }

    public async Task UpdateUserAsync(int id, UpdateUserRequest request)
    {
        var user = await _db.Usuarios.FindAsync(id)
            ?? throw new ArgumentException("Usuario no encontrado");

        if (request.Username != null)
        {
            if (await _db.Usuarios.AnyAsync(u => u.Username == request.Username && u.Id != id))
                throw new ArgumentException("El nombre de usuario ya existe");
            user.Username = request.Username;
            user.Nombre = request.Username;
        }

        if (request.Email != null)
        {
            if (await _db.Usuarios.AnyAsync(u => u.Email == request.Email && u.Id != id))
                throw new ArgumentException("El email ya está registrado");
            user.Email = request.Email;
        }

        if (request.Role != null && Enum.TryParse<UserRole>(request.Role, true, out var role))
            user.Role = role;

        if (request.IsActive.HasValue)
            user.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteUserAsync(int id, int currentUserId)
    {
        if (id == currentUserId)
            throw new ArgumentException("No puedes eliminarte a ti mismo");

        var user = await _db.Usuarios.FindAsync(id)
            ?? throw new ArgumentException("Usuario no encontrado");

        _db.Usuarios.Remove(user);
        await _db.SaveChangesAsync();
    }

    public async Task ResetPasswordAsync(int id, string newPassword)
    {
        ValidatePasswordStrength(newPassword);

        var user = await _db.Usuarios.FindAsync(id)
            ?? throw new ArgumentException("Usuario no encontrado");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.RefreshToken = null;
        await _db.SaveChangesAsync();
    }

    public async Task<AuthResponse> ImpersonateAsync(int userId)
    {
        var user = await _db.Usuarios.FindAsync(userId)
            ?? throw new ArgumentException("Usuario no encontrado");
        return await GenerateAuthResponseAsync(user);
    }

    public async Task<string> ActivateAsync(string token)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.ActivationToken == token);

        if (user == null)
            return GenerateActivationHtml(false, "Token de activación inválido.");

        if (user.ActivationTokenExpiresAt < DateTime.UtcNow)
            return GenerateActivationHtml(false, "El token de activación ha expirado. Solicita uno nuevo.");

        user.IsActive = true;
        user.ActivationToken = null;
        user.ActivationTokenExpiresAt = null;
        await _db.SaveChangesAsync();

        _ = Task.Run(async () =>
        {
            try { await _email.SendWelcomeEmailAsync(user.Email, user.Username); }
            catch { }
        });

        return GenerateActivationHtml(true, $"¡Cuenta activada correctamente, {user.Username}! Ya puedes iniciar sesión.");
    }

    public async Task ResendActivationAsync(string email)
    {
        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || user.IsActive) return; // Silent - prevent email enumeration

        user.ActivationToken = GenerateRandomToken();
        user.ActivationTokenExpiresAt = DateTime.UtcNow.AddHours(24);
        await _db.SaveChangesAsync();

        var baseUrl = _config["Email:AppBaseUrl"] ?? "http://localhost:8080";
        var activationUrl = $"{baseUrl}/auth/activate?token={user.ActivationToken}";

        await _email.SendActivationEmailAsync(email, user.Username, activationUrl);
    }

    public async Task<AuthResponse> Verify2FAAsync(Verify2FARequest request)
    {
        var principal = ValidateTempToken(request.TempToken);
        var userId = int.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        var user = await _db.Usuarios.FindAsync(userId)
            ?? throw new ArgumentException("Usuario no encontrado");

        if (!user.TwoFactorEnabled || user.TwoFactorSecret == null)
            throw new ArgumentException("2FA no está habilitado");

        // Try TOTP code first
        var totp = new Totp(Base32Encoding.ToBytes(user.TwoFactorSecret));
        if (totp.VerifyTotp(request.Code, out _, new VerificationWindow(1)))
            return await GenerateAuthResponseAsync(user);

        // Try recovery code
        if (TryUseRecoveryCode(user, request.Code))
        {
            await _db.SaveChangesAsync();
            return await GenerateAuthResponseAsync(user);
        }

        throw new ArgumentException("Código 2FA inválido");
    }

    public TwoFactorSetupResponse Setup2FA(Usuario user)
    {
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);
        user.TwoFactorSecret = secret;

        var otpAuthUri = $"otpauth://totp/EatHealthyCycle:{user.Email}?secret={secret}&issuer=EatHealthyCycle&digits=6";
        return new TwoFactorSetupResponse(secret, otpAuthUri);
    }

    public TwoFactorConfirmResponse Confirm2FA(Usuario user, string code)
    {
        if (user.TwoFactorSecret == null)
            throw new ArgumentException("Primero configura 2FA con /me/2fa/setup");

        var totp = new Totp(Base32Encoding.ToBytes(user.TwoFactorSecret));
        if (!totp.VerifyTotp(code, out _, new VerificationWindow(1)))
            throw new ArgumentException("Código inválido");

        user.TwoFactorEnabled = true;

        // Generate recovery codes
        var codes = new string[8];
        for (int i = 0; i < 8; i++)
            codes[i] = GenerateRecoveryCode();

        var hashedCodes = codes.Select(c => BCrypt.Net.BCrypt.HashPassword(c)).ToArray();
        user.RecoveryCodes = JsonSerializer.Serialize(hashedCodes);

        return new TwoFactorConfirmResponse(codes);
    }

    public bool Disable2FA(Usuario user, string password)
    {
        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return false;

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.RecoveryCodes = null;
        return true;
    }

    public async Task UpdateProfileAsync(int userId, UpdateProfileRequest request)
    {
        var user = await _db.Usuarios.FindAsync(userId)
            ?? throw new ArgumentException("Usuario no encontrado");

        if (request.Username != null)
        {
            if (await _db.Usuarios.AnyAsync(u => u.Username == request.Username && u.Id != userId))
                throw new ArgumentException("El nombre de usuario ya existe");
            user.Username = request.Username;
            user.Nombre = request.Username;
        }

        if (request.Email != null)
        {
            if (await _db.Usuarios.AnyAsync(u => u.Email == request.Email && u.Id != userId))
                throw new ArgumentException("El email ya está registrado");
            user.Email = request.Email;
        }

        await _db.SaveChangesAsync();
    }

    public async Task ChangePasswordAsync(int userId, ChangeMyPasswordRequest request)
    {
        ValidatePasswordStrength(request.NewPassword);

        var user = await _db.Usuarios.FindAsync(userId)
            ?? throw new ArgumentException("Usuario no encontrado");

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            throw new ArgumentException("Contraseña actual incorrecta");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        user.RefreshToken = null;
        await _db.SaveChangesAsync();
    }

    // --- Private helpers ---

    private async Task<AuthResponse> GenerateAuthResponseAsync(Usuario user)
    {
        var accessToken = GenerateAccessToken(user);
        var refreshToken = GenerateRandomToken();

        user.RefreshToken = refreshToken;
        user.RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenExpirationDays);
        await _db.SaveChangesAsync();

        return new AuthResponse(accessToken, refreshToken, MapToUserInfo(user));
    }

    private string GenerateAccessToken(Usuario user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpirationMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateTempToken(Usuario user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("purpose", "2fa")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClaimsPrincipal ValidateTempToken(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SecretKey));

        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwt.Issuer,
            ValidAudience = _jwt.Audience,
            IssuerSigningKey = key
        }, out _);

        if (principal.FindFirst("purpose")?.Value != "2fa")
            throw new ArgumentException("Token temporal inválido");

        return principal;
    }

    private static string GenerateRandomToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string GenerateRecoveryCode()
    {
        return $"{RandomNumberGenerator.GetInt32(10000000, 99999999):D8}";
    }

    private bool TryUseRecoveryCode(Usuario user, string code)
    {
        if (user.RecoveryCodes == null) return false;

        var hashedCodes = JsonSerializer.Deserialize<string[]>(user.RecoveryCodes);
        if (hashedCodes == null) return false;

        for (int i = 0; i < hashedCodes.Length; i++)
        {
            if (BCrypt.Net.BCrypt.Verify(code, hashedCodes[i]))
            {
                hashedCodes[i] = "USED";
                user.RecoveryCodes = JsonSerializer.Serialize(hashedCodes);
                return true;
            }
        }
        return false;
    }

    private static UserInfoDto MapToUserInfo(Usuario user)
    {
        return new UserInfoDto(user.Id, user.Username, user.Email, user.Role.ToString(), user.IsActive);
    }

    private static void ValidatePasswordStrength(string password)
    {
        if (password.Length < 8)
            throw new ArgumentException("La contraseña debe tener al menos 8 caracteres");
        if (!password.Any(char.IsUpper))
            throw new ArgumentException("La contraseña debe tener al menos una mayúscula");
        if (!password.Any(char.IsLower))
            throw new ArgumentException("La contraseña debe tener al menos una minúscula");
        if (!password.Any(char.IsDigit))
            throw new ArgumentException("La contraseña debe tener al menos un dígito");
        if (!password.Any(c => !char.IsLetterOrDigit(c)))
            throw new ArgumentException("La contraseña debe tener al menos un carácter especial");
    }

    private static string GenerateActivationHtml(bool success, string message)
    {
        var color = success ? "#4CAF50" : "#f44336";
        var icon = success ? "&#10004;" : "&#10008;";
        return $@"
<!DOCTYPE html>
<html>
<head><meta charset='utf-8'><title>Activación - EatHealthyCycle</title></head>
<body style='font-family: Arial, sans-serif; display: flex; justify-content: center; align-items: center; min-height: 100vh; margin: 0; background: #f0f0f0;'>
    <div style='background: white; padding: 40px; border-radius: 10px; text-align: center; box-shadow: 0 2px 10px rgba(0,0,0,0.1);'>
        <div style='font-size: 60px; color: {color};'>{icon}</div>
        <h2 style='color: {color};'>{(success ? "Cuenta Activada" : "Error")}</h2>
        <p>{message}</p>
    </div>
</body>
</html>";
    }
}
