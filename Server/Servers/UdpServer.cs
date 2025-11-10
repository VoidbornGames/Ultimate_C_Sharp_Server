// File: Server/Servers/UdpServer.cs

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services;

namespace Server.Servers
{
    internal class UdpServer : IDisposable
    {
        private readonly int _port;
        private readonly Logger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private UdpClient _udpClient;
        // Using a ConcurrentDictionary for thread-safe client management
        private readonly ConcurrentDictionary<IPEndPoint, bool> _clients = new();

        public UdpServer(ServerSettings settings, ConfigManager configManager, Logger logger)
        {
            _port = settings.udpPort;
            _logger = logger;
        }

        /// <summary>
        /// Starts the UDP server and begins listening for voice packets.
        /// </summary>
        public async Task Start()
        {
            try
            {
                _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
                _logger.Log($"🎧 Voice UDP server started on port {_port}");
            }
            catch (SocketException ex)
            {
                _logger.Log($"FATAL ERROR: Could not start server on port {_port}. Is it already in use? Error: {ex.Message}");
                return; // Exit if the port is unavailable
            }

            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    var sender = result.RemoteEndPoint;
                    var data = result.Buffer;

                    // Register new clients
                    if (_clients.TryAdd(sender, true))
                    {
                        _logger.Log($"ℹ️ New Voice Client Connected! IP: {sender.Address} Port: {sender.Port}");
                    }

                    // Forward the voice packet to all other clients
                    foreach (var clientEndpoint in _clients.Keys)
                    {
                        if (!clientEndpoint.Equals(sender))
                        {
                            try
                            {
                                await _udpClient.SendAsync(data, data.Length, clientEndpoint);
                                // Optional: Log forwarding for debugging
                                // _logger.Log($"🔊 Forwarding {data.Length} bytes from {sender.Address} to {clientEndpoint.Address}");
                            }
                            catch (Exception ex)
                            {
                                _logger.Log($"❌ Failed to send to client {clientEndpoint}. Removing. Error: {ex.Message}");
                                // Remove unreachable clients
                                _clients.TryRemove(clientEndpoint, out _);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // This is expected when StopAsync is called.
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _logger.Log($"❌ UDP server receive loop error: {ex.Message}");
            }
            finally
            {
                _logger.Log("🛑 UDP server stopped.");
            }
        }

        /// <summary>
        /// Stops the UDP server gracefully.
        /// </summary>
        public async Task StopAsync()
        {
            _cts.Cancel();
            _udpClient?.Close();
            await Task.Delay(100); // Give time for operations to cancel
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _cts?.Dispose();
        }
    }
}