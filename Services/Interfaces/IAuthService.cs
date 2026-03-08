using EatHealthyCycle.DTOs;
using EatHealthyCycle.Models;

namespace EatHealthyCycle.Services.Interfaces;

public interface IAuthService
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest request);
    Task<object> LoginAsync(LoginRequest request);
    Task<AuthResponse> RefreshTokenAsync(RefreshRequest request);
    Task<UserInfoDto> GetUserInfoAsync(int userId);
    Task<List<UserInfoDto>> GetAllUsersAsync();
    Task<UserInfoDto> CreateUserAsync(CreateUserRequest request);
    Task UpdateUserAsync(int id, UpdateUserRequest request);
    Task DeleteUserAsync(int id, int currentUserId);
    Task ResetPasswordAsync(int id, string newPassword);
    Task<AuthResponse> ImpersonateAsync(int userId);
    Task<string> ActivateAsync(string token);
    Task ResendActivationAsync(string email);
    Task<AuthResponse> Verify2FAAsync(Verify2FARequest request);
    TwoFactorSetupResponse Setup2FA(Usuario user);
    TwoFactorConfirmResponse Confirm2FA(Usuario user, string code);
    bool Disable2FA(Usuario user, string password);
    Task UpdateProfileAsync(int userId, UpdateProfileRequest request);
    Task ChangePasswordAsync(int userId, ChangeMyPasswordRequest request);
}
