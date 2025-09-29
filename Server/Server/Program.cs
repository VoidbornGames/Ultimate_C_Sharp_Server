using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UltimateServer
{
    class Program
    {
        private static int Port = 11001;
        private static int webPort = 11002;
        private const string UsersFile = "users.json";
        private const string ConfigFile = "config.json";
        private const string LogsFolder = "logs";
        private const string LogFile = "logs/latest.log";
        private static readonly object logLock = new();
        private static readonly object userLock = new();

        public static List<User> Users = new();
        public static ServerConfig Config = new();
        private static TcpListener? listener;
        private static HttpListener? httpListener;
        private static CancellationTokenSource cts = new();

        private static ConcurrentDictionary<TcpClient, bool> activeClients = new();
        private static readonly Dictionary<string, Func<Data, Data>> CommandHandlers =
            new(StringComparer.OrdinalIgnoreCase);

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
                Port = parsedPort;
            if (args.Length > 1 && int.TryParse(args[1], out int parsedPortWeb))
                webPort = parsedPortWeb;

            PrepareLogs();
            await LoadUsersAsync();
            LoadConfig();
            RegisterCommands();
            StartHttpServer(webPort);

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), cts.Token);
                    await SaveUsersAsync();
                }
            });

            listener = new TcpListener(IPAddress.Parse(Config.Ip), Port);
            listener.Start();
            Log($"🚀 Server listening on {Config.Ip}:{Port} (Max {Config.MaxConnections} clients)");

            Console.CancelKeyPress += async (s, e) =>
            {
                e.Cancel = true;
                Log("🛑 Shutdown requested...");
                cts.Cancel();
                listener.Stop();
                await SaveUsersAsync();
                Environment.Exit(0);
            };

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    Log($"🔹 New client connected: {client.Client.RemoteEndPoint}");

                    if (activeClients.Count >= Config.MaxConnections)
                    {
                        Log($"⚠️ Connection refused: max clients reached.");
                        client.Close();
                        continue;
                    }

                    activeClients[client] = true;

                    _ = HandleClient(client, cts.Token).ContinueWith(t =>
                    {
                        activeClients.TryRemove(client, out _);
                        Log($"🔹 Client removed: {client.Client.RemoteEndPoint}");
                    });
                }
                catch (OperationCanceledException) { break; }
            }
        }

        static void StartHttpServer(int httpPort)
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://*:{httpPort}/");
            httpListener.Start();
            Log($"🌐 HTTP server listening on port {httpPort}");

            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var context = await httpListener.GetContextAsync();
                    var request = context.Request;
                    var response = context.Response;

                    response.AddHeader("Access-Control-Allow-Origin", "*");

                    try
                    {
                        switch (request.Url.AbsolutePath)
                        {
                            case "/stats":
                                var stats = new
                                {
                                    uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
                                    users = Users.Count,
                                    maxConnections = Config.MaxConnections,
                                    protocol = 1
                                };
                                await WriteJsonResponse(response, stats);
                                break;

                            case "/system":
                                var proc = Process.GetCurrentProcess();
                                double cpuUsage = 0.02;
                                double memUsage = proc.WorkingSet64 / 1024.0 / 1024.0; // MB
                                var systemStats = new { cpuUsage, memoryMB = memUsage };
                                await WriteJsonResponse(response, systemStats);
                                break;

                            case "/logs":
                                string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "latest.log");
                                string[] lines = File.Exists(logFile)
                                    ? File.ReadLines(logFile).Reverse().Take(50).Reverse().ToArray()
                                    : Array.Empty<string>();
                                await WriteJsonResponse(response, lines);
                                break;

                            default:
                                string htmlPath = Path.Combine(AppContext.BaseDirectory, "index.html");
                                if (File.Exists(htmlPath))
                                {
                                    string html = await File.ReadAllTextAsync(htmlPath);
                                    byte[] buffer = Encoding.UTF8.GetBytes(html);
                                    response.ContentType = "text/html";
                                    response.ContentLength64 = buffer.Length;
                                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                                }
                                else
                                {
                                    response.StatusCode = 404;
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("HTTP server error: " + ex.Message);
                        response.StatusCode = 500;
                    }
                    finally
                    {
                        response.OutputStream.Close();
                    }
                }
            }, cts.Token);
        }

        static async Task WriteJsonResponse(HttpListenerResponse response, object data)
        {
            string json = JsonSerializer.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        static void PrepareLogs()
        {
            try
            {
                if (!Directory.Exists(LogsFolder)) Directory.CreateDirectory(LogsFolder);

                if (File.Exists(LogFile))
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string zipFile = Path.Combine(LogsFolder, $"latest_{timestamp}.zip");

                    using var archive = ZipFile.Open(zipFile, ZipArchiveMode.Create);
                    archive.CreateEntryFromFile(LogFile, "latest.log");

                    File.Delete(LogFile);
                    File.Create(LogFile).Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error preparing logs: {ex.Message}");
            }
        }

        static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile);
                    Config = JsonSerializer.Deserialize<ServerConfig>(json) ?? new ServerConfig();
                }
                else
                {
                    Config = new ServerConfig();
                    File.WriteAllText(ConfigFile,
                        JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
                }
                Log("✅ Config loaded.");
            }
            catch (Exception ex)
            {
                LogError($"Error loading config: {ex.Message}");
            }
        }

        static async Task LoadUsersAsync()
        {
            try
            {
                if (File.Exists(UsersFile))
                {
                    string json = await File.ReadAllTextAsync(UsersFile);
                    Users = JsonSerializer.Deserialize<List<User>>(json) ?? new List<User>();
                }
                else Users = new List<User>();

                Log($"✅ Loaded {Users.Count} users.");
            }
            catch (Exception ex)
            {
                LogError($"Error loading users: {ex.Message}");
                Users = new List<User>();
            }
        }

        static async Task SaveUsersAsync()
        {
            try
            {
                string json = JsonSerializer.Serialize(Users, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(UsersFile, json);
                Log("💾 Users saved.");
            }
            catch (Exception ex)
            {
                LogError($"Error saving users: {ex.Message}");
            }
        }

        static async Task HandleClient(TcpClient client, CancellationToken token)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            Log($"🔹 Client connected: {client.Client.RemoteEndPoint}");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;

                    Log($"📥 Received: {line}");

                    Data? request = null;
                    try { request = JsonSerializer.Deserialize<Data>(line); } catch { }

                    if (request == null)
                    {
                        await SendResponse(writer, new Data { theCommand = "error", jsonData = "Invalid JSON" });
                        continue;
                    }

                    if (!CommandHandlers.TryGetValue(request.theCommand, out var handler))
                    {
                        await SendResponse(writer, new Data { theCommand = "error", jsonData = $"Unknown command: {request.theCommand}" });
                        continue;
                    }

                    var response = handler(request);
                    await SendResponse(writer, response);
                }
            }
            catch (Exception ex)
            {
                LogError($"Client error: {ex.Message}");
            }
            finally
            {
                activeClients.TryRemove(client, out _);
                Log($"🔹 Client disconnected: {client.Client.RemoteEndPoint}");
            }
        }

        static void RegisterCommands()
        {
            AddCommand("createUser", (req) =>
            {
                var newUser = JsonSerializer.Deserialize<User>(req.jsonData);
                lock (userLock)
                {
                    if (newUser != null && !Users.Exists(u => u.Username == newUser.Username))
                    {
                        Users.Add(newUser);
                        _ = SaveUsersAsync();
                        Log($"✅ User created: {newUser.Username}");
                        return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = $"User {newUser.Username} created." };
                    }
                }
                return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = "User exists or invalid data." };
            });

            AddCommand("loginUser", (req) =>
            {
                var newUser = JsonSerializer.Deserialize<User>(req.jsonData);
                if (newUser != null && Users.Exists(u => u.Username == newUser.Username))
                {
                    Log($"✅ User Login: {newUser.Username}");
                    return new Data { protocolVersion = 1, theCommand = "loginUser", jsonData = "{ \"Succeed\": \"true\" }" };
                }
                return new Data { protocolVersion = 1, theCommand = "loginUser", jsonData = "Invalid login" };
            });

            AddCommand("listUsers", (req) =>
            {
                lock (userLock)
                {
                    string usersJson = JsonSerializer.Serialize(Users);
                    Log("📄 Sent user list.");
                    return new Data { protocolVersion = 1, theCommand = "listUsers", jsonData = usersJson };
                }
            });

            AddCommand("say", (req) =>
            {
                Log($"🗨️ Client says: {req.jsonData}");
                return new Data { protocolVersion = 1, theCommand = "reply", jsonData = $"Server received: {req.jsonData}" };
            });

            AddCommand("makeUUID", (req) =>
            {
                var uuid = Guid.NewGuid();
                Log($"🆔 Generated UUID: {uuid}");
                return new Data { protocolVersion = 1, theCommand = "uuid", jsonData = uuid.ToString() };
            });

            AddCommand("stats", (req) =>
            {
                var stats = new
                {
                    uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(),
                    users = Users.Count,
                    protocol = 1
                };
                return new Data { protocolVersion = 1, theCommand = "stats", jsonData = JsonSerializer.Serialize(stats) };
            });
        }

        static void AddCommand(string name, Func<Data, Data> handler) => CommandHandlers[name] = handler;

        static async Task SendResponse(StreamWriter writer, Data data)
        {
            string json = JsonSerializer.Serialize(data);
            await writer.WriteLineAsync(json);
            Log($"📤 Sent: {json}");
        }

        static void Log(string message) => WriteLog("INFO", message);
        static void LogError(string message) => WriteLog("ERROR", message);

        static void WriteLog(string level, string message)
        {
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            Console.WriteLine(logEntry);
            lock (logLock) { File.AppendAllTextAsync(LogFile, logEntry + Environment.NewLine); }
        }
    }

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
        public int Score { get; set; } = 0;
        public string Role { get; set; } = "player";
    }

    public class ServerConfig
    {
        public string Ip { get; set; } = "0.0.0.0";
        public int MaxConnections { get; set; } = 50;
    }
}
