using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UltimateServer
{
    class Program
    {
        private static int Port = 11001;
        private const string UsersFile = "users.json";
        private const string ConfigFile = "config.json";
        private const string LogsFolder = "logs";
        private const string LogFile = "logs/latest.log";
        private static readonly object logLock = new();
        private static readonly object userLock = new();

        public static List<User> Users = new();
        public static ServerConfig Config = new();
        private static TcpListener? listener;
        private static CancellationTokenSource cts = new();

        private static ConcurrentDictionary<TcpClient, bool> activeClients = new();
        private static readonly Dictionary<string, Func<Data, Data>> CommandHandlers =
            new(StringComparer.OrdinalIgnoreCase);

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && int.TryParse(args[0], out int parsedPort))
                Port = parsedPort;
            else
                Port = 11001;

            PrepareLogs();
            await LoadUsersAsync();
            LoadConfig();
            RegisterCommands();
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
                        Log($"⚠️ Connection ip refused: max clients reached.");
                        client.Close();
                        continue;
                    }

                    activeClients[client] = true; // Add client to active list

                    _ = HandleClient(client, cts.Token).ContinueWith(t =>
                    {
                        activeClients.TryRemove(client, out _); // Remove when done
                        Log($"🔹 Client removed from active list: {client.Client.RemoteEndPoint}");
                    });
                }
                catch (OperationCanceledException) { break; }
            }
        }

        static void PrepareLogs()
        {
            try
            {
                if (!Directory.Exists(LogsFolder))
                    Directory.CreateDirectory(LogsFolder);

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
                    File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true }));
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
                else
                {
                    Users = new List<User>();
                }
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
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                stream.ReadTimeout = 30000; // 10s idle timeout

                try
                {
                    int byteCount = await stream.ReadAsync(buffer, token);
                    if (byteCount == 0) { Log("⚠️ Client disconnected immediately."); return; }

                    if (byteCount > buffer.Length) { LogError("Payload too large."); return; }

                    string received = Encoding.UTF8.GetString(buffer, 0, byteCount);
                    Log($"📥 Raw: {received}");

                    var request = JsonSerializer.Deserialize<Data>(received);
                    if (request == null) { Log("⚠️ Failed to parse request."); return; }

                    if (!CommandHandlers.TryGetValue(request.theCommand, out var handler))
                    {
                        await SendResponse(stream, new Data { protocolVersion = 1, theCommand = "error", jsonData = $"Unknown command: {request.theCommand}" });
                        return;
                    }

                    var response = handler(request);
                    await SendResponse(stream, response);
                }
                catch (Exception ex)
                {
                    LogError($"Client error: {ex.Message}");
                }
                finally
                {
                    Log($"🔹 Client disconnected: {client.Client.RemoteEndPoint}");
                }
            }
        }

        static void RegisterCommands()
        {
            CommandHandlers["createUser"] = (req) =>
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
                return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = "User already exists or invalid data." };
            };

            CommandHandlers["loginUser"] = (req) =>
            {
                var newUser = JsonSerializer.Deserialize<User>(req.jsonData);
                if (newUser != null && Users.Exists(u => u.Username == newUser.Username))
                {
                    Log($"✅ User Login: {newUser.Username}");
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = $"User {newUser.Username} created." };
                }
                return new Data { protocolVersion = 1, theCommand = "logedIn", jsonData = "{ \"Succeed\": \"true\" }" };
            };

            CommandHandlers["listUsers"] = (req) =>
            {
                lock (userLock)
                {
                    string usersJson = JsonSerializer.Serialize(Users);
                    Log("📄 Sent user list.");
                    return new Data { protocolVersion = 1, theCommand = "listUsers", jsonData = usersJson };
                }
            };

            CommandHandlers["say"] = (req) =>
            {
                Log($"🗨️ Client says: {req.jsonData}");
                return new Data { protocolVersion = 1, theCommand = "reply", jsonData = $"Server received: {req.jsonData}" };
            };

            CommandHandlers["makeUUID"] = (req) =>
            {
                var uuid = Guid.NewGuid();
                Log($"🆔 Generated UUID: {uuid}");
                return new Data { protocolVersion = 1, theCommand = "uuid", jsonData = uuid.ToString() };
            };

            CommandHandlers["stats"] = (req) =>
            {
                var stats = new
                {
                    uptime = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(),
                    users = Users.Count,
                    protocol = 1
                };
                return new Data { protocolVersion = 1, theCommand = "stats", jsonData = JsonSerializer.Serialize(stats) };
            };
        }

        static async Task SendResponse(NetworkStream stream, Data data)
        {
            string json = JsonSerializer.Serialize(data);
            byte[] responseBytes = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(responseBytes);
            Log($"📤 Sent response: {json}");
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
