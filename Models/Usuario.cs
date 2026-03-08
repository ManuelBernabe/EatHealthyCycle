namespace EatHealthyCycle.Models;

public class Usuario
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Standard;
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }

    // Refresh Token
    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }

    // Email Activation
    public string? ActivationToken { get; set; }
    public DateTime? ActivationTokenExpiresAt { get; set; }

    // Two-Factor Authentication
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecret { get; set; }
    public string? RecoveryCodes { get; set; }

    // Navigation
    public List<Dieta> Dietas { get; set; } = new();
    public List<RegistroPeso> RegistrosPeso { get; set; } = new();
    public List<PlanSemanal> PlanesSemanal { get; set; } = new();
}
