using Newtonsoft.Json;
using System.Text;
using System.Xml.Linq;
using UltimateServer.Services;

namespace Server.Services
{
    /// <summary>
    /// Manages website creation, configuration, and deletion on a server.
    /// Handles Nginx configuration, PHP-FPM pool management, SSL certificates, and SFTP user accounts.
    /// </summary>
    class SitePress
    {
        private Logger _logger;
        private Nginx _nginx;
        private SftpServer _sftpServer;

        private string templateFolder;

        /// <summary>
        /// Gets the path to the sites configuration file
        /// </summary>
        public string sitesConfig { get; private set; } = "sites.json";

        /// <summary>
        /// Dictionary containing site names and their corresponding ports
        /// </summary>
        public Dictionary<string, int> sites;

        /// <summary>
        /// Initializes a new instance of the SitePress class
        /// </summary>
        /// <param name="logger">Logger instance for logging operations</param>
        /// <param name="nginx">Nginx service instance for managing web server configurations</param>
        /// <param name="sftpServer">SFTP server instance for managing user accounts</param>
        public SitePress(Logger logger, Nginx nginx, SftpServer sftpServer)
        {
            _logger = logger;
            _nginx = nginx;
            _sftpServer = sftpServer;
            templateFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Template");
        }

        /// <summary>
        /// Starts the SitePress service by loading existing sites
        /// </summary>
        public async Task Start()
        {
            sites = await LoadSites();
            _logger.Log("🎨 SitePress has been started!");
        }

        /// <summary>
        /// Stops the SitePress service and saves the current sites configuration
        /// </summary>
        public async Task StopAsync()
        {
            await SaveSites();
            _logger.Log("🎨 SitePress stopped");
        }

        /// <summary>
        /// Creates a new website with the specified name and port
        /// </summary>
        /// <param name="name">The domain of the website</param>
        /// <param name="port">The port number for the website</param>
        /// <returns>True if the site was created successfully, false otherwise</returns>
        public async Task<bool> CreateSite(string name, int port)
        {
            var sitePath = "/var/www/" + name;

            try
            {
                // 1. Create site directory
                Directory.CreateDirectory(sitePath);
                CopyDirectory(templateFolder, sitePath);

                // 2. Create PHP-FPM pool for this site
                await CreatePhpFpmPool(name, sitePath);

                // 3. Create nginx config with site-specific socket
                var httpConfig = _nginx.configHttp
                    .Replace("%SiteName%", name)
                    .Replace("%SitePath%", sitePath)
                    .Replace("%SitePort%", port.ToString());

                var siteConf = $"/etc/nginx/sites-available/{name}.conf";
                File.WriteAllText(siteConf, httpConfig);

                await _nginx.RunCommand("sudo", $"ln -sf {siteConf} /etc/nginx/sites-enabled/");
                await _nginx.RunCommand("sudo", "nginx -t");
                await _nginx.RunCommand("sudo", "systemctl reload nginx");

                // 4. Get SSL certificate
                await _nginx.RunCommand("sudo", $"certbot --nginx -d {name} --non-interactive --agree-tos -m admin@{name} --redirect");

                // 5. Replace with SSL config
                var sslConfig = _nginx.configSSL
                    .Replace("%SiteName%", name)
                    .Replace("%SitePath%", sitePath);

                File.WriteAllText(siteConf, sslConfig);
                await _nginx.RunCommand("sudo", "nginx -t");
                await _nginx.RunCommand("sudo", "systemctl reload nginx");

                // 6. Add to sites list
                sites.Add(name, port);
                await SaveSites();

                // 7. SFTP setup
                var pass = SimplePasswordGenerator.Generate();
                _sftpServer.usersFolders.Add(name, name);
                _sftpServer.userCredentials.Add(name, pass);
                _logger.Log($"SitePress: SFTP user {name} has been created with password: {pass}");
                await _sftpServer.Save();

                _logger.Log($"✅ Site {name} created successfully with PHP-FPM isolation");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to create site {name}: {e.Message}");
                // Cleanup on failure
                await DeletePhpFpmPool(name);
                return false;
            }
        }

        /// <summary>
        /// Deletes an existing website
        /// </summary>
        /// <param name="name">The domain of the website to delete</param>
        /// <returns>True if the site was deleted successfully, false otherwise</returns>
        public async Task<bool> DeleteSite(string name)
        {
            var sitePath = "/var/www/" + name;

            if (!Directory.Exists(sitePath))
                return false;

            try
            {
                string siteConf = "/etc/nginx/sites-available/" + name + ".conf";

                // 1. Remove nginx config
                File.Delete(siteConf);
                File.Delete("/etc/nginx/sites-enabled/" + name + ".conf");

                // 2. Remove PHP-FPM pool
                await DeletePhpFpmPool(name);

                // 3. Remove site directory
                await _nginx.RunCommand("sudo", $"rm -rf {sitePath}");
                await _nginx.RunCommand("sudo", "systemctl reload nginx");

                // 4. Remove from sites list
                sites.Remove(name);
                await SaveSites();

                // 5. Remove SFTP user
                if (_sftpServer.userCredentials.TryGetValue(name, out _))
                    _sftpServer.userCredentials.Remove(name);
                if (_sftpServer.usersFolders.TryGetValue(name, out _))
                    _sftpServer.usersFolders.Remove(name);
                await _sftpServer.Save();

                _logger.Log($"✅ Site {name} deleted successfully");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to delete site {name}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a PHP-FPM pool configuration for a specific site
        /// </summary>
        /// <param name="siteName">The domain of the site</param>
        /// <param name="sitePath">The file path of the site</param>
        private async Task CreatePhpFpmPool(string siteName, string sitePath)
        {
            // 5. Replace with SSL config
            var poolConfig = _nginx.poolConfig
                .Replace("%SiteName%", siteName)
                .Replace("%SitePath%", sitePath);

            var poolFilePath = $"/etc/php/8.3/fpm/pool.d/{siteName}.conf";

            // Write pool configuration
            await File.WriteAllTextAsync(poolFilePath, poolConfig);

            // Restart PHP-FPM to load the new pool
            await _nginx.RunCommand("sudo", "systemctl restart php8.3-fpm");
        }

        /// <summary>
        /// Deletes a PHP-FPM pool configuration for a specific site
        /// </summary>
        /// <param name="siteName">The name of the site</param>
        private async Task DeletePhpFpmPool(string siteName)
        {
            var poolFilePath = $"/etc/php/8.3/fpm/pool.d/{siteName}.conf";

            if (File.Exists(poolFilePath))
            {
                File.Delete(poolFilePath);
                await _nginx.RunCommand("sudo", "systemctl restart php8.3-fpm");
                _logger.Log($"✅ PHP-FPM pool deleted for {siteName}");
            }
        }

        /// <summary>
        /// Saves the sites configuration to a JSON file
        /// </summary>
        public async Task SaveSites()
        {
            var _sites = JsonConvert.SerializeObject(sites);
            await File.WriteAllTextAsync(sitesConfig, _sites);
        }

        /// <summary>
        /// Loads the sites configuration from a JSON file
        /// </summary>
        /// <returns>Dictionary containing site names and their ports</returns>
        public async Task<Dictionary<string, int>> LoadSites()
        {
            if (File.Exists(sitesConfig))
            {
                var _sites = JsonConvert.DeserializeObject<Dictionary<string, int>>(await File.ReadAllTextAsync(sitesConfig));
                if (_sites == null) return new Dictionary<string, int>();
                else return _sites;
            }
            return new Dictionary<string, int>();
        }

        /// <summary>
        /// Recursively copies a directory and its contents to another location
        /// </summary>
        /// <param name="sourceDir">The source directory path</param>
        /// <param name="destinationDir">The destination directory path</param>
        static void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destinationDir, Path.GetFileName(directory));
                CopyDirectory(directory, destSubDir);
            }
        }
    }

    /// <summary>
    /// Utility class for generating simple random passwords
    /// </summary>
    public static class SimplePasswordGenerator
    {
        private const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*";

        /// <summary>
        /// Generates a random password with the specified length
        /// </summary>
        /// <param name="length">The length of the password to generate (default: 16)</param>
        /// <returns>A randomly generated password</returns>
        public static string Generate(int length = 16)
        {
            var random = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                random.Append(Chars[Random.Shared.Next(Chars.Length)]);
            }
            return random.ToString();
        }
    }
}