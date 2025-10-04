using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Services
{
    public class ValidationService
    {
        private readonly ServerConfig _config;
        private readonly Logger _logger;

        public ValidationService(ServerConfig config, Logger logger)
        {
            _config = config;
            _logger = logger;
        }

        public (bool isValid, List<string> errors) ValidateModel<T>(T model) where T : class
        {
            var context = new ValidationContext(model);
            var results = new List<ValidationResult>();
            var isValid = Validator.TryValidateObject(model, context, results, true);

            return (isValid, results.Select(r => r.ErrorMessage).ToList());
        }

        public bool IsValidJson(string jsonString)
        {
            if (string.IsNullOrWhiteSpace(jsonString))
                return false;

            try
            {
                System.Text.Json.JsonDocument.Parse(jsonString);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool IsValidPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                Path.GetFullPath(path);
                return !path.Contains("..") && !path.Contains("//") && !path.Contains("\\\\");
            }
            catch
            {
                return false;
            }
        }

        public bool IsValidFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            return !fileName.Contains("..") &&
                   !fileName.Contains("/") &&
                   !fileName.Contains("\\") &&
                   !fileName.Any(c => Path.GetInvalidFileNameChars().Contains(c));
        }

        public bool IsVideoFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension == ".mp4" || extension == ".webm" || extension == ".ogg" ||
                   extension == ".avi" || extension == ".mov" || extension == ".mkv";
        }

        public bool IsSafeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                var uri = new Uri(url);
                return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            }
            catch
            {
                return false;
            }
        }

        public bool IsSafeEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        public bool IsStrongPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < _config.PasswordMinLength)
                return false;

            if (_config.RequireSpecialChars)
            {
                return Regex.IsMatch(password, @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^\da-zA-Z]).{8,}$");
            }

            return password.Length >= _config.PasswordMinLength;
        }

        public string SanitizeInput(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            // Basic HTML sanitization
            return Regex.Replace(input, "<.*?>", string.Empty);
        }
    }
}