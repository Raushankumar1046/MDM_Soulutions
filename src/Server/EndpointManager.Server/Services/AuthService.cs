namespace EndpointManager.Server.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LiteDB;

public class AdminUserRecord
{
    [BsonId]
    public string Username { get; set; } = "admin";
    public string FullName { get; set; } = "Administrator";
    public string Email { get; set; } = "admin@local";
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public string Role { get; set; } = "SuperAdmin"; // SuperAdmin, Admin, Viewer
    public bool IsActive { get; set; } = true;
    public bool Is2FaEnabled { get; set; } = false;
    public string TotpSecret { get; set; } = string.Empty;
    public string? InviteToken { get; set; }
    public DateTime? InviteExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
}

public class ActiveSession
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public DateTime ExpiresAtUtc { get; set; }
}

public class Pending2FaSession
{
    public string TempToken { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public class AuthService
{
    private readonly LiteDbService _dbService;
    private readonly TotpService _totpService;
    private readonly ILogger<AuthService> _logger;
    private readonly ILiteCollection<AdminUserRecord> _usersCollection;
    
    // In-memory fast cache of active tokens: Token -> ActiveSession
    private readonly ConcurrentDictionary<string, ActiveSession> _activeTokens = new();

    // In-memory cache of temporary 2FA verification tokens (valid for 5 minutes)
    private readonly ConcurrentDictionary<string, Pending2FaSession> _pending2FaSessions = new();

    public AuthService(LiteDbService dbService, TotpService totpService, ILogger<AuthService> logger)
    {
        _dbService = dbService;
        _totpService = totpService;
        _logger = logger;
        _usersCollection = _dbService.Database.GetCollection<AdminUserRecord>("admin_users");
        _usersCollection.EnsureIndex(u => u.Username, unique: true);
        _usersCollection.EnsureIndex(u => u.InviteToken);

        InitializeDefaultAdmin();
    }

    private void InitializeDefaultAdmin()
    {
        try
        {
            var admin = _usersCollection.FindById("admin");
            var flagPath = Path.Combine(AppContext.BaseDirectory, "reset_admin.flag");
            if (admin == null || File.Exists(flagPath))
            {
                ResetAdminPassword("admin123");
                if (File.Exists(flagPath))
                {
                    try { File.Delete(flagPath); } catch { }
                }
            }
            else
            {
                // Ensure default admin record has required profile fields populated
                bool updated = false;
                if (string.IsNullOrEmpty(admin.FullName)) { admin.FullName = "Administrator"; updated = true; }
                if (string.IsNullOrEmpty(admin.Role)) { admin.Role = "SuperAdmin"; updated = true; }
                if (updated) _usersCollection.Update(admin);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize default admin user in LiteDB.");
        }
    }

    public void ResetAdminPassword(string newPassword = "admin123")
    {
        try
        {
            var (hash, salt) = HashPassword(newPassword);
            var admin = _usersCollection.FindById("admin");
            if (admin == null)
            {
                admin = new AdminUserRecord
                {
                    Username = "admin",
                    FullName = "Administrator",
                    Email = "admin@local",
                    Role = "SuperAdmin",
                    IsActive = true,
                    Is2FaEnabled = false,
                    PasswordHash = hash,
                    Salt = salt,
                    CreatedAtUtc = DateTime.UtcNow
                };
                _usersCollection.Insert(admin);
            }
            else
            {
                admin.PasswordHash = hash;
                admin.Salt = salt;
                admin.IsActive = true;
                _usersCollection.Update(admin);
            }
            _logger.LogWarning("Admin password has been set to: '{Password}'", newPassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set admin password.");
        }
    }

    public (bool Success, bool Requires2Fa, string? Token, string? TempToken, string? Username, string? Message) Login(string username, string password, string? totpCode = null)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (false, false, null, null, null, "Username and password are required.");
        }

        var cleanUsername = username.Trim().ToLowerInvariant();
        var user = _usersCollection.FindById(cleanUsername);
        if (user == null || !user.IsActive)
        {
            return (false, false, null, null, null, "Invalid username or account is deactivated.");
        }

        if (string.IsNullOrEmpty(user.PasswordHash) || !VerifyPassword(password, user.PasswordHash, user.Salt))
        {
            _logger.LogWarning("Failed login attempt for user: {User}", username);
            return (false, false, null, null, null, "Invalid username or password.");
        }

        // Check if Two-Factor Authentication is enabled for this user
        if (user.Is2FaEnabled && !string.IsNullOrEmpty(user.TotpSecret))
        {
            // If code is not provided in the first step, issue a temporary 5-minute 2FA token
            if (string.IsNullOrWhiteSpace(totpCode))
            {
                var tempTokenBytes = RandomNumberGenerator.GetBytes(24);
                var tempToken = Convert.ToHexString(tempTokenBytes).ToLowerInvariant();
                _pending2FaSessions[tempToken] = new Pending2FaSession
                {
                    TempToken = tempToken,
                    Username = user.Username,
                    ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
                };

                return (false, true, null, tempToken, user.Username, "Two-Factor Authentication required. Enter the 6-digit code from your Authenticator app.");
            }

            // Direct inline verification if code was submitted together
            if (!_totpService.ValidateCode(user.TotpSecret, totpCode))
            {
                return (false, true, null, null, user.Username, "Invalid 6-digit Authenticator code. Please check your app.");
            }
        }

        // Successful authentication: issue full session token
        var sessionToken = CreateUserSession(user);
        return (true, false, sessionToken, null, user.Username, "Login successful.");
    }

    public (bool Success, string? Token, string? Username, string? Role, string? Message) Verify2Fa(string tempToken, string totpCode)
    {
        if (string.IsNullOrWhiteSpace(tempToken) || string.IsNullOrWhiteSpace(totpCode))
        {
            return (false, null, null, null, "Temporary session token and 6-digit code are required.");
        }

        if (!_pending2FaSessions.TryGetValue(tempToken, out var pending) || pending.ExpiresAtUtc < DateTime.UtcNow)
        {
            _pending2FaSessions.TryRemove(tempToken, out _);
            return (false, null, null, null, "2FA session has expired. Please log in again.");
        }

        var user = _usersCollection.FindById(pending.Username);
        if (user == null || !user.IsActive)
        {
            return (false, null, null, null, "User account not found or deactivated.");
        }

        if (!_totpService.ValidateCode(user.TotpSecret, totpCode))
        {
            return (false, null, null, null, "Invalid 6-digit code. Please enter the latest code from your Authenticator app.");
        }

        // Code is valid! Consume temporary session and create full session token
        _pending2FaSessions.TryRemove(tempToken, out _);
        var sessionToken = CreateUserSession(user);

        return (true, sessionToken, user.Username, user.Role, "2FA Verification successful.");
    }

    private string CreateUserSession(AdminUserRecord user)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        var session = new ActiveSession
        {
            Token = token,
            Username = user.Username,
            Role = user.Role,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24) // 24-hour token validity
        };

        _activeTokens[token] = session;

        user.LastLoginUtc = DateTime.UtcNow;
        _usersCollection.Update(user);

        _logger.LogInformation("User '{User}' ({Role}) logged in successfully.", user.Username, user.Role);
        return token;
    }

    public bool ValidateToken(string? token, out string username)
    {
        username = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (_activeTokens.TryGetValue(token, out var session))
        {
            if (session.ExpiresAtUtc > DateTime.UtcNow)
            {
                username = session.Username;
                return true;
            }
            else
            {
                _activeTokens.TryRemove(token, out _);
            }
        }

        return false;
    }

    public void Logout(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _activeTokens.TryRemove(token, out _);
        }
    }

    public (bool Success, string Message) ChangePassword(string username, string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
        {
            return (false, "New password must be at least 6 characters long.");
        }

        var user = _usersCollection.FindById(username.Trim().ToLowerInvariant());
        if (user == null)
        {
            return (false, "User not found.");
        }

        if (!VerifyPassword(currentPassword, user.PasswordHash, user.Salt))
        {
            return (false, "Current password is incorrect.");
        }

        var (newHash, newSalt) = HashPassword(newPassword);
        user.PasswordHash = newHash;
        user.Salt = newSalt;
        _usersCollection.Update(user);

        _logger.LogInformation("Password successfully updated for user: {User}", username);
        return (true, "Password changed successfully.");
    }

    #region User Management & 2FA Setup

    public IEnumerable<object> GetAllUsers()
    {
        return _usersCollection.FindAll().Select(u => new
        {
            u.Username,
            u.FullName,
            u.Email,
            u.Role,
            u.IsActive,
            u.Is2FaEnabled,
            HasPendingInvite = !string.IsNullOrEmpty(u.InviteToken) && (u.InviteExpiresAtUtc == null || u.InviteExpiresAtUtc > DateTime.UtcNow),
            InviteExpiresAtUtc = u.InviteExpiresAtUtc,
            u.CreatedAtUtc,
            u.LastLoginUtc
        });
    }

    public (bool Success, string Message, string? InviteToken, string? TotpSecret, string? QrUri) CreateUserInvite(
        string fullName, string email, string username, string role)
    {
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(username))
        {
            return (false, "Full Name, Email, and Username are required.", null, null, null);
        }

        var cleanUsername = username.Trim().ToLowerInvariant();
        var cleanEmail = email.Trim().ToLowerInvariant();

        if (_usersCollection.FindById(cleanUsername) != null)
        {
            return (false, $"Username '{cleanUsername}' is already taken.", null, null, null);
        }

        if (_usersCollection.FindOne(u => u.Email == cleanEmail) != null)
        {
            return (false, $"Email '{cleanEmail}' is already associated with an account.", null, null, null);
        }

        var totpSecret = _totpService.GenerateSecret();
        var inviteToken = Guid.NewGuid().ToString("N");
        var qrUri = _totpService.GenerateOtpAuthUri("CyberShieldMDM", cleanEmail, totpSecret);

        var newUser = new AdminUserRecord
        {
            Username = cleanUsername,
            FullName = fullName.Trim(),
            Email = cleanEmail,
            Role = string.IsNullOrWhiteSpace(role) ? "Admin" : role.Trim(),
            IsActive = true,
            Is2FaEnabled = false,
            TotpSecret = totpSecret,
            InviteToken = inviteToken,
            InviteExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            PasswordHash = string.Empty,
            Salt = string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };

        _usersCollection.Insert(newUser);
        _logger.LogInformation("Created user invite for '{Username}' ({Email}). Role: {Role}", cleanUsername, cleanEmail, newUser.Role);

        return (true, "User invitation created successfully.", inviteToken, totpSecret, qrUri);
    }

    public (bool Success, string Message, object? UserInfo) VerifyInviteToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, "Invitation token is required.", null);
        }

        var user = _usersCollection.FindOne(u => u.InviteToken == token.Trim());
        if (user == null)
        {
            return (false, "Invalid or expired invitation link.", null);
        }

        if (user.InviteExpiresAtUtc.HasValue && user.InviteExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return (false, "This invitation link has expired. Please ask your administrator to send a new invite.", null);
        }

        var qrUri = _totpService.GenerateOtpAuthUri("CyberShieldMDM", user.Email, user.TotpSecret);

        return (true, "Token verified successfully.", new
        {
            user.Username,
            user.FullName,
            user.Email,
            user.Role,
            user.TotpSecret,
            QrUri = qrUri
        });
    }

    public (bool Success, string Message) CompleteActivation(string token, string newPassword, string totpCode)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword) || string.IsNullOrWhiteSpace(totpCode))
        {
            return (false, "Token, new password, and 6-digit Authenticator verification code are required.");
        }

        if (newPassword.Length < 6)
        {
            return (false, "Password must be at least 6 characters long.");
        }

        var user = _usersCollection.FindOne(u => u.InviteToken == token.Trim());
        if (user == null || (user.InviteExpiresAtUtc.HasValue && user.InviteExpiresAtUtc.Value < DateTime.UtcNow))
        {
            return (false, "Invitation link is invalid or has expired.");
        }

        // Verify the 6-digit TOTP code generated by user's app
        if (!_totpService.ValidateCode(user.TotpSecret, totpCode))
        {
            return (false, "Invalid 6-digit code. Please enter the current code from your Authenticator app.");
        }

        // Set password and mark 2FA as fully activated
        var (hash, salt) = HashPassword(newPassword);
        user.PasswordHash = hash;
        user.Salt = salt;
        user.Is2FaEnabled = true;
        user.InviteToken = null;
        user.InviteExpiresAtUtc = null;

        _usersCollection.Update(user);
        _logger.LogInformation("User '{User}' successfully completed account activation with 2FA.", user.Username);

        return (true, "Account activated successfully! You can now log in using your password and Authenticator code.");
    }

    public (bool Success, string Message, string? InviteToken, string? QrUri, string? Secret) ResetUser2Fa(string username)
    {
        var clean = username.Trim().ToLowerInvariant();
        var user = _usersCollection.FindById(clean);
        if (user == null)
        {
            return (false, "User not found.", null, null, null);
        }

        var totpSecret = _totpService.GenerateSecret();
        var inviteToken = Guid.NewGuid().ToString("N");
        var qrUri = _totpService.GenerateOtpAuthUri("CyberShieldMDM", user.Email, totpSecret);

        user.TotpSecret = totpSecret;
        user.InviteToken = inviteToken;
        user.InviteExpiresAtUtc = DateTime.UtcNow.AddDays(7);
        user.Is2FaEnabled = false;

        _usersCollection.Update(user);
        _logger.LogInformation("2FA reset initiated for user '{Username}'. New invite token generated.", clean);

        return (true, "2FA reset successfully. A new activation link is ready.", inviteToken, qrUri, totpSecret);
    }

    public (bool Success, string Message) DeleteUser(string username)
    {
        var clean = username.Trim().ToLowerInvariant();
        if (clean == "admin")
        {
            return (false, "The primary system administrator ('admin') cannot be deleted.");
        }

        var user = _usersCollection.FindById(clean);
        if (user == null)
        {
            return (false, "User not found.");
        }

        _usersCollection.Delete(clean);
        _logger.LogInformation("User '{Username}' was permanently removed by administrator.", clean);
        return (true, $"User '{clean}' was successfully deleted.");
    }

    #endregion

    public static string? ExtractBearerToken(HttpRequest request)
    {
        var authHeader = request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader.Substring("Bearer ".Length).Trim();
        }
        
        // Also support SignalR query string token (?access_token=...)
        if (request.Query.TryGetValue("access_token", out var qToken) && !string.IsNullOrEmpty(qToken))
        {
            return qToken.ToString();
        }

        return null;
    }

    private static (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToBase64String(saltBytes);

        using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 100_000, HashAlgorithmName.SHA256);
        var hash = Convert.ToBase64String(pbkdf2.GetBytes(32));

        return (hash, salt);
    }

    private static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        try
        {
            var saltBytes = Convert.FromBase64String(storedSalt);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 100_000, HashAlgorithmName.SHA256);
            var computedHash = Convert.ToBase64String(pbkdf2.GetBytes(32));
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedHash), Encoding.UTF8.GetBytes(storedHash));
        }
        catch
        {
            return false;
        }
    }
}
