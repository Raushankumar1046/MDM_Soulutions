namespace EndpointManager.Server.Services;

using System.Net;
using System.Net.Mail;
using LiteDB;

public class SmtpSettingsRecord
{
    [BsonId]
    public string Id { get; set; } = "default";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string FromEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = "CyberShield MDM";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class EmailService
{
    private readonly LiteDbService _dbService;
    private readonly ILogger<EmailService> _logger;
    private readonly ILiteCollection<SmtpSettingsRecord> _smtpCollection;

    public EmailService(LiteDbService dbService, ILogger<EmailService> logger)
    {
        _dbService = dbService;
        _logger = logger;
        _smtpCollection = _dbService.Database.GetCollection<SmtpSettingsRecord>("smtp_settings");
    }

    public SmtpSettingsRecord GetSettings()
    {
        var settings = _smtpCollection.FindById("default");
        if (settings == null)
        {
            settings = new SmtpSettingsRecord
            {
                Id = "default",
                Host = "",
                Port = 587,
                EnableSsl = true,
                FromEmail = "",
                SenderName = "CyberShield MDM",
                Username = "",
                Password = ""
            };
            _smtpCollection.Insert(settings);
        }
        return settings;
    }

    public void SaveSettings(SmtpSettingsRecord newSettings)
    {
        newSettings.Id = "default";
        newSettings.UpdatedAtUtc = DateTime.UtcNow;
        _smtpCollection.Upsert(newSettings);
        _logger.LogInformation("SMTP Settings successfully updated for host: {Host}:{Port}", newSettings.Host, newSettings.Port);
    }

    public async Task<(bool Success, string Message)> SendInviteEmailAsync(string toEmail, string recipientName, string inviteUrl)
    {
        var settings = GetSettings();
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.FromEmail))
        {
            return (false, "SMTP server is not configured yet. Please configure SMTP in Settings or share the invitation link manually.");
        }

        string subject = "🔐 You've Been Invited to CyberShield MDM (Account & 2FA Setup)";
        string body = $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <style>
    body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #0b0f19; color: #e2e8f0; margin: 0; padding: 24px; }}
    .card {{ max-width: 580px; margin: 0 auto; background: #131b2e; border: 1px solid rgba(255,255,255,0.1); border-radius: 12px; padding: 32px; box-shadow: 0 10px 30px rgba(0,0,0,0.5); }}
    .logo {{ color: #38bdf8; font-size: 22px; font-weight: 700; margin-bottom: 20px; }}
    h2 {{ color: #ffffff; font-size: 20px; margin-top: 0; }}
    p {{ color: #94a3b8; font-size: 14px; line-height: 1.6; }}
    .btn {{ display: inline-block; background: linear-gradient(135deg, #0284c7 0%, #2563eb 100%); color: #ffffff !important; text-decoration: none; padding: 12px 28px; font-size: 14px; font-weight: 600; border-radius: 8px; margin: 24px 0; }}
    .note {{ background: rgba(56, 189, 248, 0.08); border-left: 3px solid #38bdf8; padding: 12px 16px; font-size: 13px; color: #cbd5e1; margin-top: 20px; border-radius: 0 6px 6px 0; }}
    .footer {{ margin-top: 30px; font-size: 11px; color: #64748b; border-top: 1px solid rgba(255,255,255,0.06); padding-top: 16px; }}
  </style>
</head>
<body>
  <div class=""card"">
    <div class=""logo"">🛡️ CyberShield MDM</div>
    <h2>Welcome, {WebUtility.HtmlEncode(recipientName)}!</h2>
    <p>You have been granted access to the CyberShield Central Endpoint Management Portal.</p>
    <p>To activate your account and configure <strong>Two-Factor Authentication (2FA)</strong> with your Authenticator app (Google Authenticator, Microsoft Authenticator, or Authy), please click the activation button below:</p>
    
    <div style=""text-align: center;"">
      <a href=""{inviteUrl}"" class=""btn"" target=""_blank"">Activate Account & Setup 2FA</a>
    </div>

    <div class=""note"">
      <strong>Next Steps:</strong><br>
      1. Click the button above to set your password.<br>
      2. Scan the displayed QR Code using your phone's Authenticator App.<br>
      3. Enter the 6-digit verification code to complete activation.
    </div>

    <p style=""font-size: 12px; margin-top: 20px;"">If the button doesn't work, copy and paste this link into your browser:<br>
      <a href=""{inviteUrl}"" style=""color: #38bdf8; word-break: break-all;"">{inviteUrl}</a>
    </p>

    <div class=""footer"">
      This is an automated invitation from CyberShield MDM. If you were not expecting this, please contact your IT Administrator.
    </div>
  </div>
</body>
</html>";

        return await SendHtmlEmailAsync(toEmail, subject, body);
    }

    public async Task<(bool Success, string Message)> SendTestEmailAsync(string toEmail, SmtpSettingsRecord? customSettings = null)
    {
        var activeSettings = customSettings ?? GetSettings();
        string subject = "✅ CyberShield MDM - SMTP Configuration Test Successful";
        string body = $@"
<!DOCTYPE html>
<html>
<body style=""font-family: sans-serif; background-color: #0b0f19; color: #e2e8f0; padding: 20px;"">
  <div style=""max-width: 500px; margin: 0 auto; background: #131b2e; border: 1px solid #10b981; border-radius: 10px; padding: 24px;"">
    <h2 style=""color: #10b981; margin-top: 0;"">✅ SMTP Test Succeeded!</h2>
    <p style=""color: #94a3b8;"">This email confirms that your CyberShield MDM server can successfully send automated emails through your SMTP server ({WebUtility.HtmlEncode(activeSettings.Host)}:{activeSettings.Port}).</p>
    <p style=""font-size: 12px; color: #64748b;"">Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</p>
  </div>
</body>
</html>";

        return await SendHtmlEmailAsync(toEmail, subject, body, customSettings);
    }

    private async Task<(bool Success, string Message)> SendHtmlEmailAsync(string toEmail, string subject, string htmlBody, SmtpSettingsRecord? customSettings = null)
    {
        var settings = customSettings ?? GetSettings();
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return (false, "SMTP Host is missing. Please enter your SMTP server (e.g. smtp.gmail.com).");
        }
        if (string.IsNullOrWhiteSpace(settings.FromEmail))
        {
            return (false, "Sender Email is missing. Please enter your email address.");
        }
        if (settings.Port == 465)
        {
            return (false, "Port 465 uses Implicit SSL which is unsupported by STARTTLS. Please change Port to 587 (with SSL/TLS enabled) for Gmail/Office365.");
        }

        try
        {
            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = 12000 // 12 seconds timeout
            };

            if (!string.IsNullOrWhiteSpace(settings.Username) && !string.IsNullOrWhiteSpace(settings.Password))
            {
                var pass = settings.Password.Trim();
                var cleanedPass = pass.Replace(" ", "");
                if (cleanedPass.Length == 16 && cleanedPass.All(char.IsLetterOrDigit))
                {
                    pass = cleanedPass;
                }
                client.Credentials = new NetworkCredential(settings.Username.Trim(), pass);
            }

            var fromAddress = new MailAddress(settings.FromEmail, settings.SenderName ?? "CyberShield MDM");
            var toAddress = new MailAddress(toEmail);

            using var message = new MailMessage(fromAddress, toAddress)
            {
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };

            await client.SendMailAsync(message);
            _logger.LogInformation("Email sent successfully to {To} with subject '{Subject}'", toEmail, subject);
            return (true, "Email sent successfully.");
        }
        catch (SmtpException smtpEx)
        {
            _logger.LogError(smtpEx, "SMTP Error sending to {To}: {Error}", toEmail, smtpEx.Message);
            var innerMsg = smtpEx.InnerException?.Message;
            var details = !string.IsNullOrEmpty(innerMsg) ? $"{smtpEx.Message} ({innerMsg})" : smtpEx.Message;

            if (details.Contains("5.7.8") || details.Contains("Username and Password not accepted") || details.Contains("Authentication failed") || details.Contains("BadCredentials"))
            {
                return (false, "Google/SMTP Authentication Failed. Make sure you are using a 16-character Google 'App Password' instead of your regular account password.");
            }
            return (false, $"SMTP Error: {details}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}: {Error}", toEmail, ex.Message);
            return (false, $"Email delivery failed: {ex.Message}");
        }
    }
}

