using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Services
{
    public class HttpServer
    {
        private readonly int _port;
        private readonly Logger _logger;
        private readonly UserService _userService;
        private readonly AuthenticationService _authService;
        private readonly VideoService _videoService;
        private readonly CompressionService _compressionService;
        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private double _lastCpuUsage = 0;

        // REPLACE YOUR ENTIRE CONSTRUCTOR WITH THIS ONE
        public HttpServer(
            ServerSettings settings, // <-- The first parameter is now ServerSettings
            Logger logger,
            UserService userService,
            AuthenticationService authService,
            VideoService videoService)
        {
            _port = settings.WebPort; // <-- We get the port from the settings object
            _logger = logger;
            _userService = userService;
            _authService = authService;
            _videoService = videoService;
            _compressionService = new CompressionService(new ServerConfig(), logger);
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://*:{_port}/");
            _httpListener.Start();
            _logger.Log($"🌐 HTTP server listening on port {_port}");

            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = HandleRequestAsync(context);
                }
            }, _cts.Token);

            // Start CPU monitoring
            _ = Task.Run(async () =>
            {
                var proc = Process.GetCurrentProcess();
                TimeSpan prevCpu = proc.TotalProcessorTime;
                DateTime prevTime = DateTime.UtcNow;

                while (!_cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    TimeSpan currCpu = proc.TotalProcessorTime;
                    DateTime currTime = DateTime.UtcNow;

                    double cpuUsedMs = (currCpu - prevCpu).TotalMilliseconds;
                    double elapsedMs = (currTime - prevTime).TotalMilliseconds;
                    int cores = Environment.ProcessorCount;

                    _lastCpuUsage = Math.Round((cpuUsedMs / (elapsedMs * cores)) * 100, 2);

                    prevCpu = currCpu;
                    prevTime = currTime;
                }
            });
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _httpListener?.Stop();
            _logger.Log("🌐 HTTP server stopped");
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            response.AddHeader("Access-Control-Allow-Origin", "*");
            if (request.HttpMethod == "OPTIONS")
            {
                response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                response.StatusCode = 200;
                response.Close();
                return;
            }

            try
            {
                switch (request.Url.AbsolutePath)
                {
                    // Authentication endpoints
                    case "/api/login":
                        await HandleLoginAsync(request, response);
                        break;

                    case "/api/verify-2fa":
                        await HandleTwoFactorVerificationAsync(request, response);
                        break;

                    // Protected endpoints - require authentication
                    case "/stats":
                        if (ValidateAuthentication(request))
                            await HandleStatsAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/system":
                        if (ValidateAuthentication(request))
                            await HandleSystemAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/logs":
                        if (ValidateAuthentication(request))
                            await HandleLogsAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/videos":
                        if (ValidateAuthentication(request))
                            await HandleVideosAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/upload-url":
                        if (ValidateAuthentication(request))
                            await HandleVideoUploadAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    default:
                        if (request.Url.AbsolutePath.StartsWith("/videos/"))
                        {
                            // Handle video requests with authentication
                            if (ValidateAuthentication(request))
                                await ServeVideoAsync(request, response);
                            else
                                SendUnauthorized(response);
                        }
                        else
                        {
                            await ServeDefaultPageAsync(request, response);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"HTTP server error: {ex.Message}");
                response.StatusCode = 500;
            }
            finally
            {
                response.Close();
            }
        }

        private async Task HandleLoginAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                // FIX: Deserialize to LoginRequest
                var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(body);

                if (loginRequest != null)
                {
                    // FIX: Call the new async method
                    var (user, message) = await _userService.AuthenticateUserAsync(loginRequest);

                    if (user != null)
                    {
                        // Generate JWT token
                        string token = _authService.GenerateJwtToken(user);

                        var responseData = new
                        {
                            success = true,
                            token = token,
                            refreshToken = user.RefreshToken, // Also send refresh token
                            user = new { username = user.Username, role = user.Role, email = user.Email }
                        };

                        await WriteJsonResponseAsync(response, responseData);
                        _logger.Log($"✅ User logged in: {user.Username}");
                    }
                    else
                    {
                        response.StatusCode = 401;
                        await WriteJsonResponseAsync(response, new { success = false, message });
                        _logger.Log($"⚠️ Failed login attempt: {loginRequest.Username}");
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid request" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Login error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
            }
        }

        private async Task HandleTwoFactorVerificationAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            // Implementation for two-factor authentication
            // This is optional but recommended for higher security
            await WriteJsonResponseAsync(response, new { success = false, message = "2FA not implemented" });
        }

        private bool ValidateAuthentication(HttpListenerRequest request)
        {
            string authHeader = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                return false;
            }

            string token = authHeader.Substring("Bearer ".Length);
            return _authService.ValidateJwtToken(token);
        }

        private void SendUnauthorized(HttpListenerResponse response)
        {
            response.StatusCode = 401;
            response.AddHeader("WWW-Authenticate", "Bearer");
        }

        private async Task HandleStatsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var stats = new
            {
                uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
                users = _userService.Users.Count,
                maxConnections = 50, // This should come from config
                protocol = 1
            };
            await WriteJsonResponseAsync(response, stats);
        }

        private async Task HandleSystemAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var proc = Process.GetCurrentProcess();
            double cpuUse = _lastCpuUsage;

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

            await WriteJsonResponseAsync(response, systemStats);
        }

        private async Task HandleLogsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "latest.log");
            string[] lines = File.Exists(logFile)
                ? File.ReadLines(logFile).Reverse().Take(50).Reverse().ToArray()
                : Array.Empty<string>();
            await WriteJsonResponseAsync(response, lines);
        }

        private async Task HandleVideosAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                if (request.HttpMethod == "GET")
                {
                    string[] files = _videoService.GetVideoFiles();
                    await WriteJsonResponseAsync(response, files);
                }
                else
                {
                    response.StatusCode = 405;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Method not allowed" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in HandleVideos: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Internal server error" });
            }
        }

        private async Task HandleVideoUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                using var reader = new StreamReader(request.InputStream);
                string body = await reader.ReadToEndAsync();
                var json = JsonConvert.DeserializeObject<Dictionary<string, string>>(body);

                if (json != null && json.TryGetValue("url", out var videoUrl))
                {
                    var (success, message) = await _videoService.DownloadVideoFromUrl(videoUrl);

                    if (success)
                    {
                        await WriteJsonResponseAsync(response, new { success = true, message });
                    }
                    else
                    {
                        response.StatusCode = 400;
                        await WriteJsonResponseAsync(response, new { success = false, message });
                    }
                }
                else
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid JSON body - url required" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"URL Download failed: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Download failed: " + ex.Message });
            }
        }

        private async Task ServeVideoAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                // Extract filename from URL
                string path = request.Url.AbsolutePath;
                if (!path.StartsWith("/videos/"))
                {
                    _logger.Log($"❌ Invalid video URL format: {path}");
                    response.StatusCode = 400;
                    await WriteStringResponseAsync(response, "Invalid URL format");
                    return;
                }

                string fileName = path.Substring("/videos/".Length);

                // Remove any query parameters
                int queryIndex = fileName.IndexOf('?');
                if (queryIndex > 0)
                {
                    fileName = fileName.Substring(0, queryIndex);
                }

                string filePath = _videoService.GetVideoFilePath(fileName);
                if (filePath == null)
                {
                    _logger.Log($"⚠️ Video file not found: {fileName}");
                    response.StatusCode = 404;
                    await WriteStringResponseAsync(response, "Video file not found");
                    return;
                }

                _logger.Log($"📹 Serving video: {fileName} ({new FileInfo(filePath).Length} bytes)");

                try
                {
                    string contentType = _videoService.GetContentType(filePath);
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
                            return;
                        }

                        long contentLength = end - start + 1;
                        response.StatusCode = 206;
                        response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                        response.ContentLength64 = contentLength;

                        _logger.Log($"🎬 Serving range {start}-{end} of {fileLength} bytes");

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
                        _logger.Log($"🎬 Serving full file ({fileLength} bytes)");

                        using var fs = File.OpenRead(filePath);
                        await fs.CopyToAsync(response.OutputStream);
                    }

                    _logger.Log($"✅ Video served successfully");
                }
                catch (IOException ioEx)
                {
                    if (ioEx.Message.Contains("Connection reset by peer") || ioEx.Message.Contains("The network connection was aborted"))
                    {
                        _logger.Log("INFO: Client disconnected during video stream. This is normal.");
                    }
                    else
                    {
                        _logger.LogError($"Error serving video: {ioEx.Message}");
                        response.StatusCode = 500;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error serving video: {ex.Message}");
                    response.StatusCode = 500;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in ServeVideo: {ex.Message}");
                response.StatusCode = 500;
            }
        }

        private async Task ServeDefaultPageAsync(HttpListenerRequest request, HttpListenerResponse response)
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
        }

        private async Task WriteJsonResponseAsync(HttpListenerResponse response, object data)
        {
            string json = JsonConvert.SerializeObject(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private async Task WriteStringResponseAsync(HttpListenerResponse response, string text)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(text);
            response.ContentType = "text/plain";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
    }
}