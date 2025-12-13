using System.Collections.Concurrent;
using System.Net;

namespace UltimateServer.Services
{
    public class DDoSProtectionService
    {
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, RequestTracker> _ipTrackers = new();
        private readonly ConcurrentDictionary<string, DateTime> _blockedIPs = new();
        private readonly Timer _cleanupTimer;

        // Configuration
        private readonly int _maxRequestsPerMinute;
        private readonly int _maxConcurrentConnections;
        private readonly int _blockDurationMinutes;
        private readonly int _maxRequestSizeKB;
        private readonly int _maxHeaderSizeKB;

        public DDoSProtectionService(Logger logger, int maxRequestsPerMinute = 60,
            int maxConcurrentConnections = 20, int blockDurationMinutes = 10,
            int maxRequestSizeKB = 1024, int maxHeaderSizeKB = 8)
        {
            _logger = logger;
            _maxRequestsPerMinute = maxRequestsPerMinute;
            _maxConcurrentConnections = maxConcurrentConnections;
            _blockDurationMinutes = blockDurationMinutes;
            _maxRequestSizeKB = maxRequestSizeKB;
            _maxHeaderSizeKB = maxHeaderSizeKB;

            // Clean up expired blocks every minute
            _cleanupTimer = new Timer(CleanupExpiredBlocks, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public bool IsAllowed(HttpListenerRequest request)
        {
            var clientIP = GetClientIP(request);

            // Check if IP is blocked
            if (_blockedIPs.TryGetValue(clientIP, out var blockExpiry))
            {
                if (DateTime.UtcNow < blockExpiry)
                {
                    _logger.LogSecurity($"Blocked IP {clientIP} attempted to access {request.Url}");
                    return false;
                }

                // Block expired, remove it
                _blockedIPs.TryRemove(clientIP, out _);
            }

            // Check request size
            if (request.ContentLength64 > _maxRequestSizeKB * 1024)
            {
                _logger.LogSecurity($"Oversized request from {clientIP}: {request.ContentLength64} bytes");
                BlockIP(clientIP, "Oversized request");
                return false;
            }

            // Check header size
            int headerSize = 0;
            foreach (string headerName in request.Headers)
            {
                headerSize += headerName.Length + request.Headers[headerName].Length;
            }

            if (headerSize > _maxHeaderSizeKB * 1024)
            {
                _logger.LogSecurity($"Oversized headers from {clientIP}: {headerSize} bytes");
                BlockIP(clientIP, "Oversized headers");
                return false;
            }

            // Track and check rate limits
            var tracker = _ipTrackers.GetOrAdd(clientIP, _ => new RequestTracker());

            if (tracker.IncrementAndCheckLimit(_maxRequestsPerMinute))
            {
                _logger.LogSecurity($"Rate limit exceeded for IP {clientIP}");
                BlockIP(clientIP, "Rate limit exceeded");
                return false;
            }

            return true;
        }

        private string GetClientIP(HttpListenerRequest request)
        {
            // Check for X-Forwarded-For header (when behind a proxy)
            string xForwardedFor = request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(xForwardedFor))
            {
                return xForwardedFor.Split(',')[0].Trim();
            }

            // Check for X-Real-IP header
            string xRealIP = request.Headers["X-Real-IP"];
            if (!string.IsNullOrEmpty(xRealIP))
            {
                return xRealIP;
            }

            // Fall back to remote endpoint
            return request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        }

        private void BlockIP(string ip, string reason)
        {
            var blockExpiry = DateTime.UtcNow.AddMinutes(_blockDurationMinutes);
            _blockedIPs.AddOrUpdate(ip, blockExpiry, (_, _) => blockExpiry);
            _logger.LogSecurity($"IP {ip} blocked for {_blockDurationMinutes} minutes. Reason: {reason}");
        }

        private void CleanupExpiredBlocks(object state)
        {
            var now = DateTime.UtcNow;
            var expiredIPs = _blockedIPs.Where(kvp => kvp.Value < now).Select(kvp => kvp.Key).ToList();

            foreach (var ip in expiredIPs)
            {
                _blockedIPs.TryRemove(ip, out _);
            }

            // Also reset request counters
            foreach (var tracker in _ipTrackers.Values)
            {
                tracker.ResetIfExpired();
            }
        }

        private class RequestTracker
        {
            private int _requestCount;
            private DateTime _windowStart = DateTime.UtcNow;

            public bool IncrementAndCheckLimit(int maxRequests)
            {
                var now = DateTime.UtcNow;

                // Reset counter if window has expired
                if ((now - _windowStart).TotalMinutes >= 1)
                {
                    _requestCount = 1;
                    _windowStart = now;
                    return false;
                }

                _requestCount++;
                return _requestCount > maxRequests;
            }

            public void ResetIfExpired()
            {
                if ((DateTime.UtcNow - _windowStart).TotalMinutes >= 1)
                {
                    _requestCount = 0;
                    _windowStart = DateTime.UtcNow;
                }
            }
        }
    }
}