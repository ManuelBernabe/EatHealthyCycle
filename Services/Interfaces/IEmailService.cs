namespace EatHealthyCycle.Services.Interfaces;

public interface IEmailService
{
    Task SendWelcomeEmailAsync(string toEmail, string username);
    Task SendActivationEmailAsync(string toEmail, string username, string activationUrl);
}
