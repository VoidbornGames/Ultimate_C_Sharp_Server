using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

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
        private static readonly string JwtSecret = "your-super-secret-jwt-key-change-this-in-production-32-chars-min";
        private static readonly SymmetricSecurityKey JwtKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));

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
                    if (request.HttpMethod == "OPTIONS")
                    {
                        response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                        response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                        response.StatusCode = 200;
                        response.Close();
                        continue;
                    }

                    try
                    {
                        switch (request.Url.AbsolutePath)
                        {
                            // Authentication endpoints
                            case "/api/login":
                                await HandleLogin(request, response);
                                break;

                            case "/api/verify-2fa":
                                await HandleTwoFactorVerification(request, response);
                                break;

                            // Protected endpoints - require authentication
                            case "/stats":
                                if (ValidateAuthentication(request))
                                    await HandleStats(request, response);
                                else
                                    SendUnauthorized(response);
                                break;

                            case "/system":
                                if (ValidateAuthentication(request))
                                    await HandleSystem(request, response);
                                else
                                    SendUnauthorized(response);
                                break;

                            case "/logs":
                                if (ValidateAuthentication(request))
                                    await HandleLogs(request, response);
                                else
                                    SendUnauthorized(response);
                                break;

                            case "/videos":
                                if (ValidateAuthentication(request))
                                    await HandleVideos(request, response);
                                else
                                    SendUnauthorized(response);
                                break;

                            case "/upload-url":
                                if (ValidateAuthentication(request))
                                    await HandleVideoUpload(request, response);
                                else
                                    SendUnauthorized(response);
                                break;

                            default:
                                if (request.Url.AbsolutePath.StartsWith("/videos/"))
                                {
                                    // Handle video requests with authentication
                                    if (ValidateAuthentication(request))
                                        await ServeVideo(request, response);
                                    else
                                        SendUnauthorized(response);
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
                }
            }, cts.Token);
        }

        static async Task HandleLogin(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponse(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var loginData = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                if (loginData != null &&
                    loginData.TryGetValue("username", out var username) &&
                    loginData.TryGetValue("password", out var password))
                {
                    // Find user in the database
                    var user = Users.FirstOrDefault(u => u.Username == username);

                    if (user != null && VerifyPassword(password, user.Password))
                    {
                        // Generate JWT token
                        string token = GenerateJwtToken(user);

                        var responseData = new
                        {
                            success = true,
                            token = token,
                            user = new { username = user.Username, role = user.Role }
                        };

                        await WriteJsonResponse(response, responseData);
                        Log($"✅ User logged in: {username}");
                    }
                    else
                    {
                        response.StatusCode = 401;
                        await WriteJsonResponse(response, new { success = false, message = "Invalid username or password" });
                        Log($"⚠️ Failed login attempt: {username}");
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponse(response, new { success = false, message = "Invalid request" });
                }
            }
            catch (Exception ex)
            {
                LogError($"Login error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { success = false, message = "Server error" });
            }
        }

        static async Task HandleTwoFactorVerification(HttpListenerRequest request, HttpListenerResponse response)
        {
            // Implementation for two-factor authentication
            // This is optional but recommended for higher security
            await WriteJsonResponse(response, new { success = false, message = "2FA not implemented" });
        }

        static bool ValidateAuthentication(HttpListenerRequest request)
        {
            string authHeader = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                return false;
            }

            string token = authHeader.Substring("Bearer ".Length);
            return ValidateJwtToken(token);
        }

        static void SendUnauthorized(HttpListenerResponse response)
        {
            response.StatusCode = 401;
            response.AddHeader("WWW-Authenticate", "Bearer");
            response.Close();
        }

        static async Task HandleStats(HttpListenerRequest request, HttpListenerResponse response)
        {
            var stats = new
            {
                uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
                users = Users.Count,
                maxConnections = Config.MaxConnections,
                protocol = 1
            };
            await WriteJsonResponse(response, stats);
        }

        static async Task HandleSystem(HttpListenerRequest request, HttpListenerResponse response)
        {
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
        }

        static async Task HandleLogs(HttpListenerRequest request, HttpListenerResponse response)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "latest.log");
            string[] lines = File.Exists(logFile)
                ? File.ReadLines(logFile).Reverse().Take(50).Reverse().ToArray()
                : Array.Empty<string>();
            await WriteJsonResponse(response, lines);
        }

        static async Task HandleVideos(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                Log($"📹 Handling videos request from: {request.RemoteEndPoint}");

                string videoDir = Path.Combine(AppContext.BaseDirectory, "videos");
                Directory.CreateDirectory(videoDir);

                if (request.HttpMethod == "GET")
                {
                    // Check if directory exists and is accessible
                    if (!Directory.Exists(videoDir))
                    {
                        LogError($"Videos directory does not exist: {videoDir}");
                        await WriteJsonResponse(response, new { success = false, message = "Videos directory not found" });
                        return;
                    }

                    try
                    {
                        string[] files = Directory.GetFiles(videoDir)
                            .Where(f => IsVideoFile(f))
                            .Select(f => Path.GetFileName(f))
                            .ToArray();

                        Log($"✅ Found {files.Length} video files");
                        await WriteJsonResponse(response, files);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        LogError($"Access denied to videos directory: {ex.Message}");
                        response.StatusCode = 403;
                        await WriteJsonResponse(response, new { success = false, message = "Access denied to videos directory" });
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        LogError($"Videos directory not found: {ex.Message}");
                        response.StatusCode = 404;
                        await WriteJsonResponse(response, new { success = false, message = "Videos directory not found" });
                    }
                }
                else
                {
                    response.StatusCode = 405;
                    await WriteJsonResponse(response, new { success = false, message = "Method not allowed" });
                }
            }
            catch (Exception ex)
            {
                LogError($"Error in HandleVideos: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { success = false, message = "Internal server error" });
            }
        }

        // Helper method to check if file is a video
        static bool IsVideoFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension == ".mp4" || extension == ".webm" || extension == ".ogg" ||
                   extension == ".avi" || extension == ".mov" || extension == ".mkv";
        }

        static async Task HandleVideoUpload(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponse(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                if (json != null && json.TryGetValue("url", out var videoUrl))
                {
                    if (string.IsNullOrWhiteSpace(videoUrl))
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponse(response, new { success = false, message = "URL cannot be empty" });
                        return;
                    }

                    string videoDir = Path.Combine(AppContext.BaseDirectory, "videos");
                    Directory.CreateDirectory(videoDir);

                    // Get file name from URL or generate one
                    string fileName = Path.GetFileName(new Uri(videoUrl).LocalPath);
                    if (string.IsNullOrEmpty(fileName) || !fileName.Contains('.'))
                    {
                        fileName = "downloaded_" + DateTime.Now.Ticks + ".mp4";
                    }

                    // Sanitize filename
                    fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                    string filePath = Path.Combine(videoDir, fileName);

                    Log($"📥 Starting download from: {videoUrl}");

                    using var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromMinutes(10); // 10 minute timeout

                    try
                    {
                        // Get the response headers first to check content type
                        using var responseMessage = await httpClient.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead);

                        if (!responseMessage.IsSuccessStatusCode)
                        {
                            LogError($"Download failed with status: {responseMessage.StatusCode}");
                            response.StatusCode = 400;
                            await WriteJsonResponse(response, new { success = false, message = $"Download failed: HTTP {responseMessage.StatusCode}" });
                            return;
                        }

                        // Check if it's a video content type
                        string contentType = responseMessage.Content.Headers.ContentType?.MediaType ?? "";
                        if (!contentType.StartsWith("video/") && !contentType.Contains("octet-stream"))
                        {
                            Log($"⚠️ Warning: Content type is {contentType}, but proceeding anyway");
                        }

                        // Download the file
                        using var responseStream = await responseMessage.Content.ReadAsStreamAsync();
                        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

                        long totalBytes = responseMessage.Content.Headers.ContentLength ?? 0;
                        long downloadedBytes = 0;
                        byte[] buffer = new byte[8192];
                        int bytesRead;

                        while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;

                            // Log progress for large files
                            if (totalBytes > 0 && downloadedBytes % (1024 * 1024) == 0) // Every MB
                            {
                                double progress = (double)downloadedBytes / totalBytes * 100;
                                Log($"📥 Download progress: {progress:F1}%");
                            }
                        }

                        await fileStream.FlushAsync();

                        // Verify file was downloaded
                        if (new FileInfo(filePath).Length > 0)
                        {
                            await WriteJsonResponse(response, new { success = true, message = $"Download successful: {fileName}" });
                            Log($"✅ Video downloaded successfully: {fileName} ({new FileInfo(filePath).Length} bytes)");
                        }
                        else
                        {
                            File.Delete(filePath); // Remove empty file
                            response.StatusCode = 500;
                            await WriteJsonResponse(response, new { success = false, message = "Downloaded file is empty" });
                            LogError("Downloaded file is empty");
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        LogError("Download timed out");
                        response.StatusCode = 408;
                        await WriteJsonResponse(response, new { success = false, message = "Download timed out" });
                    }
                    catch (HttpRequestException httpEx)
                    {
                        LogError($"HTTP error during download: {httpEx.Message}");
                        response.StatusCode = 400;
                        await WriteJsonResponse(response, new { success = false, message = $"Download failed: {httpEx.Message}" });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponse(response, new { success = false, message = "Invalid JSON body - url required" });
                }
            }
            catch (Exception ex)
            {
                LogError($"URL Download failed: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponse(response, new { success = false, message = "Download failed: " + ex.Message });
            }
        }

        static async Task ServeVideo(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // Extract filename from URL
                string path = request.Url.AbsolutePath;
                if (!path.StartsWith("/videos/"))
                {
                    Log($"❌ Invalid video URL format: {path}");
                    response.StatusCode = 400;
                    await WriteStringResponse(response, "Invalid URL format");
                    response.Close();
                    return;
                }

                string fileName = path.Substring("/videos/".Length);

                // Remove any query parameters
                int queryIndex = fileName.IndexOf('?');
                if (queryIndex > 0)
                {
                    fileName = fileName.Substring(0, queryIndex);
                }

                // Validate filename to prevent directory traversal
                if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
                {
                    Log($"❌ Invalid filename requested: {fileName}");
                    response.StatusCode = 400;
                    await WriteStringResponse(response, "Invalid filename");
                    response.Close();
                    return;
                }

                string filePath = Path.Combine(AppContext.BaseDirectory, "videos", fileName);

                if (!File.Exists(filePath))
                {
                    Log($"⚠️ Video file not found: {filePath}");
                    response.StatusCode = 404;
                    await WriteStringResponse(response, "Video file not found");
                    response.Close();
                    return;
                }

                Log($"📹 Serving video: {fileName} ({new FileInfo(filePath).Length} bytes)");

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
                        ".mkv" => "video/x-matroska",
                        _ => "application/octet-stream"
                    };

                    response.ContentType = contentType;
                    response.AddHeader("Accept-Ranges", "bytes");
                    response.AddHeader("Access-Control-Allow-Origin", "*");
                    response.AddHeader("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
                    response.AddHeader("Access-Control-Allow-Headers", "Range");

                    long fileLength = new FileInfo(filePath).Length;

                    if (request.Headers["Range"] != null)
                    {
                        // Handle range requests for video streaming
                        string range = request.Headers["Range"].Replace("bytes=", "");
                        string[] parts = range.Split('-');
                        long start = 0;
                        long end = fileLength - 1;

                        if (parts.Length > 0 && long.TryParse(parts[0], out long parsedStart))
                            start = parsedStart;
                        if (parts.Length > 1 && long.TryParse(parts[1], out long parsedEnd))
                            end = parsedEnd;

                        if (start >= fileLength || end >= fileLength || start > end)
                        {
                            response.StatusCode = 416;
                            response.AddHeader("Content-Range", $"bytes */{fileLength}");
                            response.Close();
                            return;
                        }

                        long contentLength = end - start + 1;
                        response.StatusCode = 206;
                        response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                        response.ContentLength64 = contentLength;

                        Log($"🎬 Serving range {start}-{end} of {fileLength} bytes");

                        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        fs.Seek(start, SeekOrigin.Begin);
                        byte[] buffer = new byte[8192]; // Larger buffer for better performance
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
                        // Full file request
                        response.ContentLength64 = fileLength;
                        Log($"🎬 Serving full file ({fileLength} bytes)");

                        using var fs = File.OpenRead(filePath);
                        await fs.CopyToAsync(response.OutputStream);
                    }

                    Log($"✅ Video served successfully");
                }
                catch (IOException ioEx)
                {
                    if (ioEx.Message.Contains("Connection reset by peer") || ioEx.Message.Contains("The network connection was aborted"))
                    {
                        Log("INFO: Client disconnected during video stream. This is normal.");
                    }
                    else
                    {
                        LogError($"Error serving video: {ioEx.Message}");
                        response.StatusCode = 500;
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
            catch (Exception ex)
            {
                LogError($"Error in ServeVideo: {ex.Message}");
                response.StatusCode = 500;
                response.Close();
            }
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
                else
                {
                    Users = new List<User>();
                    // Create default admin user
                    Users.Add(new User
                    {
                        Username = "admin",
                        Password = HashPassword("admin123"),
                        uuid = Guid.NewGuid(),
                        Role = "admin"
                    });
                    await SaveUsersAsync();
                    Log("✅ Created default admin user (username: admin, password: admin123)");
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
                        newUser.Password = HashPassword(newUser.Password);
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

        // Authentication helper methods
        static string HashPassword(string password)
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password + JwtSecret));
            return Convert.ToBase64String(bytes);
        }

        static bool VerifyPassword(string inputPassword, string storedPassword)
        {
            string hashedInput = HashPassword(inputPassword);
            return hashedInput == storedPassword;
        }

        static string GenerateJwtToken(User user)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role),
                    new Claim("uuid", user.uuid.ToString())
                }),
                Expires = DateTime.UtcNow.AddHours(24),
                SigningCredentials = new SigningCredentials(JwtKey, SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        static bool ValidateJwtToken(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                tokenHandler.ValidateToken(token, new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = JwtKey,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ClockSkew = TimeSpan.Zero
                }, out SecurityToken validatedToken);

                return true;
            }
            catch
            {
                return false;
            }
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
        public Guid uuid { get; set; }
        public string Role { get; set; } = "player";
        public bool TwoFactorEnabled { get; set; } = false;
        public string TwoFactorSecret { get; set; } = "";
    }

    public class ServerConfig
    {
        public string Ip { get; set; } = "0.0.0.0";
        public int MaxConnections { get; set; } = 50;
        public string DashboardPasswordHash { get; set; } = "12345678";
    }
}
