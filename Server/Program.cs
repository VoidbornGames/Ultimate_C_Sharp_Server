using Microsoft.Extensions.DependencyInjection;
using UltimateServer;
using UltimateServer.Servers;
using UltimateServer.Services;
using UltimateServer.Events;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer
{
    class Program
    {
        private static int tcpPort = 11001;
        private static int httpPort = 11002;
        private static int udpPort = 11003;
        private static int sftpPort = 11004;
        private static CancellationTokenSource cts = new();
        private const string JwtSecret = "your-super-secret-jwt-key-change-this-in-production-32-chars-min";

        static async Task Main(string[] args)
        {
            // Stating the app with args: dotnet Server.dll 11001 11002 11003 11004
            //                                        args: arg-1 arg-2 arg-3 arg-4

            // Gets the TCP port from the arg-1 if exist
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
                tcpPort = parsedPort;
            // Gets the HTTP port from the arg-2 if exist
            if (args.Length > 1 && int.TryParse(args[1], out int parsedPortWeb))
                httpPort = parsedPortWeb;
            // Gets the UDP port from the arg-3 if exist
            if (args.Length > 2 && int.TryParse(args[2], out int parsedVoicePort))
                udpPort = parsedVoicePort;
            // Gets the SFTP port from the arg-4 if exist
            if (args.Length > 3 && int.TryParse(args[3], out int parsedSftpPort))
                sftpPort = parsedSftpPort;

            // Create a Service Collection for easy management
            var services = new ServiceCollection();

            // --- REGISTER SINGLETON SERVICES ---
            services.AddSingleton<Logger>();
            services.AddSingleton(provider => new ConfigManager(logger: provider.GetRequiredService<Logger>()));
            services.AddSingleton(provider => new EmailService(config: provider.GetRequiredService<ConfigManager>().Config));
            services.AddSingleton(provider => provider.GetRequiredService<ConfigManager>().Config);
            services.AddSingleton<FilePaths>();
            services.AddSingleton(provider => new ServerSettings { tcpPort = tcpPort, httpPort = httpPort, udpPort = udpPort, sftpPort = sftpPort });
            services.AddSingleton<CacheService>();
            services.AddSingleton<IEventBus, InMemoryEventBus>();
            services.AddSingleton<Services.EventHandler>();
            services.AddSingleton<PluginManager>();
            services.AddSingleton<SitePress>();
            services.AddSingleton<SftpServer>();
            services.AddSingleton<DataBox>();
            services.AddSingleton<MiniDB>();
            services.AddSingleton<DataBackuper>();
            services.AddSingleton<WebSocketServer>();
            services.AddSingleton<UserService>();
            services.AddSingleton<HttpServer>();
            services.AddSingleton<TcpServer>();
            services.AddSingleton<UdpServer>();

            // --- REGISTER SCOPED SERVICES ---
            services.AddScoped<AuthenticationService>(provider =>
                new AuthenticationService(
                    JwtSecret,
                    provider.GetRequiredService<ServerConfig>(),
                    provider.GetRequiredService<Logger>()));

            services.AddScoped<ValidationService>();
            services.AddScoped<VideoService>();
            services.AddScoped<CommandHandler>();
            services.AddScoped<Nginx>();

            var serviceProvider = services.BuildServiceProvider();

            // --- INITIALIZE AND START SERVICES ---
            var logger = serviceProvider.GetRequiredService<Logger>();
            logger.PrepareLogs();
            var pluginManager = serviceProvider.GetRequiredService<PluginManager>();

            // --- SUBSCRIBE EVENT HANDLERS ---
            var eventBus = serviceProvider.GetRequiredService<IEventBus>();
            var eventHandler = serviceProvider.GetRequiredService<Services.EventHandler>();

            eventBus.Subscribe<UserRegisteredEvent>(eventHandler);
            eventBus.Subscribe<VideoUploadedEvent>(eventHandler);

            // --- INITIALIZE AND START CORE SERVICES ---
            var userService = serviceProvider.GetRequiredService<UserService>();
            var videoService = serviceProvider.GetRequiredService<VideoService>();
            var commandHandler = serviceProvider.GetRequiredService<CommandHandler>();
            var httpServer = serviceProvider.GetRequiredService<HttpServer>();
            var tcpServer = serviceProvider.GetRequiredService<TcpServer>();
            var udpServer = serviceProvider.GetRequiredService<UdpServer>();
            var sitePress = serviceProvider.GetRequiredService<SitePress>();
            var sftpServer = serviceProvider.GetRequiredService<SftpServer>();
            var dataBox = serviceProvider.GetRequiredService<DataBox>();
            var miniDB = serviceProvider.GetRequiredService<MiniDB>();
            var dataBackuper = serviceProvider.GetRequiredService<DataBackuper>();
            var webSocketServer = serviceProvider.GetRequiredService<WebSocketServer>();

            // DataBox and miniDB must be the first one to start becuase many of codes might use it for data saving!
            await miniDB.Start();
            await dataBox.Start();

            await userService.LoadUsersAsync();

            await httpServer.Start();
            await tcpServer.Start();
            await udpServer.Start();
            await sitePress.Start();
            await sftpServer.Start();
            await dataBackuper.Start();
            await webSocketServer.Start();

            // Load plugins AFTER starting the main servers
            Directory.Delete(Path.Combine("plugins", ".plugin_temp"), true);
            pluginManager._serviceProvider = serviceProvider;
            await pluginManager.LoadPluginsAsync();

            // Start the auto save proccess
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // A 5 minutes delay for save loop
                    await Task.Delay(TimeSpan.FromMinutes(5), cts.Token);

                    await userService.SaveUsersAsync();
                    await sitePress.SaveSites();
                    await sftpServer.Save();

                    // DataBox must be the last one to save becuase many of codes might use it for data saving!
                    await dataBox.Save();
                }
            });

            // Start the plugin update proccess
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // Every 1000ms all the plugins will get the OnUpdateAsync() call
                    await Task.Delay(1000, cts.Token);
                    await pluginManager.UpdateLoadedPluginsAsync(cts.Token);
                }
            });

            // Tell the user that the server started successfully
            logger.Log($"🚀 Server started successfully!");


            /// Stop everything correctly on shoutdown
            // Handle SIGINT (Ctrl+C)
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                ShutdownApplication(serviceProvider, logger, cts).GetAwaiter().GetResult();
            };

            // Handle SIGINT (Ctrl+C) for when running in a terminal
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                ShutdownApplication(serviceProvider, logger, cts).GetAwaiter().GetResult();
            };

            // --- ShutdownApplication method ---
            static async Task ShutdownApplication(ServiceProvider serviceProvider, Logger logger, CancellationTokenSource cts)
            {
                if (cts.IsCancellationRequested) return;

                cts.Cancel();
                logger.Log("🛑 Shutdown requested...");

                logger.Log("💾 Saving final data...");
                var userService = serviceProvider.GetRequiredService<UserService>();
                await userService.SaveUsersAsync();

                var sitePress = serviceProvider.GetRequiredService<SitePress>();
                await sitePress.SaveSites();

                var sftpServer = serviceProvider.GetRequiredService<SftpServer>();
                await sftpServer.Save();

                logger.Log("🛑 Stopping servers...");
                var httpServer = serviceProvider.GetRequiredService<HttpServer>();
                var tcpServer = serviceProvider.GetRequiredService<TcpServer>();
                var udpServer = serviceProvider.GetRequiredService<UdpServer>();
                var sitePressService = serviceProvider.GetRequiredService<SitePress>();
                var sftpServerService = serviceProvider.GetRequiredService<SftpServer>();
                var webSocketServer = serviceProvider.GetRequiredService<WebSocketServer>();
                var dataBox = serviceProvider.GetRequiredService<DataBox>();
                var miniDB = serviceProvider.GetRequiredService<MiniDB>();

                await httpServer.StopAsync();
                await tcpServer.StopAsync();
                await udpServer.StopAsync();
                await sitePressService.StopAsync();
                await sftpServerService.StopAsync();
                await webSocketServer.Stop();

                // DataBox and miniDB must be the last one to stop because many of codes might use it for data saving!
                await dataBox.Stop();
                await miniDB.Stop();

                logger.Log("✅ Shutdown complete.");
            }

            // Use the Default for building the project so there will be no need of .Net to run the app!
            // Default build command: dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=true -p:TrimMode=partial -p:InvariantGlobalization=true -p:DebugType=None -p:DebugSymbols=false -p:EnableDiagnostics=false -p:PublishReadyToRun=false -p:TieredCompilation=false -p:EventSourceSupport=false -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=false

            // Just an infinite delay so the server wont stop as soon as it starts
            await Task.Delay(Timeout.Infinite);
        }
    }
}
