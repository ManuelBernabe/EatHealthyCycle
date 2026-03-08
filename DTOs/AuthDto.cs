namespace EatHealthyCycle.DTOs;

// Registration
public record RegisterRequest(string Username, string Email, string Password);
public record RegisterResponse(string Message, string ActivationToken);
public record ResendActivationRequest(string Email);

// Login
public record LoginRequest(string Username, string Password);
public record AuthResponse(string AccessToken, string RefreshToken, UserInfoDto User);
public record RefreshRequest(string RefreshToken);
public record UserInfoDto(int Id, string Username, string Email, string Role, bool IsActive);

// 2FA
public record TwoFactorLoginResponse(bool Requires2FA, string TempToken);
public record Verify2FARequest(string TempToken, string Code);
public record TwoFactorSetupResponse(string Secret, string OtpAuthUri);
public record Confirm2FARequest(string Code);
public record Disable2FARequest(string Password);
public record TwoFactorStatusResponse(bool Enabled);
public record TwoFactorConfirmResponse(string[] RecoveryCodes);

// User Management (Admin)
public record CreateUserRequest(string Username, string Email, string Password, string Role);
public record UpdateUserRequest(string? Username, string? Email, string? Role, bool? IsActive);
public record ResetPasswordRequest(string NewPassword);

// Self-service profile
public record UpdateProfileRequest(string? Username = null, string? Email = null);
public record ChangeMyPasswordRequest(string CurrentPassword, string NewPassword);
