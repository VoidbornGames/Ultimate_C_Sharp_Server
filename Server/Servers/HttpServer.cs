using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UltimateServer.Models;
using Newtonsoft.Json;
using UltimateServer.Services;
using Microsoft.Extensions.DependencyInjection; // Required for IServiceProvider

namespace UltimateServer.Services
{
    public class HttpServer
    {
        private readonly int _port;
        private readonly string _ip;
        private readonly Logger _logger;
        private readonly UserService _userService;
        private readonly AuthenticationService _authService;
        private readonly VideoService _videoService;
        private readonly CompressionService _compressionService;
        private readonly PluginManager _pluginManager;
        private readonly IServiceProvider _serviceProvider; // Added for DI
        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private double _lastCpuUsage = 0;
        private readonly ConcurrentDictionary<TcpClient, bool> _activeClients = new();

        public HttpServer(
            ServerSettings settings,
            Logger logger,
            UserService userService,
            AuthenticationService authService,
            VideoService videoService,
            PluginManager pluginManager,
            IServiceProvider serviceProvider) // Injected IServiceProvider
        {
            _port = settings.WebPort;
            _ip = settings.Ip;
            _logger = logger;
            _userService = userService;
            _logger.Log($"🌐 HttpServer initialized for port {_port}.");
            _authService = authService;
            _videoService = videoService;
            _pluginManager = pluginManager;
            _serviceProvider = serviceProvider;
            _compressionService = new CompressionService(new ServerConfig(), logger);
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            try
            {
                _httpListener = new HttpListener();
                string prefix = $"http://*:{_port}/";
                _httpListener.Prefixes.Add(prefix);
                _httpListener.Start();
                _logger.Log($"🌐 HTTP server listening on {prefix}");

                _ = Task.Run(async () =>
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        var context = await _httpListener.GetContextAsync();
                        _ = HandleRequestAsync(context).ContinueWith(t =>
                        {
                            if (t.Exception != null)
                                _logger.LogError($"Request handling error: {t.Exception.Message}");
                        });
                    }
                }, _cts.Token);

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

                        _lastCpuUsage = Math.Round(cpuUsedMs / (elapsedMs * cores) * 100, 2);

                        prevCpu = currCpu;
                        prevTime = currTime;
                    }
                });
            }
            catch (HttpListenerException ex)
            {
                _logger.LogError($"❌ Failed to start HTTP server: {ex.Message}");
                _logger.LogError($"💡 Tip: Make sure port {_port} is not already in use");
                throw;
            }
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _httpListener?.Stop();
            _logger.Log("🌐 HTTP server stopped.");
            await Task.CompletedTask;
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            bool requestHandled = false; // Flag to track if a plugin handled the request

            // Add CORS headers
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
                // =================================================================
                // 1. Check for a plugin-registered route first
                // =================================================================
                foreach (var pluginEntry in _pluginManager.GetLoadedPlugins())
                {
                    var plugin = pluginEntry.Value;
                    var pluginCtx = _pluginManager.GetPluginContext(plugin.Name);
                    if (pluginCtx != null)
                    {
                        var handler = pluginCtx.GetRouteHandler(request.Url.AbsolutePath);
                        if (handler != null)
                        {
                            try
                            {
                                // The plugin handler is now responsible for the full response
                                await handler(request);
                                requestHandled = true; // Mark as handled by a plugin
                                break; // Stop checking other plugins
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Plugin route handler error: {ex.Message}");
                                response.StatusCode = 500;
                                await WriteJsonResponseAsync(response, new { success = false, message = "Plugin error." });
                                requestHandled = true; // Also considered handled
                                break;
                            }
                        }
                    }
                }

                // If a plugin handled the request, we're done.
                if (requestHandled)
                {
                    return;
                }

                // =================================================================
                // 2. Handle default server routes
                // =================================================================
                // ... (all your switch cases for /api/login, /stats, etc.) ...
                switch (request.Url.AbsolutePath)
                {
                    // Authentication endpoints
                    case "/api/login":
                        await HandleLoginAsync(request, response);
                        break;

                    case "/api/register":
                        await HandleRegisterAsync(request, response);
                        break;

                    case "/api/verify-2fa":
                        await HandleTwoFactorVerificationAsync(request, response);
                        break;

                    // Plugin management endpoints
                    case "/api/plugins":
                        if (ValidateAuthentication(request))
                            await HandlePluginsListAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/plugins/upload":
                        if (ValidateAuthentication(request))
                            await HandlePluginUploadAsync(request, response);
                        else
                            SendUnauthorized(response);
                        break;

                    case "/api/plugins/reload":
                        if (ValidateAuthentication(request))
                            await HandlePluginsReloadAsync(request, response);
                        else
                            SendUnauthorized(response);
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
                        // Handle plugin enable/disable (placeholder)
                        if (request.Url.AbsolutePath.StartsWith("/api/plugins/") &&
                            (request.Url.AbsolutePath.EndsWith("/enable") || request.Url.AbsolutePath.EndsWith("/disable")))
                        {
                            if (ValidateAuthentication(request))
                            {
                                var pathParts = request.Url.AbsolutePath.Split('/');
                                if (pathParts.Length >= 4)
                                {
                                    var pluginId = pathParts[3];
                                    var enable = request.Url.AbsolutePath.EndsWith("/enable");
                                    await HandlePluginToggleAsync(request, response, pluginId, enable);
                                }
                                else
                                {
                                    response.StatusCode = 400;
                                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid plugin ID" });
                                }
                            }
                            else
                                SendUnauthorized(response);
                        }
                        else if (request.Url.AbsolutePath.StartsWith("/videos/"))
                        {
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
                // =================================================================
                // FIX: Only close the response if the SERVER handled it.
                // If a plugin handled it, it's the plugin's responsibility.
                // =================================================================
                if (!requestHandled)
                {
                    response.Close();
                }
            }
        }

        // ========================================================================
        // API ENDPOINTS
        // ========================================================================

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
                var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(body);

                if (loginRequest != null)
                {
                    var (user, message) = await _userService.AuthenticateUserAsync(loginRequest);
                    if (user != null)
                    {
                        var token = _authService.GenerateJwtToken(user);
                        var responseData = new
                        {
                            success = true,
                            token,
                            refreshToken = user.RefreshToken,
                            user = new { username = user.Username, role = user.Role, email = user.Email }
                        };
                        await WriteJsonResponseAsync(response, responseData);
                        _logger.Log($"✅ User logged in: {user.Username}");
                    }
                    else
                    {
                        response.StatusCode = 401;
                        await WriteJsonResponseAsync(response, new { success = false, message });
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

        private async Task HandleRegisterAsync(HttpListenerRequest request, HttpListenerResponse response)
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
                var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(body);

                if (registerRequest != null)
                {
                    var (user, message) = await _userService.CreateUserAsync(registerRequest);
                    if (user != null)
                    {
                        var token = _authService.GenerateJwtToken(user);
                        var responseData = new
                        {
                            success = true,
                            token,
                            refreshToken = user.RefreshToken,
                            user = new { username = user.Username, role = user.Role, email = user.Email }
                        };
                        await WriteJsonResponseAsync(response, responseData);
                        _logger.Log($"✅ User registered: {user.Username}");
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
                    await WriteJsonResponseAsync(response, new { success = false, message = "Invalid request" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Registration error: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Server error" });
            }
        }

        private async Task HandleTwoFactorVerificationAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            await WriteJsonResponseAsync(response, new { success = false, message = "2FA not implemented" });
        }

        // ========================================================================
        // PLUGIN MANAGEMENT ENDPOINTS
        // ========================================================================

        private async Task HandlePluginsListAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                var plugins = _pluginManager.GetLoadedPlugins();
                var pluginList = plugins.Select(p => new
                {
                    id = p.Key,
                    name = p.Value.Name,
                    version = p.Value.Version,
                    enabled = true
                }).ToList();

                await WriteJsonResponseAsync(response, new { success = true, plugins = pluginList });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error listing plugins: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to list plugins" });
            }
        }

        /// <summary>
        /// Handles the upload of a plugin file using a safe, temporary-file process.
        /// </summary>
        private async Task HandlePluginUploadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            if (!request.HasEntityBody)
            {
                response.StatusCode = 400;
                await WriteJsonResponseAsync(response, new { success = false, message = "No file uploaded" });
                return;
            }

            var contentType = request.ContentType;
            if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("multipart/form-data"))
            {
                response.StatusCode = 400;
                await WriteJsonResponseAsync(response, new { success = false, message = "Invalid content type" });
                return;
            }

            string pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
            if (!Directory.Exists(pluginsDirectory))
                Directory.CreateDirectory(pluginsDirectory);

            string tempFilePath = null;
            string finalFilePath = null;

            try
            {
                // --- Step 1: Save the uploaded file to a temporary location ---
                _logger.Log("📤 Receiving plugin file...");
                var (filename, fileContent) = await ReadMultipartFileAsync(request);
                if (string.IsNullOrEmpty(filename))
                {
                    response.StatusCode = 400;
                    await WriteJsonResponseAsync(response, new { success = false, message = "No filename found in upload." });
                    return;
                }

                tempFilePath = Path.Combine(pluginsDirectory, $"{filename}.tmp");
                finalFilePath = Path.Combine(pluginsDirectory, filename);
                await File.WriteAllBytesAsync(tempFilePath, fileContent);
                _logger.Log($"✅ Plugin temporarily saved to '{tempFilePath}'");

                // --- Step 2: Unload all existing plugins ---
                _logger.Log("🔄 Unloading existing plugins before replacement...");
                await _pluginManager.UnloadAllPluginsAsync();

                // --- Step 3: Replace the old file with the new one ---
                if (File.Exists(finalFilePath))
                {
                    File.Delete(finalFilePath);
                }
                File.Move(tempFilePath, finalFilePath);
                _logger.Log($"✅ Replaced old plugin with '{finalFilePath}'");
                tempFilePath = null; // Mark as moved, so we don't delete it in the finally block

                // --- Step 4: Load the new plugins ---
                await _pluginManager.LoadPluginsAsync(pluginsDirectory);

                _logger.Log("✅ Plugin upload and reload complete.");
                await WriteJsonResponseAsync(response, new { success = true, message = "Plugin uploaded and reloaded successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error uploading plugin: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to upload plugin." });
            }
            finally
            {
                // --- Step 5: Cleanup ---
                // If the temp file still exists (due to an error), delete it.
                if (tempFilePath != null && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        _logger.Log($"🧹 Cleaned up temporary file '{tempFilePath}'");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to clean up temporary file: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// REQUIRED HELPER METHOD 1: Parses multipart/form-data to extract a single file.
        /// </summary>
        private async Task<(string filename, byte[] data)> ReadMultipartFileAsync(HttpListenerRequest request)
        {
            var contentType = request.ContentType;
            var boundary = contentType.Split(';')[1].Split('=')[1].Trim('"');
            var boundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}");
            var endBoundaryBytes = Encoding.ASCII.GetBytes($"--{boundary}--");

            using var memoryStream = new MemoryStream();
            await request.InputStream.CopyToAsync(memoryStream);
            var requestData = memoryStream.ToArray();

            int startIndex = FindIndexOf(requestData, boundaryBytes);
            if (startIndex == -1) return (null, null);
            startIndex += boundaryBytes.Length;

            int headerEndIndex = FindIndexOf(requestData, new byte[] { 0x0D, 0x0A, 0x0D, 0x0A }, startIndex);
            if (headerEndIndex == -1) return (null, null);
            int fileContentStartIndex = headerEndIndex + 4;

            int endIndex = FindIndexOf(requestData, endBoundaryBytes, fileContentStartIndex);
            if (endIndex == -1) return (null, null);

            var headerBytes = new byte[headerEndIndex - startIndex];
            Array.Copy(requestData, startIndex, headerBytes, 0, headerBytes.Length);
            var headerText = Encoding.UTF8.GetString(headerBytes);
            var filenameMatch = System.Text.RegularExpressions.Regex.Match(headerText, @"filename=""([^""]+)""");
            var filename = filenameMatch.Success ? filenameMatch.Groups[1].Value : null;

            var fileContentLength = endIndex - fileContentStartIndex;
            var fileContent = new byte[fileContentLength];
            Array.Copy(requestData, fileContentStartIndex, fileContent, 0, fileContentLength);

            return (filename, fileContent);
        }

        /// <summary>
        /// REQUIRED HELPER METHOD 2: Finds a byte array within another byte array.
        /// </summary>
        private int FindIndexOf(byte[] source, byte[] pattern, int startIndex = 0)
        {
            for (int i = startIndex; i <= source.Length - pattern.Length; i++)
            {
                if (source[i] == pattern[0])
                {
                    bool found = true;
                    for (int j = 1; j < pattern.Length; j++)
                    {
                        if (source[i + j] != pattern[j])
                        {
                            found = false;
                            break;
                        }
                    }
                    if (found) return i;
                }
            }
            return -1;
        }

        private async Task HandlePluginsReloadAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                await WriteJsonResponseAsync(response, new { success = false, message = "Only POST allowed" });
                return;
            }

            try
            {
                _logger.Log("🔄 Reloading plugins via API request...");
                var pluginsDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");

                // 1. Unload all plugins first
                await _pluginManager.UnloadAllPluginsAsync();

                // 2. CRITICAL: Add a small delay to allow the OS to release file locks
                _logger.Log("⏳ Waiting for file locks to be released...");
                await Task.Delay(1000); // Wait for 500 milliseconds

                // 3. Load all plugins from the directory
                await _pluginManager.LoadPluginsAsync(pluginsDirectory);

                _logger.Log("✅ Plugin reload complete.");
                await WriteJsonResponseAsync(response, new { success = true, message = "Plugins reloaded successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reloading plugins: {ex.Message}");
                response.StatusCode = 500;
                await WriteJsonResponseAsync(response, new { success = false, message = "Failed to reload plugins." });
            }
        }

        private async Task HandlePluginToggleAsync(HttpListenerRequest request, HttpListenerResponse response, string pluginId, bool enable)
        {
            await WriteJsonResponseAsync(response, new
            {
                success = true,
                message = $"Plugin {pluginId} {(enable ? "enabled" : "disabled")} successfully"
            });
        }

        // ========================================================================
        // AUTHENTICATION & AUTHORIZATION HELPERS
        // ========================================================================

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

        // ========================================================================
        // CORE REQUEST HANDLERS
        // ========================================================================

        private async Task HandleStatsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var stats = new
            {
                uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"hh\:mm\:ss"),
                users = _userService.Users.Count,
                maxConnections = 50,
                protocol = 1
            };
            await WriteJsonResponseAsync(response, stats);
        }

        private async Task HandleSystemAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var proc = Process.GetCurrentProcess();
            double cpuUse = _lastCpuUsage;
            double memUsage = proc.WorkingSet64 / 1024.0 / 1024.0;
            object systemStats;

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    string memInfo = File.ReadAllText("/proc/meminfo");
                    string totalLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal:"));
                    string freeLine = memInfo.Split('\n').FirstOrDefault(l => l.StartsWith("MemAvailable:"));
                    double totalMem = totalLine != null ? double.Parse(new string(totalLine.Where(char.IsDigit).ToArray())) / 1024.0 : memUsage;
                    double freeMem = freeLine != null ? double.Parse(new string(freeLine.Where(char.IsDigit).ToArray())) / 1024.0 : 0;
                    memUsage = totalMem - freeMem;

                    DriveInfo drive = new DriveInfo("/");
                    long totalSpace = drive.TotalSize;
                    long usedSpace = totalSpace - drive.AvailableFreeSpace;

                    long totalBytesSent = 0, totalBytesReceived = 0;
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        var nicStats = nic.GetIPv4Statistics();
                        totalBytesSent += nicStats.BytesSent;
                        totalBytesReceived += nicStats.BytesReceived;
                    }

                    systemStats = new { cpuUsage = cpuUse, memoryMB = memUsage, diskUsedGB = usedSpace / (1024.0 * 1024 * 1024), diskTotalGB = totalSpace / (1024.0 * 1024 * 1024), netSentMB = totalBytesSent / (1024.0 * 1024), netReceivedMB = totalBytesReceived / (1024.0 * 1024) };
                }
                catch { systemStats = new { cpuUsage = cpuUse, memoryMB = memUsage }; }
            }
            else
            {
                systemStats = new { cpuUsage = cpuUse, memoryMB = memUsage };
            }

            await WriteJsonResponseAsync(response, systemStats);
        }

        private async Task HandleLogsAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            string logFile = Path.Combine(AppContext.BaseDirectory, "logs", "latest.log");
            string[] lines = File.Exists(logFile) ? File.ReadLines(logFile).Reverse().Take(50).Reverse().ToArray() : Array.Empty<string>();
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
                        await WriteJsonResponseAsync(response, new { success = true, message });
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

        // ========================================================================
        // FILE SERVING
        // ========================================================================

        private async Task ServeVideoAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            // (This method is complex but appears correct, so I will leave it as is)
            try
            {
                string path = request.Url.AbsolutePath;
                if (!path.StartsWith("/videos/")) { response.StatusCode = 400; await WriteStringResponseAsync(response, "Invalid URL format"); return; }

                string fileName = path.Substring("/videos/".Length);
                int queryIndex = fileName.IndexOf('?');
                if (queryIndex > 0) { fileName = fileName.Substring(0, queryIndex); }

                string filePath = _videoService.GetVideoFilePath(fileName);
                if (filePath == null) { response.StatusCode = 404; await WriteStringResponseAsync(response, "Video file not found"); return; }

                string contentType = _videoService.GetContentType(filePath);
                response.ContentType = contentType;
                response.AddHeader("Accept-Ranges", "bytes");
                long fileLength = new FileInfo(filePath).Length;

                if (request.Headers["Range"] != null)
                {
                    string range = request.Headers["Range"].Replace("bytes=", "");
                    string[] parts = range.Split('-');
                    long start = parts.Length > 0 && long.TryParse(parts[0], out var s) ? s : 0;
                    long end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? e : fileLength - 1;

                    if (start >= fileLength || end >= fileLength || start > end) { response.StatusCode = 416; response.AddHeader("Content-Range", $"bytes */{fileLength}"); return; }

                    long contentLength = end - start + 1;
                    response.StatusCode = 206;
                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                    response.ContentLength64 = contentLength;

                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.Seek(start, SeekOrigin.Begin);
                        byte[] buffer = new byte[8192];
                        int bytesRead;
                        long bytesRemaining = contentLength;
                        while (bytesRemaining > 0 && (bytesRead = await fs.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, bytesRemaining))) > 0)
                        {
                            await response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                            bytesRemaining -= bytesRead;
                        }
                    }
                }
                else
                {
                    response.ContentLength64 = fileLength;
                    using (var fs = File.OpenRead(filePath)) { await fs.CopyToAsync(response.OutputStream); }
                }
            }
            catch (Exception ex) { _logger.LogError($"Error serving video: {ex.Message}"); response.StatusCode = 500; }
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

        // ========================================================================
        // HELPER METHODS
        // ========================================================================

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