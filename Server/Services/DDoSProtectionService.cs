using System.Collections.Concurrent;
using System.Net;

namespace UltimateServer.Services
{
    /// <summary>
    /// Production-grade L7 Anti-DDoS / Anti-Abuse service.
    /// 
    /// Key design rules:
    /// - Rate limit applies to ALL requests
    /// - Concurrency limit applies ONLY to long-lived / expensive requests
    /// - GET / static / panel traffic is NEVER concurrency-limited
    /// - Browser parallelism is respected
    /// </summary>
    public sealed class DDoSProtectionService : IDisposable
    {
        private readonly Logger _logger;

        /* ================= CONFIG ================= */

        private readonly int _maxRequestsPerMinute;
        private readonly int _maxConcurrentConnections;
        private readonly int _blockMinutesBase;
        private readonly int _maxRequestSizeBytes;
        private readonly int _maxHeaderSizeBytes;
        private readonly int _maxTrackedIPs;

        /* ================= STATE ================= */

        private readonly ConcurrentDictionary<string, IpTracker> _trackers = new();
        private readonly ConcurrentDictionary<string, BlockEntry> _blocked = new();
        private readonly ConcurrentDictionary<string, int> _activeConnections = new();
        private readonly Timer _cleanupTimer;

        /* ================= TRUST ================= */

        private static readonly IPAddress[] TrustedProxies =
        {
            IPAddress.Loopback,
            IPAddress.IPv6Loopback
            // Add reverse proxy IPs here
        };

        public DDoSProtectionService(
            Logger logger,
            int maxRequestsPerMinute = 120,
            int maxConcurrentConnections = 8,
            int blockMinutesBase = 2,
            int maxRequestSizeKB = 1024,
            int maxHeaderSizeKB = 16,
            int maxTrackedIPs = 100_000)
        {
            _logger = logger;
            _maxRequestsPerMinute = maxRequestsPerMinute;
            _maxConcurrentConnections = maxConcurrentConnections;
            _blockMinutesBase = blockMinutesBase;
            _maxRequestSizeBytes = maxRequestSizeKB * 1024;
            _maxHeaderSizeBytes = maxHeaderSizeKB * 1024;
            _maxTrackedIPs = maxTrackedIPs;

            _cleanupTimer = new Timer(Cleanup, null,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));
        }

        /* ================= PUBLIC API ================= */

        public bool IsAllowed(HttpListenerRequest request)
        {
            var ip = ResolveClientIp(request);
            var now = DateTime.UtcNow;

            if (IsBlocked(ip, now))
                return false;

            // Payload protection
            if (request.ContentLength64 > _maxRequestSizeBytes)
                return Strike(ip, "Payload too large");

            if (CalculateHeaderSize(request) > _maxHeaderSizeBytes)
                return Strike(ip, "Headers too large");

            // Rate limiting (ALL requests)
            var tracker = _trackers.GetOrAdd(ip, _ => new IpTracker(_maxRequestsPerMinute));
            tracker.Touch(now);

            if (!tracker.TryConsume(now))
                return Strike(ip, "Rate limit exceeded");

            // Concurrency limiting (ONLY expensive requests)
            if (ShouldCountConcurrency(request))
            {
                var active = _activeConnections.AddOrUpdate(ip, 1, (_, v) => v + 1);
                if (active > _maxConcurrentConnections)
                {
                    _activeConnections.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
                    _logger.LogSecurity($"IP {ip} exceeded concurrent connection limit");
                    return false;
                }
            }

            return true;
        }

        public void OnRequestFinished(HttpListenerRequest request)
        {
            if (!ShouldCountConcurrency(request))
                return;

            var ip = ResolveClientIp(request);
            _activeConnections.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
        }

        /* ================= CORE LOGIC ================= */

        private bool Strike(string ip, string reason)
        {
            var tracker = _trackers.GetOrAdd(ip, _ => new IpTracker(_maxRequestsPerMinute));
            tracker.Strikes++;

            var blockMinutes = _blockMinutesBase * tracker.Strikes;
            _blocked[ip] = new BlockEntry(DateTime.UtcNow.AddMinutes(blockMinutes));

            _logger.LogSecurity($"IP {ip} blocked for {blockMinutes}m | {reason}");
            return false;
        }

        private bool IsBlocked(string ip, DateTime now)
        {
            if (_blocked.TryGetValue(ip, out var entry))
            {
                if (entry.ExpiresAt > now)
                    return true;

                _blocked.TryRemove(ip, out _);
            }

            return false;
        }

        /* ================= CLEANUP ================= */

        private void Cleanup(object _)
        {
            var now = DateTime.UtcNow;

            foreach (var b in _blocked.Where(x => x.Value.ExpiresAt <= now).ToList())
                _blocked.TryRemove(b);

            foreach (var t in _trackers.Where(x => (now - x.Value.LastSeen).TotalMinutes > 5).ToList())
                _trackers.TryRemove(t);

            if (_trackers.Count > _maxTrackedIPs)
            {
                var excess = _trackers.Count - _maxTrackedIPs;
                foreach (var t in _trackers.OrderBy(x => x.Value.LastSeen).Take(excess))
                    _trackers.TryRemove(t);

                _logger.LogSecurity("Tracker eviction triggered due to IP flood");
            }
        }

        /* ================= UTIL ================= */

        private static bool ShouldCountConcurrency(HttpListenerRequest request)
        {
            if (request.HttpMethod == "POST") return true;
            if (request.HttpMethod == "PUT") return true;
            if (request.HttpMethod == "PATCH") return true;

            if (request.Headers["Upgrade"]?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true)
                return true;

            return false; // GET / static / panel traffic ignored
        }

        private static int CalculateHeaderSize(HttpListenerRequest request)
        {
            int size = 0;
            foreach (string key in request.Headers)
                size += key.Length + request.Headers[key].Length;
            return size;
        }

        private static string ResolveClientIp(HttpListenerRequest request)
        {
            var remote = request.RemoteEndPoint?.Address;

            if (remote != null && TrustedProxies.Any(p => p.Equals(remote)))
            {
                var xff = request.Headers["X-Forwarded-For"];
                if (!string.IsNullOrWhiteSpace(xff))
                    return xff.Split(',')[0].Trim();
            }

            return remote?.ToString() ?? "unknown";
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();
        }

        /* ================= MODELS ================= */

        private sealed class BlockEntry
        {
            public DateTime ExpiresAt;
            public BlockEntry(DateTime expires) => ExpiresAt = expires;
        }

        private sealed class IpTracker
        {
            private double _tokens;
            private readonly double _capacity;
            private readonly double _refillRate;
            private DateTime _lastRefill;

            public int Strikes;
            public DateTime LastSeen { get; private set; }

            public IpTracker(int maxPerMinute)
            {
                _capacity = maxPerMinute;
                _refillRate = maxPerMinute / 60d;
                _tokens = _capacity;
                _lastRefill = DateTime.UtcNow;
                LastSeen = DateTime.UtcNow;
            }

            public void Touch(DateTime now) => LastSeen = now;

            public bool TryConsume(DateTime now)
            {
                var elapsed = (now - _lastRefill).TotalSeconds;
                _tokens = Math.Min(_capacity, _tokens + elapsed * _refillRate);
                _lastRefill = now;

                if (_tokens < 1)
                    return false;

                _tokens--;
                return true;
            }
        }
    }
}