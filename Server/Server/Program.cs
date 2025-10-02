using System;
using System.Threading;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer
{
    class Program
    {
        private static int Port = 11001;
        private static int WebPort = 11002;
        private static CancellationTokenSource cts = new();

        // FIX: Added a constant for the JWT Secret
        private const string JwtSecret = "your-super-secret-jwt-key-change-this-in-production-32-chars-min";

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
                Port = parsedPort;
            if (args.Length > 1 && int.TryParse(args[1], out int parsedPortWeb))
                WebPort = parsedPortWeb;

            // Initialize services
            var logger = new Logger();
            var configManager = new ConfigManager(logger: logger);

            // FIX: Explicitly pass the required arguments to the AuthenticationService constructor
            var authService = new AuthenticationService(JwtSecret, configManager.Config, logger);

            var userService = new UserService(logger: logger, authService: authService);
            var videoService = new VideoService(logger: logger);
            var commandHandler = new CommandHandler(userService, logger);
            var httpServer = new HttpServer(WebPort, logger, userService, authService, videoService);
            var tcpServer = new TcpServer(Port, configManager.Config.Ip, logger, commandHandler);

            // Load initial data
            await userService.LoadUsersAsync();

            // Start servers
            httpServer.Start();
            tcpServer.Start();

            // Set up periodic save
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), cts.Token);
                    await userService.SaveUsersAsync();
                }
            });

            // Handle shutdown
            Console.CancelKeyPress += async (s, e) =>
            {
                e.Cancel = true;
                logger.Log("🛑 Shutdown requested...");
                cts.Cancel();
                await userService.SaveUsersAsync();
                await httpServer.StopAsync();
                await tcpServer.StopAsync();
                Environment.Exit(0);
            };

            // Keep the application running
            await Task.Delay(Timeout.Infinite);
        }
    }
}