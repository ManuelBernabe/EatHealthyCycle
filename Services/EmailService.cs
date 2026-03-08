using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using EatHealthyCycle.Services.Interfaces;

namespace EatHealthyCycle.Services;

public class EmailSettings
{
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "EatHealthyCycle";
    public string AppBaseUrl { get; set; } = "http://localhost:8080";
    public string? ResendApiKey { get; set; }
    public string? BrevoApiKey { get; set; }
}

public class EmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public EmailService(EmailSettings settings, ILogger<EmailService> logger, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public async Task SendActivationEmailAsync(string toEmail, string username, string activationUrl)
    {
        var subject = "Activa tu cuenta en EatHealthyCycle";
        var body = $@"
<!DOCTYPE html>
<html>
<head><meta charset='utf-8'></head>
<body style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <div style='background: linear-gradient(135deg, #4CAF50, #8BC34A); padding: 30px; border-radius: 10px; text-align: center;'>
        <h1 style='color: white; margin: 0;'>EatHealthyCycle</h1>
    </div>
    <div style='padding: 30px; background: #f9f9f9; border-radius: 0 0 10px 10px;'>
        <h2>Hola {username},</h2>
        <p>Gracias por registrarte. Para activar tu cuenta, haz clic en el siguiente enlace:</p>
        <div style='text-align: center; margin: 30px 0;'>
            <a href='{activationUrl}' style='background: #4CAF50; color: white; padding: 15px 30px; text-decoration: none; border-radius: 5px; font-size: 16px;'>
                Activar mi cuenta
            </a>
        </div>
        <p style='color: #666; font-size: 14px;'>Este enlace expira en 24 horas.</p>
        <p style='color: #999; font-size: 12px;'>Si no te registraste en EatHealthyCycle, ignora este correo.</p>
    </div>
</body>
</html>";

        await SendEmailAsync(toEmail, subject, body);
    }

    public async Task SendWelcomeEmailAsync(string toEmail, string username)
    {
        var subject = "Bienvenido a EatHealthyCycle";
        var body = $@"
<!DOCTYPE html>
<html>
<head><meta charset='utf-8'></head>
<body style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;'>
    <div style='background: linear-gradient(135deg, #4CAF50, #8BC34A); padding: 30px; border-radius: 10px; text-align: center;'>
        <h1 style='color: white; margin: 0;'>EatHealthyCycle</h1>
    </div>
    <div style='padding: 30px; background: #f9f9f9; border-radius: 0 0 10px 10px;'>
        <h2>Bienvenido {username},</h2>
        <p>Tu cuenta ha sido activada correctamente. Ya puedes empezar a gestionar tus dietas y planes semanales.</p>
        <p>Funcionalidades disponibles:</p>
        <ul>
            <li>Importar dietas desde PDF</li>
            <li>Generar planes semanales</li>
            <li>Controlar tu peso y evolucion</li>
            <li>Lista de la compra automatica</li>
        </ul>
    </div>
</body>
</html>";

        await SendEmailAsync(toEmail, subject, body);
    }

    private async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        // Try Resend first
        if (!string.IsNullOrEmpty(_settings.ResendApiKey))
        {
            try
            {
                await SendViaResendAsync(toEmail, subject, htmlBody);
                _logger.LogInformation("Email sent via Resend to {Email}", toEmail);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resend failed, trying next provider");
            }
        }

        // Try Brevo
        if (!string.IsNullOrEmpty(_settings.BrevoApiKey))
        {
            try
            {
                await SendViaBrevoAsync(toEmail, subject, htmlBody);
                _logger.LogInformation("Email sent via Brevo to {Email}", toEmail);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Brevo failed, trying SMTP");
            }
        }

        // Fallback to SMTP
        if (!string.IsNullOrEmpty(_settings.SmtpUser))
        {
            await SendViaSmtpAsync(toEmail, subject, htmlBody);
            _logger.LogInformation("Email sent via SMTP to {Email}", toEmail);
            return;
        }

        _logger.LogWarning("No email provider configured. Email to {Email} not sent. Subject: {Subject}", toEmail, subject);
    }

    private async Task SendViaResendAsync(string toEmail, string subject, string htmlBody)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ResendApiKey);

        var payload = new
        {
            from = $"{_settings.FromName} <{_settings.FromEmail}>",
            to = new[] { toEmail },
            subject,
            html = htmlBody
        };

        var response = await client.PostAsync("https://api.resend.com/emails",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task SendViaBrevoAsync(string toEmail, string subject, string htmlBody)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", _settings.BrevoApiKey);

        var payload = new
        {
            sender = new { name = _settings.FromName, email = _settings.FromEmail },
            to = new[] { new { email = toEmail } },
            subject,
            htmlContent = htmlBody
        };

        var response = await client.PostAsync("https://api.brevo.com/v3/smtp/email",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task SendViaSmtpAsync(string toEmail, string subject, string htmlBody)
    {
        using var message = new MailMessage();
        message.From = new MailAddress(_settings.FromEmail, _settings.FromName);
        message.To.Add(toEmail);
        message.Subject = subject;
        message.Body = htmlBody;
        message.IsBodyHtml = true;

        using var smtp = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort);
        smtp.Credentials = new NetworkCredential(_settings.SmtpUser, _settings.SmtpPassword);
        smtp.EnableSsl = true;
        await smtp.SendMailAsync(message);
    }
}
