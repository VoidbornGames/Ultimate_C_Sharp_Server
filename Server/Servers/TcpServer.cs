using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UltimateServer.Models;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace UltimateServer.Services
{
    public class TcpServer
    {
        private readonly int _port;
        private readonly string _ip;
        private readonly Logger _logger;
        private readonly CommandHandler _commandHandler;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private ServerConfig _config;
        private ConcurrentDictionary<TcpClient, bool> _activeClients = new();

        // REPLACE YOUR ENTIRE CONSTRUCTOR WITH THIS ONE
        public TcpServer(
            ServerSettings settings, // <-- The first parameter is now ServerSettings
            ConfigManager configManager,
            Logger logger,
            CommandHandler commandHandler)
        {
            _port = settings.tcpPort; // <-- We get the port from the settings object
            _ip = configManager.Config.Ip;
            _logger = logger;
            _commandHandler = commandHandler;
            _cts = new CancellationTokenSource();
            _config = configManager.Config;
        }

        public async Task Start()
        {
            _listener = new TcpListener(IPAddress.Parse(_ip), _port);
            _listener.Start();
            _logger.Log($"🚀 Server listening on {_port}");

            _ = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _logger.Log($"🔹 New client connected: {client.Client.RemoteEndPoint}");

                        if (_activeClients.Count >= _config.MaxConnections) // This should come from config
                        {
                            _logger.Log($"⚠️ Connection refused: max clients reached.");
                            client.Close();
                            continue;
                        }

                        _activeClients[client] = true;

                        _ = HandleClientAsync(client, _cts.Token).ContinueWith(t =>
                        {
                            _activeClients.TryRemove(client, out _);
                            _logger.Log($"🔹 Client removed: {client.Client.RemoteEndPoint}");
                        });
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            _listener?.Stop();
            _logger.Log("🚀 TCP server stopped");
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            _logger.Log($"🔹 Client connected: {client.Client.RemoteEndPoint}");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;

                    _logger.Log($"📥 Received: {line}");

                    Data? request = null;
                    try { request = JsonConvert.DeserializeObject<Data>(line); } catch { }

                    if (request == null)
                    {
                        await SendResponseAsync(writer, new Data { theCommand = "error", jsonData = "Invalid JSON" });
                        continue;
                    }

                    if (!_commandHandler.TryHandleCommand(request, out var response))
                    {
                        if (response.theCommand == "Invalid User")
                            response = new Data { theCommand = "error", jsonData = $"Invalid username or password" };
                        else
                            response = new Data { theCommand = "error", jsonData = $"Unknown command: {request.theCommand}" };
                    }

                    await SendResponseAsync(writer, response);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Client error: {ex.Message}");
            }
            finally
            {
                _activeClients.TryRemove(client, out _);
                _logger.Log($"🔹 Client disconnected: {client.Client.RemoteEndPoint}");
            }
        }

        private async Task SendResponseAsync(StreamWriter writer, Data data)
        {
            string json = JsonConvert.SerializeObject(data);
            await writer.WriteLineAsync(json);
            _logger.Log($"📤 Sent: {json}");
        }
    }
}