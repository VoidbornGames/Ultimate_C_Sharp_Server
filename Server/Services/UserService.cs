using Newtonsoft.Json;
using UltimateServer.Models;
using UltimateServer.Events;

namespace UltimateServer.Services
{
    public class UserService
    {
        private readonly string _usersFile;
        private readonly Logger _logger;
        private readonly AuthenticationService _authService;
        private readonly ValidationService _validationService;
        private readonly CacheService _cacheService;
        private readonly IEventBus _eventBus;
        private readonly object _userLock = new();

        public List<User> Users { get; private set; }

        // UPDATED CONSTRUCTOR: Accept FilePaths instead of a string
        public UserService(
            FilePaths filePaths, // <-- Changed from string usersFile
            Logger logger,
            AuthenticationService authService,
            ValidationService validationService,
            CacheService cacheService,
            IEventBus eventBus)
        {
            _usersFile = filePaths.UsersFile; // <-- Get the path from the object
            _logger = logger;
            _authService = authService;
            _validationService = validationService;
            _cacheService = cacheService;
            _eventBus = eventBus;
            Users = new List<User>();
        }

        public async Task LoadUsersAsync()
        {
            try
            {
                // Try to get from cache first
                var cachedUsers = _cacheService.Get<List<User>>("users");
                if (cachedUsers != null)
                {
                    Users = cachedUsers;
                    _logger.Log("✅ Users loaded from cache.");
                    return;
                }

                if (File.Exists(_usersFile))
                {
                    string json = await File.ReadAllTextAsync(_usersFile);
                    Users = JsonConvert.DeserializeObject<List<User>>(json) ?? new List<User>();
                }
                else
                {
                    Users = new List<User>();
                    // Create default admin user
                    Users.Add(new User
                    {
                        Username = "admin",
                        Password = _authService.HashPassword("admin123"),
                        Email = "admin@example.com",
                        uuid = Guid.NewGuid(),
                        Role = "admin"
                    });
                    await SaveUsersAsync();
                    _logger.Log("✅ Created default admin user (username: admin, password: admin123)");
                }

                // Cache the users
                _cacheService.Set("users", Users, TimeSpan.FromMinutes(30));
                _logger.Log($"✅ Loaded {Users.Count} users.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading users: {ex.Message}");
                Users = new List<User>();
            }
        }

        public async Task SaveUsersAsync()
        {
            try
            {
                string json = JsonConvert.SerializeObject(Users, Formatting.Indented);
                await File.WriteAllTextAsync(_usersFile, json);

                // Update cache
                _cacheService.Set("users", Users, TimeSpan.FromMinutes(30));

                _logger.Log("💾 Users saved.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving users: {ex.Message}");
            }
        }

        public async Task<(User user, string message)> CreateUserAsync(RegisterRequest request)
        {
            // Validate input
            var (isValid, errors) = _validationService.ValidateModel(request);
            if (!isValid)
            {
                return (null, string.Join(", ", errors));
            }

            // Check if user already exists
            if (Users.Any(u => u.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "Username already exists");
            }

            if (Users.Any(u => u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "Email already exists");
            }

            lock (_userLock)
            {
                var newUser = new User
                {
                    Username = request.Username,
                    Password = _authService.HashPassword(request.Password),
                    Email = request.Email,
                    uuid = Guid.NewGuid(),
                    Role = request.Role,
                    RefreshToken = _authService.GenerateRefreshToken(),
                    RefreshTokenExpiry = DateTime.UtcNow.AddDays(7)
                };
                Users.Add(newUser);
            }

            await SaveUsersAsync();
            var createdUser = Users.FirstOrDefault(u => u.Username == request.Username);
            _logger.Log($"✅ User created: {request.Username}");

            // NEW: Publish the UserRegisteredEvent to the event bus
            if (createdUser != null)
            {
                await _eventBus.PublishAsync(new UserRegisteredEvent(createdUser));
            }

            return (createdUser, "User created successfully");
        }

        public async Task<(User user, string message)> AuthenticateUserAsync(LoginRequest request)
        {
            // Check if account is locked
            if (_authService.IsAccountLocked(request.Username))
            {
                return (null, "Account is temporarily locked due to multiple failed login attempts");
            }

            var user = Users.FirstOrDefault(u => u.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                _authService.RecordFailedLoginAttempt(request.Username);
                return (null, "Invalid username or password");
            }

            if (_authService.VerifyPassword(request.Password, user.Password))
            {
                // Reset failed login attempts on successful login
                _authService.ResetFailedLoginAttempts(request.Username);

                // Update last login
                user.LastLogin = DateTime.UtcNow;

                // Generate new refresh token if needed or if remember me is checked
                if (request.RememberMe || string.IsNullOrEmpty(user.RefreshToken) || user.RefreshTokenExpiry <= DateTime.UtcNow)
                {
                    user.RefreshToken = _authService.GenerateRefreshToken();
                    user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
                }

                await SaveUsersAsync();
                _logger.Log($"✅ User authenticated: {user.Username}");
                return (user, "Authentication successful");
            }
            else
            {
                _authService.RecordFailedLoginAttempt(request.Username);
                return (null, "Invalid username or password");
            }
        }

        public async Task<(User user, string message)> RefreshTokenAsync(RefreshTokenRequest request)
        {
            var user = Users.FirstOrDefault(u => u.RefreshToken == request.RefreshToken);

            if (user == null)
            {
                return (null, "Invalid refresh token");
            }

            if (user.RefreshTokenExpiry <= DateTime.UtcNow)
            {
                return (null, "Refresh token expired");
            }

            // Generate new refresh token
            user.RefreshToken = _authService.GenerateRefreshToken();
            user.RefreshTokenExpiry = DateTime.UtcNow.AddDays(7);

            await SaveUsersAsync();
            _logger.Log($"✅ Token refreshed for user: {user.Username}");
            return (user, "Token refreshed successfully");
        }

        public async Task<(bool success, string message)> ChangePasswordAsync(string username, string currentPassword, string newPassword)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            if (!_authService.VerifyPassword(currentPassword, user.Password))
            {
                return (false, "Current password is incorrect");
            }

            if (!_validationService.IsStrongPassword(newPassword))
            {
                return (false, "New password does not meet security requirements");
            }

            user.Password = _authService.HashPassword(newPassword);
            await SaveUsersAsync();

            _logger.Log($"✅ Password changed for user: {username}");
            return (true, "Password changed successfully");
        }

        public async Task<(bool success, string message)> ResetPasswordAsync(PasswordResetRequest request)
        {
            var user = Users.FirstOrDefault(u => u.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                // Don't reveal that the email doesn't exist for security reasons
                return (true, "If the email exists, a password reset link has been sent");
            }

            // Generate password reset token (in a real app, this would be sent via email)
            var resetToken = _authService.GeneratePasswordResetToken();

            // Store the token with expiry (in a real app, this would be in a separate table)
            // For this example, we'll just log it
            _logger.Log($"🔑 Password reset token for {user.Email}: {resetToken}");

            return (true, "If the email exists, a password reset link has been sent");
        }

        public async Task<(bool success, string message)> ConfirmPasswordResetAsync(ChangePasswordRequest request)
        {
            // In a real implementation, you would validate the token against a stored token
            // For this example, we'll just check if the token looks valid
            if (!_authService.ValidatePasswordResetToken(null, request.Token))
            {
                return (false, "Invalid or expired reset token");
            }

            if (!_validationService.IsStrongPassword(request.NewPassword))
            {
                return (false, "New password does not meet security requirements");
            }

            // In a real implementation, you would find the user associated with the token
            // For this example, we'll just return success
            _logger.Log($"✅ Password reset confirmed with token: {request.Token.Substring(0, 10)}...");

            return (true, "Password reset successfully");
        }

        public async Task<(bool success, string message)> UpdateUserProfileAsync(string username, User updatedUser)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            // Validate email if it's being changed
            if (!string.IsNullOrEmpty(updatedUser.Email) &&
                updatedUser.Email != user.Email &&
                Users.Any(u => u.Email.Equals(updatedUser.Email, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "Email already exists");
            }

            // Update allowed fields
            if (!string.IsNullOrEmpty(updatedUser.Email))
            {
                user.Email = updatedUser.Email;
            }

            await SaveUsersAsync();
            _logger.Log($"✅ Profile updated for user: {username}");
            return (true, "Profile updated successfully");
        }

        public async Task<(bool success, string message)> DeleteUserAsync(string username)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            if (user.Role == "admin")
            {
                return (false, "Cannot delete admin user");
            }

            lock (_userLock)
            {
                Users.Remove(user);
            }

            await SaveUsersAsync();
            _logger.Log($"✅ User deleted: {username}");
            return (true, "User deleted successfully");
        }

        public User GetUserByUsername(string username)
        {
            return Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }

        public User GetUserByEmail(string email)
        {
            return Users.FirstOrDefault(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
        }

        public User GetUserByUuid(Guid uuid)
        {
            return Users.FirstOrDefault(u => u.uuid == uuid);
        }

        public async Task<(bool success, string message)> LogoutAsync(string username)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            // Invalidate refresh token
            user.RefreshToken = "";
            user.RefreshTokenExpiry = DateTime.UtcNow;

            await SaveUsersAsync();
            _logger.Log($"✅ User logged out: {username}");
            return (true, "Logout successful");
        }

        public async Task<(bool success, string message)> EnableTwoFactorAsync(string username)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            // Generate 2FA secret (in a real implementation, you would use a library like Google.Authenticator)
            user.TwoFactorSecret = Guid.NewGuid().ToString();
            user.TwoFactorEnabled = true;

            await SaveUsersAsync();
            _logger.Log($"✅ 2FA enabled for user: {username}");
            return (true, "Two-factor authentication enabled");
        }

        public async Task<(bool success, string message)> DisableTwoFactorAsync(string username)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            user.TwoFactorEnabled = false;
            user.TwoFactorSecret = "";

            await SaveUsersAsync();
            _logger.Log($"✅ 2FA disabled for user: {username}");
            return (true, "Two-factor authentication disabled");
        }

        public async Task<(bool success, string message)> VerifyTwoFactorAsync(string username, string code)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            if (!user.TwoFactorEnabled)
            {
                return (false, "Two-factor authentication is not enabled");
            }

            // In a real implementation, you would verify the code against the secret
            // For this example, we'll just check if the code is "123456"
            if (code != "123456")
            {
                return (false, "Invalid verification code");
            }

            _logger.Log($"✅ 2FA verified for user: {username}");
            return (true, "Two-factor authentication verified");
        }

        public List<User> GetUsersByRole(string role)
        {
            return Users.Where(u => u.Role.Equals(role, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public async Task<(bool success, string message)> ChangeUserRoleAsync(string username, string newRole)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            if (user.Role == "admin" && newRole != "admin")
            {
                // Check if there are other admin users
                var adminCount = Users.Count(u => u.Role == "admin");
                if (adminCount <= 1)
                {
                    return (false, "Cannot change role of the last admin user");
                }
            }

            user.Role = newRole;
            await SaveUsersAsync();

            _logger.Log($"✅ Role changed for user {username} to {newRole}");
            return (true, "Role changed successfully");
        }

        public async Task<(bool success, string message)> LockUserAsync(string username, int lockDurationMinutes = 30)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            user.LockedUntil = DateTime.UtcNow.AddMinutes(lockDurationMinutes);
            await SaveUsersAsync();

            _logger.Log($"🔒 User locked: {username} until {user.LockedUntil}");
            return (true, $"User locked for {lockDurationMinutes} minutes");
        }

        public async Task<(bool success, string message)> UnlockUserAsync(string username)
        {
            var user = Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                return (false, "User not found");
            }

            user.LockedUntil = null;
            user.FailedLoginAttempts = 0;

            // Also reset in the authentication service
            _authService.ResetFailedLoginAttempts(username);

            await SaveUsersAsync();

            _logger.Log($"🔓 User unlocked: {username}");
            return (true, "User unlocked successfully");
        }
    }
}