using Newtonsoft.Json;
using System.Text;
using System.Xml.Linq;
using UltimateServer.Services;

namespace Server.Services
{
    class SitePress
    {
        private Logger _logger;
        private Nginx _nginx;
        private SftpServer _sftpServer;

        private string templateFolder;
        private string sitesConfig = "sites.json";

        public Dictionary<string, int> sites;

        public SitePress(Logger logger, Nginx nginx, SftpServer sftpServer)
        {
            _logger = logger;
            _nginx = nginx;
            _sftpServer = sftpServer;
            templateFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Template");
        }

        public async Task Start()
        {
            sites = await LoadSites();
            _logger.Log("🎨 SitePress has been started!");
        }

        public async Task StopAsync()
        {
            await SaveSites();
            _logger.Log("🎨 SitePress stopped");
        }

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

        public async Task SaveSites()
        {
            var _sites = JsonConvert.SerializeObject(sites);
            await File.WriteAllTextAsync(sitesConfig, _sites);
        }

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

    public static class SimplePasswordGenerator
    {
        private const string Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*";

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