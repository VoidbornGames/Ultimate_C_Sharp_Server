using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using UltimateServer.Models;
using UltimateServer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Server.ServerTemplates;

namespace Server.Services
{
    public class HyperServerManager : IDisposable
    {

        private Dictionary<string, IServerTemplate> _servers;
        public List<IServerTemplate> _templates;
        private readonly object _serversLock = new object(); // For thread safety

        private readonly Logger _logger;
        private readonly SftpServer _sftpServer;
        private readonly DataBox _dataBox;
        private readonly ServerConfig _config;
        private bool _disposed = false;


        public HyperServerManager(Logger logger, SftpServer sftpServer, DataBox dataBox, ServerConfig config, IServiceProvider provider)
        {
            _logger = logger;
            _sftpServer = sftpServer;
            _dataBox = dataBox;
            _config = config;

            _servers = new Dictionary<string, IServerTemplate>();
            _templates = new List<IServerTemplate>();

            _templates.Add(new MinecraftPaper("Minecraft Paper", "/var/UltimateServer/Servers/", "1.20.4", [], provider));
        }

        public async Task Start()
        {
            try
            {
                _servers = await Load();
                _logger.Log($"🖥️ HyperServerManager started with {_servers.Count} servers");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to load servers from DataBox: {e.Message}");
                _servers = new Dictionary<string, IServerTemplate>();
            }
        }

        public async Task StopAsync()
        {
            try
            {
                // Stop all running servers
                var stopTasks = _servers.Values.Select(server => server.StopServer());
                await Task.WhenAll(stopTasks);

                // Save server data
                await Save();
                _logger.Log("🖥️ HyperServerManager stopped successfully");
            }
            catch (Exception e)
            {
                _logger.LogError($"Error during shutdown: {e.Message}");
            }
        }

        public async Task Save()
        {
            try
            {
                // Use a versioned key to prevent incompatibility
                await _dataBox.SaveData("servers-18572", _servers);

                // Only log debug messages if DebugMode is enabled in the config
                if (_config.DebugMode)
                    _logger.Log("🖥️ Server data saved successfully");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to save server data: {e.Message}");
                throw;
            }
        }

        public async Task<Dictionary<string, IServerTemplate>> Load()
        {
            try
            {
                if (!await _dataBox.ContainsKey("servers-18572"))
                    return new Dictionary<string, IServerTemplate>();

                var data = await _dataBox.LoadData<Dictionary<string, IServerTemplate>>("servers-18572");
                if (data != null)
                    return data;
                else
                    return new Dictionary<string, IServerTemplate>();
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to load server data: {e.Message}");
                return new Dictionary<string, IServerTemplate>();
            }
        }

        // Added method to refresh templates from disk or other source
        public async Task RefreshTemplates()
        {
            try
            {
                if (_config.DebugMode)
                    _logger.Log("🖥️ Refreshing templates");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to refresh templates: {e.Message}");
            }
        }

        public async Task<bool> CreateServer(string serverName, string serverVersion,
            string serverPath, int[] allowedPorts,
            int maxRamMB, IServerTemplate template)
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(serverName))
            {
                if (_config.DebugMode) _logger.LogError("Server name cannot be null or empty");
                return false;
            }

            if (string.IsNullOrWhiteSpace(serverPath))
            {
                if (_config.DebugMode) _logger.LogError("Server path cannot be null or empty");
                return false;
            }

            if (template == null)
            {
                if (_config.DebugMode) _logger.LogError("Template cannot be null");
                return false;
            }

            // Check if server already exists
            lock (_serversLock)
            {
                if (_servers.ContainsKey(serverName))
                {
                    if (_config.DebugMode) _logger.LogError($"Server with name '{serverName}' already exists");
                    return false;
                }
            }

            try
            {
                // Set server-specific properties
                template.Name = serverName;
                template.Version = serverVersion;
                template.AllowedPorts = allowedPorts ?? new int[Random.Shared.Next(10000, 40000)];
                template.MaxRamMB = maxRamMB;
                template.ServerPath = serverPath;

                // Add to servers collection
                lock (_serversLock)
                {
                    _servers.Add(serverName, template);
                }

                if (!Directory.Exists(serverPath)) Directory.CreateDirectory(serverPath);

                // Setup server
                await _servers[serverName].DownloadServerFiles();
                await _servers[serverName].InstallServerFiles();
                await _servers[serverName].RunServer();

                // SFTP setup
                var pass = SimplePasswordGenerator.Generate();
                _sftpServer.usersFolders.Add(serverName, serverName);
                _sftpServer.userCredentials.Add(serverName, pass);
                _logger.Log($"HyperServers: SFTP user {serverName} has been created with password: {pass}");
                await _sftpServer.Save();

                // Save the updated servers dictionary
                await Save();

                _logger.Log($"🖥️ Server {serverName} has been created and is running inside path: {serverPath}");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to create server '{serverName}': {e.Message}");

                // Clean up partial creation
                lock (_serversLock)
                {
                    _servers.Remove(serverName);
                }

                return false;
            }
        }

        public async Task<bool> UninstallServer(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                _logger.LogError("Server name cannot be null or empty");
                return false;
            }

            IServerTemplate server;
            lock (_serversLock)
            {
                if (!_servers.TryGetValue(serverName, out server))
                {
                    _logger.LogError($"Server '{serverName}' not found");
                    return false;
                }
            }

            try
            {
                await server.UninstallServer();

                // Remove SFTP user
                _sftpServer.userCredentials.Remove(serverName);
                _sftpServer.usersFolders.Remove(serverName);
                await _sftpServer.Save();

                // Remove from servers dictionary
                lock (_serversLock)
                {
                    _servers.Remove(serverName);
                }

                // Save the updated servers dictionary
                await Save();

                _logger.Log($"Server '{serverName}' has been uninstalled successfully");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to uninstall server '{serverName}': {e.Message}");
                return false;
            }
        }

        public async Task<bool> RunServer(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                _logger.LogError("Server name cannot be null or empty");
                return false;
            }

            IServerTemplate server;
            lock (_serversLock)
            {
                if (!_servers.TryGetValue(serverName, out server))
                {
                    _logger.LogError($"Server '{serverName}' not found");
                    return false;
                }
            }

            try
            {
                await server.RunServer();
                _logger.Log($"Server '{serverName}' has been started");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to start server '{serverName}': {e.Message}");
                return false;
            }
        }

        public async Task<bool> StopServer(string serverName)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                _logger.LogError("Server name cannot be null or empty");
                return false;
            }

            IServerTemplate server;
            lock (_serversLock)
            {
                if (!_servers.TryGetValue(serverName, out server))
                {
                    _logger.LogError($"Server '{serverName}' not found");
                    return false;
                }
            }

            try
            {
                await server.StopServer();
                _logger.Log($"Server '{serverName}' has been stopped");
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to stop server '{serverName}': {e.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _logger.Log("HyperServerManager disposed");
                }

                _disposed = true;
            }
        }
    }
}