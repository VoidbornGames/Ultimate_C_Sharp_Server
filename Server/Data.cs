using System.ComponentModel.DataAnnotations;

namespace UltimateServer.Models
{
    public class Data
    {
        public int protocolVersion { get; set; } = 1;
        public string theCommand { get; set; } = "";
        public string jsonData { get; set; } = "";
    }

    public class User
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string Email { get; set; } = "";
        public Guid uuid { get; set; }
        public string Role { get; set; } = "player";
        public bool TwoFactorEnabled { get; set; } = false;
        public string TwoFactorSecret { get; set; } = "";
        public DateTime LastLogin { get; set; } = DateTime.UtcNow;
        public int FailedLoginAttempts { get; set; } = 0;
        public DateTime? LockedUntil { get; set; }
        public string RefreshToken { get; set; } = "";
        public DateTime RefreshTokenExpiry { get; set; } = DateTime.UtcNow.AddDays(7);
    }

    public class ServerConfig
    {
        public string Ip { get; set; } = "0.0.0.0";
        public int MaxConnections { get; set; } = 50;
        public string DashboardPasswordHash { get; set; } = "12345678";
        public int PasswordMinLength { get; set; } = 8;
        public bool RequireSpecialChars { get; set; } = true;
        public int MaxFailedLoginAttempts { get; set; } = 5;
        public int LockoutDurationMinutes { get; set; } = 30;
        public int JwtExpiryHours { get; set; } = 24;
        public int RefreshTokenDays { get; set; } = 7;
        public int MaxRequestSizeMB { get; set; } = 100;
        public bool EnableCompression { get; set; } = true;
        public int CacheExpiryMinutes { get; set; } = 15;
        public int ConnectionPoolSize { get; set; } = 10;
        public string email_host { get; set; } = "0.0.0.0";
        public int email_port { get; set; } = 11003;
        public string email_username { get; set; } = "Email_User_Name";
        public string email_password { get; set; } = "Email_User_Password";
        public bool email_useSsl { get; set; } = false;
    }

    public class LoginRequest
    {
        [Required]
        [StringLength(50, MinimumLength = 3)]
        public string Username { get; set; }

        [Required]
        [StringLength(100, MinimumLength = 6)]
        public string Password { get; set; }

        public bool RememberMe { get; set; } = false;
    }

    public class RegisterRequest
    {
        [Required]
        [StringLength(50, MinimumLength = 3)]
        public string Username { get; set; }

        [Required]
        [StringLength(100, MinimumLength = 8)]
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^\da-zA-Z]).{8,}$",
            ErrorMessage = "Password must contain at least one uppercase letter, one lowercase letter, one digit and one special character")]
        public string Password { get; set; }

        [Required]
        [EmailAddress]
        public string Email { get; set; }

        public string Role { get; set; } = "player";
    }

    public class PasswordResetRequest
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; }
    }

    public class ChangePasswordRequest
    {
        [Required]
        public string Token { get; set; }

        [Required]
        [StringLength(100, MinimumLength = 8)]
        [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^\da-zA-Z]).{8,}$",
            ErrorMessage = "Password must contain at least one uppercase letter, one lowercase letter, one digit and one special character")]
        public string NewPassword { get; set; }
    }

    public class RefreshTokenRequest
    {
        [Required]
        public string RefreshToken { get; set; }
    }

    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }


    /// <summary>
    /// Holds all file path and folder constants for the application.
    /// </summary>
    public class FilePaths
    {
        public string UsersFile { get; set; } = "users.json";
        public string VideosFolder { get; set; } = "videos";
        public string LogsFolder { get; set; } = "logs";
        public string ConfigFile { get; set; } = "config.json";
    }


    /// <summary>
    /// Holds all startup settings for the server, such as port numbers.
    /// </summary>
    public class ServerSettings
    {
        public string Ip { get; set; } = "0.0.0.0";
        public int MaxConnections { get; set; } = 50;
        public int Port { get; set; }
        public int WebPort { get; set; }
        public int VoicePort { get; set; }
    }
}