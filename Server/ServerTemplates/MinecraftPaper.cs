using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.ServerTemplates
{
    class MinecraftPaper : IServerTemplate
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public int[] AllowedPorts { get; set; }
        public string ServerFilesDownloadLink { get; set; }
        public int MaxRamMB { get; set; } = 512;
        public string ServerPath { get; set; }
        public Process process { get; set; }

        private Nginx nginx;
        private Logger _logger;
        private ServerConfig config;

        public MinecraftPaper(string serverName, string serverPath, string serverVersion, int[] ports, IServiceProvider provider)
        {
            Name = serverName;
            ServerPath = serverPath;
            Version = serverVersion;
            AllowedPorts = ports;
            nginx = provider.GetRequiredService<Nginx>();
            _logger = provider.GetRequiredService<Logger>();
            config = provider.GetRequiredService<ConfigManager>().Config;
        }

        public async Task DownloadServerFiles()
        {
            if (!Directory.Exists(ServerPath))
                Directory.CreateDirectory(ServerPath);

            await File.WriteAllTextAsync(Path.Combine(ServerPath, "console.txt"), "");
            await File.WriteAllTextAsync(Path.Combine(ServerPath, "info.txt"),
                $"PaperMC {Version} (Docker)\nPort: {AllowedPorts[0]}\nMaxRAM: {MaxRamMB}MB");
        }

        public async Task InstallServerFiles()
        {
            await nginx.RunCommand("ufw", $"allow {AllowedPorts[0]}/tcp");
            await RunDockerCommand($"pull itzg/minecraft-server");
            await File.WriteAllTextAsync(Path.Combine(ServerPath, "eula.txt"), "eula=true\n");
        }

        public async Task RunServer()
        {
            string absPath = Path.GetFullPath(ServerPath);
            int port = AllowedPorts[0];
            string containerName = $"{Name}_{Version}_{port}";

            string dockerCmd =
                $"run -d -it --name {containerName} " +
                $"-p {port}:25565 " +
                $"-e EULA=TRUE -e TYPE=PAPER -e VERSION={Version} " +
                $"-e MEMORY={MaxRamMB}M " +
                $"--restart unless-stopped -v \"{absPath}:/data\" itzg/minecraft-server";

            if (config.DebugMode)
                _logger.Log($"Starting PaperMC {Version} in Docker on port {port}...");

            await RunDockerCommand(dockerCmd);
            await File.AppendAllTextAsync(Path.Combine(ServerPath, "console.txt"), $"[INFO] Server started via Docker\n");
        }

        public async Task StopServer()
        {
            int port = AllowedPorts[0];
            string containerName = $"{Name}_{Version}_{port}";

            try
            {
                if (config.DebugMode)
                    _logger.Log($"Stopping PaperMC server on port {port}...");

                await RunDockerCommand($"exec {containerName} rcon-cli stop");
                await Task.Delay(2000); // allow graceful shutdown
                await RunDockerCommand($"stop {containerName}");
                await RunDockerCommand($"rm {containerName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error stopping server: {ex.Message}");
            }
        }

        public async Task UninstallServer()
        {
            int port = AllowedPorts[0];
            string containerName = $"{Name}_{Version}_{port}";

            try
            {
                if (config.DebugMode)
                    _logger.Log($"Uninstalling PaperMC server {containerName}...");

                // Stop and remove Docker container (if exists)
                await RunDockerCommand($"stop {containerName}");
                await RunDockerCommand($"rm {containerName}");

                // Remove Docker volumes linked to this container (optional cleanup)
                await RunDockerCommand($"volume prune -f");

                // Remove local files
                if (Directory.Exists(ServerPath))
                {
                    Directory.Delete(ServerPath, true);
                    if (config.DebugMode)
                        _logger.Log($"Deleted local server files at {ServerPath}");
                }

                // Close firewall port
                await nginx.RunCommand("ufw", $"delete allow {port}/tcp");

                _logger.Log($"✅ Uninstalled Minecraft Paper server: {Name} ({Version}) on port {port}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uninstalling server: {ex.Message}");
            }
        }

        private async Task RunDockerCommand(string args)
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();

            if (config.DebugMode)
            {
                if (!string.IsNullOrWhiteSpace(output))
                    _logger.Log(output);
                if (!string.IsNullOrWhiteSpace(error))
                    _logger.LogError(error);
            }

            await process.WaitForExitAsync();
        }

        public async Task<string> GetConsoleOutput()
        {
            return await process.StandardOutput.ReadToEndAsync();
        }
    }
}