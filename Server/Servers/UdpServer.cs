// File: Server/Servers/UdpServer.cs

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Servers
{
    // Represents a channel within a room
    internal class Channel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ConcurrentDictionary<IPEndPoint, bool> Clients { get; set; } = new();
    }

    // Represents a room containing multiple channels
    internal class Room
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public ConcurrentDictionary<string, Channel> Channels { get; set; } = new();
        public ConcurrentDictionary<IPEndPoint, string> ClientChannels { get; set; } = new(); // Maps client to their current channel ID

        public Room(string id, string name)
        {
            Id = id;
            Name = name;
            // Create a default channel
            var defaultChannel = new Channel { Id = "general", Name = "General" };
            Channels.TryAdd("general", defaultChannel);
        }
    }

    // Message types for client-server communication
    internal enum MessageType : byte
    {
        VoiceData = 0,
        JoinRoom = 1,
        LeaveRoom = 2,
        JoinChannel = 3,
        LeaveChannel = 4,
        RoomList = 5,
        ChannelList = 6,
        ServerMessage = 7
    }

    internal class UdpServer
    {
        private readonly int _port;
        private readonly Logger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private UdpClient _udpClient;

        // Client management - maps client endpoint to their current room
        private readonly ConcurrentDictionary<IPEndPoint, string> _clientRooms = new();

        // Room management
        private readonly ConcurrentDictionary<string, Room> _rooms = new();

        public UdpServer(ServerSettings settings, ConfigManager configManager, Logger logger)
        {
            _port = settings.udpPort;
            _logger = logger;

            // Create a default room
            var defaultRoom = new Room("lobby", "Lobby");
            _rooms.TryAdd("lobby", defaultRoom);
        }

        /// <summary>
        /// Starts the UDP server and begins listening for voice packets and control messages.
        /// </summary>
        public async Task Start()
        {
            _ = Task.Run(async () =>
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

                        // If we have at least 1 byte, check the message type
                        if (data.Length > 0)
                        {
                            var messageType = (MessageType)data[0];

                            switch (messageType)
                            {
                                case MessageType.VoiceData:
                                    await HandleVoiceData(sender, data);
                                    break;
                                case MessageType.JoinRoom:
                                    await HandleJoinRoom(sender, data);
                                    break;
                                case MessageType.LeaveRoom:
                                    await HandleLeaveRoom(sender);
                                    break;
                                case MessageType.JoinChannel:
                                    await HandleJoinChannel(sender, data);
                                    break;
                                case MessageType.LeaveChannel:
                                    await HandleLeaveChannel(sender);
                                    break;
                                case MessageType.RoomList:
                                    await SendRoomList(sender);
                                    break;
                                case MessageType.ChannelList:
                                    await SendChannelList(sender, data);
                                    break;
                                default:
                                    _logger.Log($"⚠️ Unknown message type: {messageType} from {sender}");
                                    break;
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
            });
        }

        /// <summary>
        /// Handles voice data packets and forwards them to clients in the same room and channel.
        /// </summary>
        private async Task HandleVoiceData(IPEndPoint sender, byte[] data)
        {
            // Check if the sender is in a room
            if (!_clientRooms.TryGetValue(sender, out var roomId) ||
                !_rooms.TryGetValue(roomId, out var room))
            {
                // If not in a room, add them to the default room
                await HandleJoinRoom(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinRoom}lobby"));
                return;
            }

            // Get the client's current channel
            if (!room.ClientChannels.TryGetValue(sender, out var channelId) ||
                !room.Channels.TryGetValue(channelId, out var channel))
            {
                // If not in a channel, add them to the default channel
                await HandleJoinChannel(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinChannel}general"));
                return;
            }

            // Forward the voice packet to all other clients in the same channel
            foreach (var clientEndpoint in channel.Clients.Keys)
            {
                if (!clientEndpoint.Equals(sender))
                {
                    try
                    {
                        await _udpClient.SendAsync(data, data.Length, clientEndpoint);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"❌ Failed to send to client {clientEndpoint}. Removing. Error: {ex.Message}");
                        RemoveClient(clientEndpoint);
                    }
                }
            }
        }

        /// <summary>
        /// Handles a request to join a room.
        /// </summary>
        private async Task HandleJoinRoom(IPEndPoint sender, byte[] data)
        {
            try
            {
                var roomId = Encoding.UTF8.GetString(data.Skip(1).ToArray());

                // Remove client from current room if they're in one
                if (_clientRooms.TryGetValue(sender, out var currentRoomId))
                {
                    await HandleLeaveRoom(sender);
                }

                // Get or create the room
                var room = _rooms.GetOrAdd(roomId, id => new Room(id, id));

                // Add client to the room
                _clientRooms.TryAdd(sender, roomId);

                // Add client to the default channel
                await HandleJoinChannel(sender, Encoding.UTF8.GetBytes($"{(byte)MessageType.JoinChannel}general"));

                _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} joined room {roomId}");

                // Send confirmation
                var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Joined room {roomId}");
                await _udpClient.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling join room request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a request to leave a room.
        /// </summary>
        private async Task HandleLeaveRoom(IPEndPoint sender)
        {
            try
            {
                if (_clientRooms.TryRemove(sender, out var roomId) &&
                    _rooms.TryGetValue(roomId, out var room))
                {
                    // Remove from current channel
                    await HandleLeaveChannel(sender);

                    _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} left room {roomId}");

                    // Send confirmation
                    var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Left room {roomId}");
                    await _udpClient.SendAsync(response, response.Length, sender);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling leave room request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a request to join a channel.
        /// </summary>
        private async Task HandleJoinChannel(IPEndPoint sender, byte[] data)
        {
            try
            {
                // Make sure the client is in a room
                if (!_clientRooms.TryGetValue(sender, out var roomId) ||
                    !_rooms.TryGetValue(roomId, out var room))
                {
                    var _response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}You must join a room first");
                    await _udpClient.SendAsync(_response, _response.Length, sender);
                    return;
                }

                var channelId = Encoding.UTF8.GetString(data.Skip(1).ToArray());

                // Remove from current channel if they're in one
                if (room.ClientChannels.TryGetValue(sender, out var currentChannelId))
                {
                    await HandleLeaveChannel(sender);
                }

                // Get or create the channel
                var channel = room.Channels.GetOrAdd(channelId, id => new Channel { Id = id, Name = id });

                // Add client to the channel
                channel.Clients.TryAdd(sender, true);
                room.ClientChannels.TryAdd(sender, channelId);

                _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} joined channel {channelId} in room {roomId}");

                // Send confirmation
                var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Joined channel {channelId}");
                await _udpClient.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling join channel request: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles a request to leave a channel.
        /// </summary>
        private async Task HandleLeaveChannel(IPEndPoint sender)
        {
            try
            {
                if (_clientRooms.TryGetValue(sender, out var roomId) &&
                    _rooms.TryGetValue(roomId, out var room))
                {
                    if (room.ClientChannels.TryRemove(sender, out var channelId) &&
                        room.Channels.TryGetValue(channelId, out var channel))
                    {
                        channel.Clients.TryRemove(sender, out _);

                        _logger.Log($"ℹ️ Client {sender.Address}:{sender.Port} left channel {channelId} in room {roomId}");

                        // Send confirmation
                        var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Left channel {channelId}");
                        await _udpClient.SendAsync(response, response.Length, sender);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error handling leave channel request: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a list of available rooms to the client.
        /// </summary>
        private async Task SendRoomList(IPEndPoint sender)
        {
            try
            {
                var roomList = string.Join(",", _rooms.Keys);
                var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.RoomList}{roomList}");
                await _udpClient.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error sending room list: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a list of available channels in a room to the client.
        /// </summary>
        private async Task SendChannelList(IPEndPoint sender, byte[] data)
        {
            try
            {
                var roomId = Encoding.UTF8.GetString(data.Skip(1).ToArray());

                if (_rooms.TryGetValue(roomId, out var room))
                {
                    var channelList = string.Join(",", room.Channels.Keys);
                    var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ChannelList}{channelList}");
                    await _udpClient.SendAsync(response, response.Length, sender);
                }
                else
                {
                    var response = Encoding.UTF8.GetBytes($"{(byte)MessageType.ServerMessage}Room {roomId} not found");
                    await _udpClient.SendAsync(response, response.Length, sender);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"❌ Error sending channel list: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes a client from all rooms and channels.
        /// </summary>
        private void RemoveClient(IPEndPoint clientEndpoint)
        {
            if (_clientRooms.TryRemove(clientEndpoint, out var roomId) &&
                _rooms.TryGetValue(roomId, out var room))
            {
                if (room.ClientChannels.TryRemove(clientEndpoint, out var channelId) &&
                    room.Channels.TryGetValue(channelId, out var channel))
                {
                    channel.Clients.TryRemove(clientEndpoint, out _);
                }
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
    }
}