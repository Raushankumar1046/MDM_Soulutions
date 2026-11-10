namespace EndpointManager.Server.Services;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LiteDB;

public class UserPermissions
{
    // Basic Monitoring & Logs
    public bool CanViewEndpoints { get; set; } = true;
    public bool CanViewAuditLogs { get; set; } = true;

    // Individual Security Policies
    public bool CanToggleUsbLock { get; set; } = false;
    public bool CanToggleSoftwareBlock { get; set; } = false;
    public bool CanToggleFileTransfer { get; set; } = false;
    public bool CanToggleWebFilter { get; set; } = false;
    public bool CanTogglePrintControl { get; set; } = false;

    // Remote Tools Access (Live Screen, Interactive Terminal, File Manager)
    public bool CanRemoteAccess { get; set; } = false;

    // Emergency Powers (Lock Workstation, Restart Laptop, Factory Wipe)
    public bool CanEmergencyAction { get; set; } = false;

    // Administrative Powers (Invite/Manage Console Users)
    public bool CanManageUsers { get; set; } = false;

    public static UserPermissions CreateSuperAdminDefaults() => new()
    {
        CanViewEndpoints = true,
        CanViewAuditLogs = true,
        CanToggleUsbLock = true,
        CanToggleSoftwareBlock = true,
        CanToggleFileTransfer = true,
        CanToggleWebFilter = true,
        CanTogglePrintControl = true,
        CanRemoteAccess = true,
        CanEmergencyAction = true,
        CanManageUsers = true
    };

    public static UserPermissions CreateAdminDefaults() => new()
    {
        CanViewEndpoints = true,
        CanViewAuditLogs = true,
        CanToggleUsbLock = true,
        CanToggleSoftwareBlock = true,
        CanToggleFileTransfer = true,
        CanToggleWebFilter = true,
        CanTogglePrintControl = true,
        CanRemoteAccess = true,
        CanEmergencyAction = false,
        CanManageUsers = false
    };

    public static UserPermissions CreateViewerDefaults() => new()
    {
        CanViewEndpoints = true,
        CanViewAuditLogs = true,
        CanToggleUsbLock = false,
        CanToggleSoftwareBlock = false,
        CanToggleFileTransfer = false,
        CanToggleWebFilter = false,
        CanTogglePrintControl = false,
        CanRemoteAccess = false,
        CanEmergencyAction = false,
        CanManageUsers = false
    };
}

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
    public UserPermissions Permissions { get; set; } = new();
}

public class ActiveSession
{
    public string Token { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public DateTime ExpiresAtUtc { get; set; }
    public UserPermissions Permissions { get; set; } = new();
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
                if (admin.Permissions == null) { admin.Permissions = UserPermissions.CreateSuperAdminDefaults(); updated = true; }
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
                    CreatedAtUtc = DateTime.UtcNow,
                    Permissions = UserPermissions.CreateSuperAdminDefaults()
                };
                _usersCollection.Insert(admin);
            }
            else
            {
                admin.PasswordHash = hash;
                admin.Salt = salt;
                admin.IsActive = true;
                admin.Permissions = UserPermissions.CreateSuperAdminDefaults();
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
            _dbService.LogAudit("CONSOLE", "LoginFailed", false, false, $"Failed login attempt for '{cleanUsername}'. User not found or account deactivated.", actionBy: cleanUsername, category: "Auth");
            return (false, false, null, null, null, "Invalid username or account is deactivated.");
        }

        if (string.IsNullOrEmpty(user.PasswordHash) || !VerifyPassword(password, user.PasswordHash, user.Salt))
        {
            _logger.LogWarning("Failed login attempt for user: {User}", username);
            _dbService.LogAudit("CONSOLE", "LoginFailed", false, false, $"Failed login attempt for user '{cleanUsername}'. Incorrect password.", actionBy: cleanUsername, category: "Auth");
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
                _dbService.LogAudit("CONSOLE", "2FAFailed", false, false, $"Incorrect 2FA Authenticator code entered for user '{user.Username}'.", actionBy: user.Username, category: "Auth");
                return (false, true, null, null, user.Username, "Invalid 6-digit Authenticator code. Please check your app.");
            }
        }

        // Successful authentication: issue full session token
        var sessionToken = CreateUserSession(user);
        _dbService.LogAudit("CONSOLE", "UserLogin", true, true, $"User '{user.Username}' ({user.Role}) logged in successfully.", actionBy: user.Username, category: "Auth");
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
            _dbService.LogAudit("CONSOLE", "2FAFailed", false, false, $"Invalid 2FA code verification for user '{user.Username}'.", actionBy: user.Username, category: "Auth");
            return (false, null, null, null, "Invalid 6-digit code. Please enter the latest code from your Authenticator app.");
        }

        // Code is valid! Consume temporary session and create full session token
        _pending2FaSessions.TryRemove(tempToken, out _);
        var sessionToken = CreateUserSession(user);
        _dbService.LogAudit("CONSOLE", "2FAVerified", true, true, $"User '{user.Username}' ({user.Role}) completed 2FA verification and logged in.", actionBy: user.Username, category: "Auth");

        return (true, sessionToken, user.Username, user.Role, "2FA Verification successful.");
    }

    private string CreateUserSession(AdminUserRecord user)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        UserPermissions effectivePerms;
        if (user.Username.Equals("admin", StringComparison.OrdinalIgnoreCase) || user.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
        {
            effectivePerms = UserPermissions.CreateSuperAdminDefaults();
        }
        else if (user.Permissions != null)
        {
            effectivePerms = user.Permissions;
        }
        else if (user.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
        {
            effectivePerms = UserPermissions.CreateAdminDefaults();
        }
        else
        {
            effectivePerms = UserPermissions.CreateViewerDefaults();
        }

        var session = new ActiveSession
        {
            Token = token,
            Username = user.Username,
            Role = user.Role,
            Permissions = effectivePerms,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(24) // 24-hour token validity
        };

        _activeTokens[token] = session;

        user.LastLoginUtc = DateTime.UtcNow;
        _usersCollection.Update(user);

        _logger.LogInformation("User '{User}' ({Role}) logged in successfully.", user.Username, user.Role);
        return token;
    }

    public bool ValidateToken(string? token, out string username, out string role, out UserPermissions permissions)
    {
        username = string.Empty;
        role = string.Empty;
        permissions = UserPermissions.CreateViewerDefaults();
        if (string.IsNullOrWhiteSpace(token)) return false;

        if (_activeTokens.TryGetValue(token, out var session))
        {
            if (session.ExpiresAtUtc > DateTime.UtcNow)
            {
                username = session.Username;
                role = session.Role;
                permissions = session.Permissions ?? UserPermissions.CreateViewerDefaults();
                return true;
            }
            else
            {
                _activeTokens.TryRemove(token, out _);
            }
        }

        return false;
    }

    public bool ValidateToken(string? token, out string username, out string role) => ValidateToken(token, out username, out role, out _);
    public bool ValidateToken(string? token, out string username) => ValidateToken(token, out username, out _, out _);

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
            Permissions = (u.Username.Equals("admin", StringComparison.OrdinalIgnoreCase) || u.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
                ? UserPermissions.CreateSuperAdminDefaults()
                : (u.Permissions ?? (u.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? UserPermissions.CreateAdminDefaults() : UserPermissions.CreateViewerDefaults())),
            HasPendingInvite = !string.IsNullOrEmpty(u.InviteToken) && (u.InviteExpiresAtUtc == null || u.InviteExpiresAtUtc > DateTime.UtcNow),
            InviteExpiresAtUtc = u.InviteExpiresAtUtc,
            u.CreatedAtUtc,
            u.LastLoginUtc
        });
    }

    public (bool Success, string Message, string? InviteToken, string? TotpSecret, string? QrUri) CreateUserInvite(
        string fullName, string email, string username, string role, UserPermissions? initialPermissions = null)
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

        var assignedRole = string.IsNullOrWhiteSpace(role) ? "Admin" : role.Trim();
        var perms = initialPermissions ?? (assignedRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) 
            ? UserPermissions.CreateAdminDefaults() 
            : UserPermissions.CreateViewerDefaults());

        var newUser = new AdminUserRecord
        {
            Username = cleanUsername,
            FullName = fullName.Trim(),
            Email = cleanEmail,
            Role = assignedRole,
            IsActive = true,
            Is2FaEnabled = false,
            TotpSecret = totpSecret,
            InviteToken = inviteToken,
            InviteExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            PasswordHash = string.Empty,
            Salt = string.Empty,
            Permissions = perms,
            CreatedAtUtc = DateTime.UtcNow
        };

        _usersCollection.Insert(newUser);
        _logger.LogInformation("Created user invite for '{Username}' ({Email}). Role: {Role}", cleanUsername, cleanEmail, newUser.Role);
        _dbService.LogAudit("CONSOLE", "UserInvited", true, true, $"Invited new user '{cleanUsername}' ({cleanEmail}) with role '{newUser.Role}'.", actionBy: "Admin", category: "UserManagement");

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
        _dbService.LogAudit("CONSOLE", "AccountActivated", true, true, $"User '{user.Username}' ({user.Email}) completed activation and enabled 2FA.", actionBy: user.Username, category: "UserManagement");

        return (true, "Account activated successfully! You can now log in using your password and Authenticator code.");
    }

    public bool IsSuperAdmin(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        var clean = username.Trim().ToLowerInvariant();
        if (clean == "admin") return true;
        var user = _usersCollection.FindById(clean);
        return user != null && user.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase);
    }

    public (bool Success, string Message, string? InviteToken, string? QrUri, string? Secret) ResetUser2Fa(string operatorUsername, string targetUsername)
    {
        var cleanTarget = targetUsername.Trim().ToLowerInvariant();
        var cleanOperator = operatorUsername.Trim().ToLowerInvariant();
        
        var targetUser = _usersCollection.FindById(cleanTarget);
        if (targetUser == null)
        {
            return (false, "User not found.", null, null, null);
        }

        bool isTargetSuperAdmin = cleanTarget == "admin" || targetUser.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase);
        bool isOperatorSuperAdmin = cleanOperator == "admin" || IsSuperAdmin(cleanOperator);

        // Security check: Only SuperAdmin can reset 2FA on a SuperAdmin account
        if (isTargetSuperAdmin && !isOperatorSuperAdmin)
        {
            _logger.LogWarning("Unauthorized 2FA reset attempt: '{Operator}' tried to reset 2FA for SuperAdmin '{Target}'", cleanOperator, cleanTarget);
            _dbService.LogAudit("CONSOLE", "UnauthorizedAccess", false, false,
                $"User '{cleanOperator}' attempted to reset 2FA for protected SuperAdmin account '{cleanTarget}'. Action blocked.",
                actionBy: cleanOperator, category: "Security");
            return (false, "Access Denied: Only a SuperAdmin can reset 2FA for a SuperAdmin account.", null, null, null);
        }

        var totpSecret = _totpService.GenerateSecret();
        var inviteToken = Guid.NewGuid().ToString("N");
        var qrUri = _totpService.GenerateOtpAuthUri("CyberShieldMDM", targetUser.Email, totpSecret);

        targetUser.TotpSecret = totpSecret;
        targetUser.InviteToken = inviteToken;
        targetUser.InviteExpiresAtUtc = DateTime.UtcNow.AddDays(7);
        targetUser.Is2FaEnabled = false;

        _usersCollection.Update(targetUser);
        _logger.LogInformation("2FA reset initiated for user '{Username}' by '{Operator}'. New invite token generated.", cleanTarget, cleanOperator);
        _dbService.LogAudit("CONSOLE", "2FAReset", true, true,
            $"2FA reset requested for user '{cleanTarget}' by '{cleanOperator}'. New setup token generated.",
            actionBy: cleanOperator, category: "UserManagement");

        return (true, "2FA reset successfully. A new activation link is ready.", inviteToken, qrUri, totpSecret);
    }

    public (bool Success, string Message) DeleteUser(string operatorUsername, string targetUsername)
    {
        var cleanTarget = targetUsername.Trim().ToLowerInvariant();
        var cleanOperator = operatorUsername.Trim().ToLowerInvariant();

        var targetUser = _usersCollection.FindById(cleanTarget);
        if (targetUser == null)
        {
            return (false, "User not found.");
        }

        bool isTargetSuperAdmin = cleanTarget == "admin" || targetUser.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase);
        if (isTargetSuperAdmin)
        {
            return (false, "The primary system administrator ('admin') and SuperAdmin accounts cannot be deleted.");
        }

        bool isOperatorSuperAdmin = cleanOperator == "admin" || IsSuperAdmin(cleanOperator);
        if (!isOperatorSuperAdmin)
        {
            return (false, "Access Denied: Only a SuperAdmin can delete console users.");
        }

        _usersCollection.Delete(cleanTarget);
        RevokeUserSessions(cleanTarget);
        _logger.LogInformation("User '{Username}' was permanently removed by '{Operator}'.", cleanTarget, cleanOperator);
        _dbService.LogAudit("CONSOLE", "UserDeleted", false, true, $"User '{cleanTarget}' was permanently deleted by '{cleanOperator}'.", actionBy: cleanOperator, category: "UserManagement");
        return (true, $"User '{cleanTarget}' was successfully deleted.");
    }

    public (bool Success, string Message, AdminUserRecord? User) GetUser(string username)
    {
        var clean = username.Trim().ToLowerInvariant();
        var user = _usersCollection.FindById(clean);
        if (user == null) return (false, "User not found.", null);
        return (true, "User found.", user);
    }

    public (bool Success, string Message) UpdateUser(
        string operatorUsername,
        string targetUsername,
        string? fullName,
        string? email,
        string? role,
        bool? isActive,
        bool? is2FaEnabled,
        string? newPassword,
        UserPermissions? permissions = null)
    {
        var cleanTarget = targetUsername.Trim().ToLowerInvariant();
        var cleanOperator = operatorUsername.Trim().ToLowerInvariant();

        var targetUser = _usersCollection.FindById(cleanTarget);
        if (targetUser == null) return (false, "Target user not found.");

        bool isTargetSuperAdmin = cleanTarget == "admin" || targetUser.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase);
        bool isOperatorSuperAdmin = cleanOperator == "admin" || IsSuperAdmin(cleanOperator);

        // Security check: Only SuperAdmin can modify a SuperAdmin account
        if (isTargetSuperAdmin && !isOperatorSuperAdmin)
        {
            _logger.LogWarning("Unauthorized user modification attempt: '{Operator}' tried to modify SuperAdmin '{Target}'", cleanOperator, cleanTarget);
            _dbService.LogAudit("CONSOLE", "UnauthorizedAccess", false, false,
                $"User '{cleanOperator}' attempted to modify protected SuperAdmin account '{cleanTarget}'. Action blocked.",
                actionBy: cleanOperator, category: "Security");
            return (false, "Access Denied: Only a SuperAdmin can modify SuperAdmin account details or credentials.");
        }

        // Security check: SuperAdmin cannot be deactivated
        if (isTargetSuperAdmin && isActive.HasValue && !isActive.Value)
        {
            return (false, "The primary system administrator account cannot be deactivated.");
        }

        // Security check: Non-SuperAdmin cannot promote anyone to SuperAdmin
        if (!isOperatorSuperAdmin && !string.IsNullOrWhiteSpace(role) && role.Trim().Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Access Denied: Only a SuperAdmin can assign the SuperAdmin role.");
        }

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            targetUser.FullName = fullName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var cleanEmail = email.Trim().ToLowerInvariant();
            var existingWithEmail = _usersCollection.FindOne(u => u.Email == cleanEmail && u.Username != cleanTarget);
            if (existingWithEmail != null)
            {
                return (false, $"Email '{cleanEmail}' is already used by another user.");
            }
            targetUser.Email = cleanEmail;
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            targetUser.Role = role.Trim();
        }

        if (isActive.HasValue)
        {
            targetUser.IsActive = isActive.Value;
            if (!targetUser.IsActive)
            {
                // Instantly revoke any active sessions for this deactivated user
                RevokeUserSessions(cleanTarget);
            }
        }

        if (is2FaEnabled.HasValue)
        {
            targetUser.Is2FaEnabled = is2FaEnabled.Value;
        }

        if (isTargetSuperAdmin)
        {
            targetUser.Permissions = UserPermissions.CreateSuperAdminDefaults();
        }
        else if (permissions != null)
        {
            if (!isOperatorSuperAdmin)
            {
                // Non-SuperAdmin cannot grant user management permissions
                permissions.CanManageUsers = false;
            }
            targetUser.Permissions = permissions;
        }

        if (!string.IsNullOrWhiteSpace(newPassword))
        {
            if (newPassword.Length < 6)
            {
                return (false, "Password must be at least 6 characters long.");
            }
            var (hash, salt) = HashPassword(newPassword);
            targetUser.PasswordHash = hash;
            targetUser.Salt = salt;
        }

        _usersCollection.Update(targetUser);

        // Instantly sync updated role and permissions to in-memory active session tokens
        foreach (var session in _activeTokens.Values.Where(s => s.Username.Equals(cleanTarget, StringComparison.OrdinalIgnoreCase)))
        {
            session.Role = targetUser.Role;
            session.Permissions = targetUser.Permissions ?? UserPermissions.CreateViewerDefaults();
        }

        _logger.LogInformation("User '{Username}' updated by '{Operator}'. IsActive={IsActive}, Role={Role}, 2FA={Is2Fa}",
            cleanTarget, cleanOperator, targetUser.IsActive, targetUser.Role, targetUser.Is2FaEnabled);
        _dbService.LogAudit("CONSOLE", "UserUpdated", true, true,
            $"User '{cleanTarget}' profile/permissions modified by '{cleanOperator}': Role={targetUser.Role}, 2FA={targetUser.Is2FaEnabled}.",
            actionBy: cleanOperator, category: "UserManagement");

        return (true, "User updated successfully.");
    }

    public (bool Success, string Message, bool NewStatus) ToggleUserStatus(string operatorUsername, string targetUsername)
    {
        var cleanTarget = targetUsername.Trim().ToLowerInvariant();
        var cleanOperator = operatorUsername.Trim().ToLowerInvariant();

        var targetUser = _usersCollection.FindById(cleanTarget);
        if (targetUser == null) return (false, "User not found.", false);

        bool isTargetSuperAdmin = cleanTarget == "admin" || targetUser.Role.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase);
        if (isTargetSuperAdmin)
        {
            return (false, "SuperAdmin accounts cannot be deactivated.", true);
        }

        bool isOperatorSuperAdmin = cleanOperator == "admin" || IsSuperAdmin(cleanOperator);
        if (!isOperatorSuperAdmin && targetUser.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase) && cleanOperator != cleanTarget)
        {
            return (false, "Access Denied: Only a SuperAdmin can toggle status for other Administrator accounts.", targetUser.IsActive);
        }

        targetUser.IsActive = !targetUser.IsActive;
        if (!targetUser.IsActive)
        {
            RevokeUserSessions(cleanTarget);
        }

        _usersCollection.Update(targetUser);
        _logger.LogInformation("Toggled user '{Username}' active status to: {Status} by '{Operator}'", cleanTarget, targetUser.IsActive, cleanOperator);

        var msg = targetUser.IsActive ? $"User '{cleanTarget}' has been enabled/activated." : $"User '{cleanTarget}' has been disabled/deactivated.";
        _dbService.LogAudit("CONSOLE", "UserStatusToggled", targetUser.IsActive, true, $"{msg} Action by '{cleanOperator}'.", actionBy: cleanOperator, category: "UserManagement");
        return (true, msg, targetUser.IsActive);
    }

    public void RevokeUserSessions(string username)
    {
        var clean = username.Trim().ToLowerInvariant();
        var keysToRemove = _activeTokens
            .Where(kvp => kvp.Value.Username.Equals(clean, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _activeTokens.TryRemove(key, out _);
        }
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
