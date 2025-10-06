using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Services
{
    public class ConnectionPoolService
    {
        private readonly ConcurrentQueue<TcpClient> _pool = new();
        private readonly SemaphoreSlim _semaphore;
        private readonly ServerConfig _config;
        private readonly Logger _logger;
        private readonly string _ip;
        private readonly int _port;

        public ConnectionPoolService(string ip, int port, ServerConfig config, Logger logger)
        {
            _ip = ip;
            _port = port;
            _config = config;
            _logger = logger;
            _semaphore = new SemaphoreSlim(_config.ConnectionPoolSize, _config.ConnectionPoolSize);

            // Pre-warm the pool
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < _config.ConnectionPoolSize / 2; i++)
                {
                    await CreateConnectionAsync();
                }
            });
        }

        public async Task<TcpClient> GetConnectionAsync()
        {
            await _semaphore.WaitAsync();

            if (_pool.TryDequeue(out var connection))
            {
                // Check if the connection is still valid
                try
                {
                    if (connection.Connected)
                    {
                        return connection;
                    }
                    else
                    {
                        connection.Close();
                    }
                }
                catch
                {
                    // Connection is not valid, dispose it
                    try { connection.Close(); } catch { }
                }
            }

            // No valid connection in the pool, create a new one
            return await CreateConnectionAsync();
        }

        public void ReturnConnection(TcpClient connection)
        {
            try
            {
                if (connection.Connected)
                {
                    _pool.Enqueue(connection);
                }
                else
                {
                    connection.Close();
                }
            }
            catch
            {
                try { connection.Close(); } catch { }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task<TcpClient> CreateConnectionAsync()
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(_ip, _port);
                return client;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to create connection: {ex.Message}");
                throw;
            }
        }

        public async Task DisposeAsync()
        {
            while (_pool.TryDequeue(out var connection))
            {
                try
                {
                    connection.Close();
                }
                catch { }
            }

            _semaphore.Dispose();
            await Task.CompletedTask;
        }
    }
}