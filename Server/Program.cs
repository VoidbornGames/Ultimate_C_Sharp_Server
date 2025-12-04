using Microsoft.Extensions.DependencyInjection;
using Server;
using Server.Servers;
using Server.ServerTemplates;
using Server.Services;
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
            services.AddSingleton<HyperServerManager>();
            services.AddSingleton<MiniDB>();
            services.AddSingleton<DataBackuper>();

            // --- REGISTER SCOPED SERVICES ---
            services.AddScoped<AuthenticationService>(provider =>
                new AuthenticationService(
                    JwtSecret,
                    provider.GetRequiredService<ServerConfig>(),
                    provider.GetRequiredService<Logger>()));

            services.AddScoped<ValidationService>();
            services.AddScoped<UserService>();
            services.AddScoped<VideoService>();
            services.AddScoped<CommandHandler>();
            services.AddScoped<HttpServer>(); // HttpServer needs the PluginManager so add it after PluginManager
            services.AddScoped<TcpServer>();
            services.AddScoped<UdpServer>();
            services.AddScoped<SitePress>();
            services.AddScoped<Nginx>();
            services.AddScoped<SftpServer>();
            services.AddScoped<DataBox>();
            services.AddScoped<HyperServerManager>();
            services.AddScoped<MiniDB>();
            services.AddScoped<DataBackuper>();

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
            var hyperServerManager = serviceProvider.GetRequiredService<HyperServerManager>();
            var dataBox = serviceProvider.GetRequiredService<DataBox>();
            var miniDB = serviceProvider.GetRequiredService<MiniDB>();
            var dataBackuper = serviceProvider.GetRequiredService<DataBackuper>();

            // DataBox and miniDB must be the first one to start becuase many of codes might use it for data saving!
            await miniDB.Start();
            await dataBox.Start();

            await userService.LoadUsersAsync();

            await httpServer.Start();
            await tcpServer.Start();
            await udpServer.Start();
            await sitePress.Start();
            await sftpServer.Start();
            await hyperServerManager.Start();
            await dataBackuper.Start();

            // Load plugins AFTER starting the main servers
            pluginManager._serviceProvider = serviceProvider;
            await pluginManager.LoadPluginsAsync("plugins");

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
                    await hyperServerManager.Save();

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

            // Stop evrything correctly on shoutdown
            Console.CancelKeyPress += async (s, e) =>
            {
                // Cancel the default shoutdown event
                e.Cancel = true;

                // Run our own shoutdown
                cts.Cancel();
                logger.Log("🛑 Shutdown requested...");

                logger.Log("💾 Saving final data...");
                await userService.SaveUsersAsync();

                logger.Log("🛑 Stopping servers...");
                await httpServer.StopAsync();
                await tcpServer.StopAsync();
                await udpServer.StopAsync();
                await sitePress.StopAsync();
                await sftpServer.StopAsync();
                await hyperServerManager.StopAsync();


                // DataBox and miniDB must be the last one to stop becuase many of codes might use it for data saving!
                await dataBox.Stop();
                await miniDB.Stop();

                // A 300ms delay to be sure everything has been saved.
                await Task.Delay(300);
                Environment.Exit(0);
            };



            // Just an infinite delay so the server wont stop as soon as it starts
            await Task.Delay(Timeout.Infinite);


            // TODO: Finish the HyperServerManager panel and add it to the main panel.
        }
    }
}