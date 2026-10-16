namespace EndpointManager.Server.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LiteDB;

public class AdminUserRecord
{
    [BsonId]
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
}

public class ActiveSession
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public class AuthService
{
    private readonly LiteDbService _dbService;
    private readonly ILogger<AuthService> _logger;
    private readonly ILiteCollection<AdminUserRecord> _usersCollection;
    
    // In-memory fast cache of active tokens: Token -> ActiveSession
    private readonly ConcurrentDictionary<string, ActiveSession> _activeTokens = new();

    public AuthService(LiteDbService dbService, ILogger<AuthService> logger)
    {
        _dbService = dbService;
        _logger = logger;
        _usersCollection = _dbService.Database.GetCollection<AdminUserRecord>("admin_users");
        _usersCollection.EnsureIndex(u => u.Username, unique: true);

        InitializeDefaultAdmin();
    }

    private void InitializeDefaultAdmin()
    {
        try
        {
            var admin = _usersCollection.FindById("admin");
            if (admin == null)
            {
                // Create temporary default credentials: admin / admin123
                var (hash, salt) = HashPassword("admin123");
                admin = new AdminUserRecord
                {
                    Username = "admin",
                    PasswordHash = hash,
                    Salt = salt,
                    CreatedAtUtc = DateTime.UtcNow
                };
                _usersCollection.Insert(admin);
                _logger.LogWarning("Created initial temporary admin account: Username='admin', Password='admin123'. Change this password after initial login.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize default admin user in LiteDB.");
        }
    }

    public (bool Success, string? Token, string? Message) Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (false, null, "Username and password are required.");
        }

        var user = _usersCollection.FindById(username.Trim().ToLowerInvariant());
        if (user == null)
        {
            return (false, null, "Invalid username or password.");
        }

        if (!VerifyPassword(password, user.PasswordHash, user.Salt))
        {
            _logger.LogWarning("Failed login attempt for user: {User}", username);
            return (false, null, "Invalid username or password.");
        }

        // Generate 32-byte cryptographically secure token
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        var session = new ActiveSession
        {
            Token = token,
            Username = user.Username,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24) // 24-hour token validity
        };

        _activeTokens[token] = session;

        user.LastLoginUtc = DateTime.UtcNow;
        _usersCollection.Update(user);

        _logger.LogInformation("Admin user logged in successfully: {User}", user.Username);
        return (true, token, "Login successful.");
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

        _logger.LogInformation("Password successfully updated for admin user: {User}", username);
        return (true, "Password changed successfully.");
    }

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
