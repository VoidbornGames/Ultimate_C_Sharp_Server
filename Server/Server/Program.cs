using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Web; // Add this for HttpUtility
using Newtonsoft.Json;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

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
        private const string VideosFolder = "videos";
        private static readonly object logLock = new();
        private static readonly object userLock = new();

        public static List<User> Users = new();
        public static ServerConfig Config = new();
        private static TcpListener? listener;
        private static HttpListener? httpListener;
        private static CancellationTokenSource cts = new();
        static double lastCpuUsage = 0;

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
            PrepareVideos();
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
            Log($"🚀 Server listening on {Port} (Max {Config.MaxConnections} clients)");

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

                    _ = Task.Run(async () =>
                    {
                        var proc = Process.GetCurrentProcess();
                        TimeSpan prevCpu = proc.TotalProcessorTime;
                        DateTime prevTime = DateTime.UtcNow;

                        while (!cts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(1000);
                            TimeSpan currCpu = proc.TotalProcessorTime;
                            DateTime currTime = DateTime.UtcNow;

                            double cpuUsedMs = (currCpu - prevCpu).TotalMilliseconds;
                            double elapsedMs = (currTime - prevTime).TotalMilliseconds;
                            int cores = Environment.ProcessorCount;

                            lastCpuUsage = Math.Round((cpuUsedMs / (elapsedMs * cores)) * 100, 2);

                            prevCpu = currCpu;
                            prevTime = currTime;
                        }
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
                    // Add preflight headers for CORS
                    if (request.HttpMethod == "OPTIONS")
                    {
                        response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                        response.StatusCode = 200;
                        response.Close();
                        continue;
                    }

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
                                double cpuUse = lastCpuUsage;

                                double memUsage = proc.WorkingSet64 / 1024.0 / 1024.0;
                                double memMax = 0;
                                if (!OperatingSystem.IsWindows())
                                {
                                    try
                                    {
                                        string memInfo = File.ReadAllText("/proc/meminfo");
                                        string totalLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal:"));
                                        string freeLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemAvailable:"));

                                        double totalMem = totalLine != null
                                            ? double.Parse(new string(totalLine.Where(c => char.IsDigit(c)).ToArray())) / 1024.0
                                            : memUsage;

                                        double freeMem = freeLine != null
                                            ? double.Parse(new string(freeLine.Where(c => char.IsDigit(c)).ToArray())) / 1024.0
                                            : 0;

                                        memMax = totalMem;
                                        memUsage = totalMem - freeMem;
                                    }
                                    catch
                                    {
                                        memMax = memUsage;
                                    }
                                }

                                DriveInfo drive = new DriveInfo("/");
                                long totalSpace = drive.TotalSize;
                                long usedSpace = totalSpace - drive.AvailableFreeSpace;
                                double diskPercent = (double)usedSpace / totalSpace * 100;

                                long totalBytesSent = 0;
                                long totalBytesReceived = 0;
                                foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                                {
                                    var nicStats = nic.GetIPv4Statistics();
                                    totalBytesSent += nicStats.BytesSent;
                                    totalBytesReceived += nicStats.BytesReceived;
                                }

                                var systemStats = new
                                {
                                    cpuUsage = cpuUse,
                                    memoryMB = memUsage,
                                    memoryMaxMB = memMax,
                                    diskUsedGB = usedSpace / (1024.0 * 1024 * 1024),
                                    diskTotalGB = totalSpace / (1024.0 * 1024 * 1024),
                                    diskPercent,
                                    netSentMB = totalBytesSent / (1024.0 * 1024),
                                    netReceivedMB = totalBytesReceived / (1024.0 * 1024)
                                };

                                await WriteJsonResponse(response, systemStats);
                                break;

                            case "/logs":
                                string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "latest.log");
                                string[] lines = File.Exists(logFile)
                                    ? File.ReadLines(logFile).Reverse().Take(50).Reverse().ToArray()
                                    : Array.Empty<string>();
                                await WriteJsonResponse(response, lines);
                                break;

                            // --- START OF CHUNKED UPLOAD LOGIC ---
                            case "/upload-chunk":
                                {
                                    if (request.HttpMethod == "POST")
                                    {
                                        string tempDir = Path.Combine(AppContext.BaseDirectory, "temp");
                                        Directory.CreateDirectory(tempDir);

                                        using var reader = new StreamReader(request.InputStream);
                                        string body = await reader.ReadToEndAsync();
                                        var chunkData = JsonConvert.DeserializeObject<ChunkData>(body);

                                        if (chunkData != null)
                                        {
                                            string chunkPath = Path.Combine(tempDir, chunkData.FileId, chunkData.ChunkIndex.ToString());
                                            Directory.CreateDirectory(Path.GetDirectoryName(chunkPath)!);
                                            await File.WriteAllBytesAsync(chunkPath, Convert.FromBase64String(chunkData.Data));
                                            await WriteStringResponse(response, "Chunk received");
                                        }
                                        else
                                        {
                                            response.StatusCode = 400;
                                            await WriteStringResponse(response, "Invalid chunk data");
                                        }
                                    }
                                    break;
                                }

                            case "/finalize-upload":
                                {
                                    if (request.HttpMethod == "POST")
                                    {
                                        using var reader = new StreamReader(request.InputStream);
                                        string body = await reader.ReadToEndAsync();
                                        var finalizeData = JsonConvert.DeserializeObject<FinalizeData>(body);

                                        if (finalizeData != null)
                                        {
                                            string videoDir = Path.Combine(AppContext.BaseDirectory, VideosFolder);
                                            Directory.CreateDirectory(videoDir);
                                            string finalFilePath = Path.Combine(videoDir, finalizeData.FileName);
                                            string tempDir = Path.Combine(AppContext.BaseDirectory, "temp", finalizeData.FileId);

                                            using (var finalStream = new FileStream(finalFilePath, FileMode.Create))
                                            {
                                                for (int i = 0; i < finalizeData.TotalChunks; i++)
                                                {
                                                    string chunkPath = Path.Combine(tempDir, i.ToString());
                                                    byte[] chunkBytes = await File.ReadAllBytesAsync(chunkPath);
                                                    await finalStream.WriteAsync(chunkBytes, 0, chunkBytes.Length);
                                                }
                                            }

                                            // Clean up temp files
                                            Directory.Delete(tempDir, true);
                                            Log($"✅ Chunked upload complete: {finalizeData.FileName}");
                                            await WriteStringResponse(response, $"Upload successful: {finalizeData.FileName}");
                                        }
                                        else
                                        {
                                            response.StatusCode = 400;
                                            await WriteStringResponse(response, "Invalid finalize data");
                                        }
                                    }
                                    break;
                                }
                            // --- END OF CHUNKED UPLOAD LOGIC ---

                            case "/videos":
                                {
                                    string videoDir = Path.Combine(AppContext.BaseDirectory, "videos");
                                    Directory.CreateDirectory(videoDir);

                                    if (request.HttpMethod == "GET")
                                    {
                                        string[] files = Directory.GetFiles(videoDir).Select(f => Path.GetFileName(f)).ToArray();
                                        await WriteJsonResponse(response, files);
                                    }
                                    break;
                                }

                            case "/upload-url":
                                {
                                    if (request.HttpMethod == "POST")
                                    {
                                        try
                                        {
                                            using var reader = new StreamReader(request.InputStream);
                                            string body = await reader.ReadToEndAsync();
                                            var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);
                                            if (json != null && json.TryGetValue("url", out var videoUrl))
                                            {
                                                string videoDir = Path.Combine(AppContext.BaseDirectory, "videos");
                                                Directory.CreateDirectory(videoDir);
                                                string fileName = Path.GetFileName(new Uri(videoUrl).LocalPath);
                                                if (string.IsNullOrEmpty(fileName)) fileName = "downloaded_" + DateTime.Now.Ticks + ".mp4";
                                                string filePath = Path.Combine(videoDir, fileName);

                                                using var httpClient = new HttpClient();
                                                using var responseStream = await httpClient.GetStreamAsync(videoUrl);
                                                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                                                await responseStream.CopyToAsync(fileStream);

                                                await WriteStringResponse(response, $"Download successful: {fileName}");
                                            }
                                            else
                                            {
                                                response.StatusCode = 400;
                                                await WriteStringResponse(response, "Invalid JSON body");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            LogError($"URL Download failed: {ex.Message}");
                                            response.StatusCode = 500;
                                            await WriteStringResponse(response, "Download failed: " + ex.Message);
                                        }
                                    }
                                    else
                                    {
                                        response.StatusCode = 405;
                                        await WriteStringResponse(response, "Only POST allowed");
                                    }
                                    break;
                                }

                            default:
                                if (request.Url.AbsolutePath.StartsWith("/videos/"))
                                {
                                    string fileName = request.Url.AbsolutePath.Substring("/videos/".Length);
                                    if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
                                    {
                                        response.StatusCode = 400;
                                        await WriteStringResponse(response, "Invalid filename");
                                        response.Close();
                                        break;
                                    }

                                    string filePath = Path.Combine(AppContext.BaseDirectory, VideosFolder, fileName);

                                    if (!File.Exists(filePath))
                                    {
                                        response.StatusCode = 404;
                                        response.Close();
                                        break;
                                    }

                                    try
                                    {
                                        string extension = Path.GetExtension(filePath).ToLowerInvariant();
                                        string contentType = extension switch
                                        {
                                            ".mp4" => "video/mp4",
                                            ".webm" => "video/webm",
                                            ".ogg" => "video/ogg",
                                            ".avi" => "video/x-msvideo",
                                            ".mov" => "video/quicktime",
                                            _ => "application/octet-stream"
                                        };

                                        response.ContentType = contentType;
                                        response.AddHeader("Accept-Ranges", "bytes");

                                        if (request.Headers["Range"] != null)
                                        {
                                            long fileLength = new FileInfo(filePath).Length;
                                            long start = 0;
                                            long end = fileLength - 1;

                                            string range = request.Headers["Range"].Replace("bytes=", "");
                                            string[] parts = range.Split('-');
                                            if (parts.Length > 0 && long.TryParse(parts[0], out long parsedStart))
                                                start = parsedStart;
                                            if (parts.Length > 1 && long.TryParse(parts[1], out long parsedEnd))
                                                end = parsedEnd;

                                            if (start >= fileLength || end >= fileLength || start > end)
                                            {
                                                response.StatusCode = 416;
                                                response.AddHeader("Content-Range", $"bytes */{fileLength}");
                                                response.Close();
                                                break;
                                            }

                                            long contentLength = end - start + 1;
                                            response.StatusCode = 206;
                                            response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                                            response.ContentLength64 = contentLength;

                                            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                            fs.Seek(start, SeekOrigin.Begin);
                                            byte[] buffer = new byte[4096];
                                            int bytesRead;
                                            long bytesRemaining = contentLength;

                                            while (bytesRemaining > 0 && (bytesRead = await fs.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, bytesRemaining))) > 0)
                                            {
                                                await response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                                                bytesRemaining -= bytesRead;
                                            }
                                        }
                                        else
                                        {
                                            response.ContentLength64 = new FileInfo(filePath).Length;
                                            using var fs = File.OpenRead(filePath);
                                            await fs.CopyToAsync(response.OutputStream);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        LogError($"Error serving video: {ex.Message}");
                                        response.StatusCode = 500;
                                    }
                                    finally
                                    {
                                        response.Close();
                                    }
                                }
                                else
                                {
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
                                    response.Close();
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"HTTP server error: {ex.Message}");
                        response.StatusCode = 500;
                    }
                    finally
                    {
                        // response.Close() is handled in each case
                    }
                }
            }, cts.Token);
        }

        static void PrepareVideos()
        {
            try
            {
                if (!Directory.Exists(VideosFolder)) Directory.CreateDirectory(VideosFolder);
                Log("✅ Videos folder ready.");
            }
            catch (Exception ex)
            {
                LogError($"Error preparing videos folder: {ex.Message}");
            }
        }

        static async Task WriteJsonResponse(HttpListenerResponse response, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        static async Task WriteStringResponse(HttpListenerResponse response, string text)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            response.ContentType = "text/plain";
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
                    Config = JsonConvert.DeserializeObject<ServerConfig>(json) ?? new ServerConfig();
                }
                else
                {
                    Config = new ServerConfig();
                    File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(Config, Formatting.Indented));
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
                    Users = JsonConvert.DeserializeObject<List<User>>(json) ?? new List<User>();
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
                string json = JsonConvert.SerializeObject(Users, Formatting.Indented);
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
                    try { request = JsonConvert.DeserializeObject<Data>(line); } catch { }

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
                var newUser = JsonConvert.DeserializeObject<User>(req.jsonData);
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
                var newUser = JsonConvert.DeserializeObject<User>(req.jsonData);
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
                    string usersJson = JsonConvert.SerializeObject(Users);
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
                return new Data { protocolVersion = 1, theCommand = "uuid", jsonData = JsonConvert.SerializeObject(uuid) };
            });

            AddCommand("stats", (req) =>
            {
                var stats = new
                {
                    uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(),
                    users = Users.Count,
                    protocol = 1
                };
                return new Data { protocolVersion = 1, theCommand = "stats", jsonData = JsonConvert.SerializeObject(stats) };
            });
        }

        static void AddCommand(string name, Func<Data, Data> handler) => CommandHandlers[name] = handler;

        static async Task SendResponse(StreamWriter writer, Data data)
        {
            string json = JsonConvert.SerializeObject(data);
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

    // --- NEW CLASSES FOR CHUNKED UPLOAD ---
    public class ChunkData
    {
        public string FileId { get; set; } = "";
        public int ChunkIndex { get; set; }
        public string Data { get; set; } = ""; // Base64 encoded data
    }

    public class FinalizeData
    {
        public string FileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public int TotalChunks { get; set; }
    }
    // --- END OF NEW CLASSES ---

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
        public Guid uuid { get; set; }
        public string Role { get; set; } = "player";
    }

    public class ServerConfig
    {
        public string Ip { get; set; } = "0.0.0.0";
        public int MaxConnections { get; set; } = 50;
        public string DashboardPasswordHash { get; set; } = "12345678";
    }
}
