using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using UltimateServer.Models;
using UltimateServer.Events;
using UltimateServer.Services;
using Server.Servers;

namespace UltimateServer
{
    class Program
    {
        private static int Port = 11001;
        private static int WebPort = 11002;
        private static int VoicePort = 11003;
        private static CancellationTokenSource cts = new();
        private const string JwtSecret = "your-super-secret-jwt-key-change-this-in-production-32-chars-min";

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
                Port = parsedPort;
            if (args.Length > 1 && int.TryParse(args[1], out int parsedPortWeb))
                WebPort = parsedPortWeb;
            if (args.Length > 2 && int.TryParse(args[2], out int parsedVoicePort))
                VoicePort = parsedVoicePort;

            var services = new ServiceCollection();

            // --- REGISTER SINGLETON SERVICES ---
            services.AddSingleton<Logger>();
            services.AddSingleton(provider => new ConfigManager(logger: provider.GetRequiredService<Logger>()));
            services.AddSingleton(provider => provider.GetRequiredService<ConfigManager>().Config);
            services.AddSingleton<FilePaths>();
            services.AddSingleton(provider => new ServerSettings { Port = Port, WebPort = WebPort, VoicePort = VoicePort });
            services.AddSingleton<CacheService>();
            services.AddSingleton<IEventBus, InMemoryEventBus>();
            services.AddSingleton<NotificationService>();
            services.AddSingleton<PluginManager>(); // <-- NEW: Register Plugin Manager
            services.AddSingleton(provider => new EmailService(provider.GetRequiredService<ServerConfig>()));

            // --- REGISTER SCOPED SERVICES ---
            services.AddScoped<AuthenticationService>(provider =>
                new AuthenticationService(JwtSecret, provider.GetRequiredService<ServerConfig>(), provider.GetRequiredService<Logger>()));
            services.AddScoped<ValidationService>();
            services.AddScoped<UserService>();
            services.AddScoped<VideoService>();
            services.AddScoped<CommandHandler>();
            services.AddScoped<HttpServer>(); // HttpServer needs the PluginManager
            services.AddScoped<TcpServer>();
            services.AddScoped<UdpServer>();

            var serviceProvider = services.BuildServiceProvider();

            // --- INITIALIZE AND START SERVICES ---
            var logger = serviceProvider.GetRequiredService<Logger>();
            logger.PrepareLogs();
            var pluginManager = serviceProvider.GetRequiredService<PluginManager>(); // <-- NEW: Resolve Plugin Manager

            // Load plugins BEFORE starting the main servers
            await pluginManager.LoadPluginsAsync("plugins");

            // --- SUBSCRIBE EVENT HANDLERS ---
            var eventBus = serviceProvider.GetRequiredService<IEventBus>();
            var notificationService = serviceProvider.GetRequiredService<NotificationService>();
            eventBus.Subscribe<UserRegisteredEvent>(notificationService);
            eventBus.Subscribe<VideoUploadedEvent>(notificationService);

            // --- INITIALIZE AND START CORE SERVICES ---
            var userService = serviceProvider.GetRequiredService<UserService>();
            var videoService = serviceProvider.GetRequiredService<VideoService>();
            var commandHandler = serviceProvider.GetRequiredService<CommandHandler>();
            var httpServer = serviceProvider.GetRequiredService<HttpServer>();
            var tcpServer = serviceProvider.GetRequiredService<TcpServer>();
            var udpServer = serviceProvider.GetRequiredService<UdpServer>();

            await userService.LoadUsersAsync();
            httpServer.Start();
            tcpServer.Start();
            udpServer.Start();

            logger.Log($"🚀 Server started successfully. TCP on port {Port}, HTTP on port {WebPort}.");

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), cts.Token);
                    logger.Log("💾 Periodic save triggered.");
                    await userService.SaveUsersAsync();
                }
            });


            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                    await pluginManager.UpdateLoadedPluginsAsync(cts.Token);
                }
            });

            Console.CancelKeyPress += async (s, e) =>
            {
                e.Cancel = true;
                logger.Log("🛑 Shutdown requested...");
                cts.Cancel();

                logger.Log("💾 Saving final data...");
                await userService.SaveUsersAsync();

                logger.Log("🛑 Stopping servers...");
                await httpServer.StopAsync();
                await tcpServer.StopAsync();
                await udpServer.StopAsync();

                Environment.Exit(0);
            };

            // Sends The Server Start Message!
            // Currently its off to save emails.
            if (false)
                await serviceProvider.GetRequiredService<EmailService>().SendAsync(
                    "alirezajanaki33@gmail.com",
                    "UltimateServer",
                    "UltimateServer have been started! You can access it through: 'https://dashboard.voidborn-games.ir'",
                    false);
            await Task.Delay(Timeout.Infinite);
        }
    }
}