using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Concurrent;
using UltimateServer.Models;
using UltimateServer.Services;

namespace UltimateServer.Services
{
    /// <summary>
    /// A simple, robust, file-based key-value database with enhanced features.
    /// It is thread-safe for all public operations.
    /// </summary>
    public class MiniDB
    {
        // A record is perfect for an immutable data carrier like this.
        public record IndexEntry(long Offset, int Length, string TypeName, DateTime LastModified);
        public readonly MiniDBOptions _options;

        private readonly Dictionary<string, IndexEntry> _index = new();
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(); // Enhanced locking for better read performance
        private readonly Logger _logger;
        private bool _isIndexDirty = false;
        private readonly Timer _autoSaveTimer;
        private readonly ConcurrentQueue<OperationMetric> _metrics = new();
        private long _totalOperations = 0;
        private long _readOperations = 0;
        private long _writeOperations = 0;

        public MiniDB(ConfigManager configManager, Logger logger)
        {
            _options = configManager.Config.MiniDB_Options;
            _logger = logger;

            // Set up auto-save timer if enabled
            if (_options.AutoSaveInterval > TimeSpan.Zero)
            {
                _autoSaveTimer = new Timer(AutoSaveCallback, null,
                    _options.AutoSaveInterval, _options.AutoSaveInterval);
            }
        }

        // ----------------------------------------------------------------------
        // LIFECYCLE
        // ----------------------------------------------------------------------
        public async Task Start()
        {
            EnsureFilesExist();
            await LoadIndexAsync();
            _logger.Log("📜 MiniDB has been started.");
        }

        public async Task Stop()
        {
            await SaveIndexAsync();
            _logger.Log("📜 MiniDB has been stopped.");
        }

        // ----------------------------------------------------------------------
        // CORE OPERATIONS
        // ----------------------------------------------------------------------
        /// <summary>
        /// Inserts a new object. Throws an exception if the key already exists.
        /// </summary>
        public async Task InsertDataAsync<T>(string key, T obj, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            var stopwatch = Stopwatch.StartNew();

            _lock.EnterWriteLock();
            try
            {
                if (_index.ContainsKey(key))
                {
                    throw new MiniDBException($"An entry with key '{key}' already exists. Use UpsertAsync to update.");
                }

                // We use a synchronous write inside the lock to prevent other threads
                // from interfering with the file pointer. The overall method is still async.
                PerformWrite(key, obj);
                _isIndexDirty = true;

                // Update metrics
                Interlocked.Increment(ref _totalOperations);
                Interlocked.Increment(ref _writeOperations);
                _metrics.Enqueue(new OperationMetric("Insert", stopwatch.Elapsed, key));
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // Trigger auto-save if needed
            if (_options.AutoSaveThreshold > 0 && _totalOperations % _options.AutoSaveThreshold == 0)
            {
                _ = Task.Run(async () => await SaveIndexAsync());
            }

            await Task.CompletedTask; // Keep the signature async.
        }

        /// <summary>
        /// Inserts a new object or updates it if the key already exists.
        /// </summary>
        public async Task UpsertDataAsync<T>(string key, T obj, CancellationToken cancellationToken = default)
        {
            ValidateKey(key);
            var stopwatch = Stopwatch.StartNew();

            _lock.EnterWriteLock();
            try
            {
                // If key exists, we are overwriting the data. The old data becomes garbage.
                // The new entry will simply point to the new data at the end of the file.
                PerformWrite(key, obj);
                _isIndexDirty = true;

                // Update metrics
                Interlocked.Increment(ref _totalOperations);
                Interlocked.Increment(ref _writeOperations);
                _metrics.Enqueue(new OperationMetric("Upsert", stopwatch.Elapsed, key));
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // Trigger auto-save if needed
            if (_options.AutoSaveThreshold > 0 && _totalOperations % _options.AutoSaveThreshold == 0)
            {
                _ = Task.Run(async () => await SaveIndexAsync());
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves an object by its key.
        /// </summary>
        public async Task<T?> GetDataAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            IndexEntry entry;

            _lock.EnterReadLock();
            try
            {
                if (!_index.TryGetValue(key, out entry))
                {
                    throw new MiniDBKeyNotFoundException(key);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            var requestedType = typeof(T);
            // Use AssemblyQualifiedName for robust type checking.
            if (entry.TypeName != requestedType.AssemblyQualifiedName)
            {
                throw new MiniDBTypeMismatchException(key, requestedType, Type.GetType(entry.TypeName)!);
            }

            // File operations are synchronous inside the lock for atomicity.
            using var fs = new FileStream(_options.DatabaseFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(entry.Offset, SeekOrigin.Begin);

            byte[] lenBytes = new byte[4];
            await fs.ReadAsync(lenBytes, 0, 4, cancellationToken);
            int dataLength = BitConverter.ToInt32(lenBytes);

            byte[] data = new byte[dataLength];
            await fs.ReadAsync(data, 0, dataLength, cancellationToken);

            // Update metrics
            Interlocked.Increment(ref _totalOperations);
            Interlocked.Increment(ref _readOperations);
            _metrics.Enqueue(new OperationMetric("Read", stopwatch.Elapsed, key));

            return Deserialize<T>(data);
        }

        /// <summary>
        /// Deletes a key from the index. The data remains in the file until compaction.
        /// </summary>
        public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();

            _lock.EnterWriteLock();
            try
            {
                if (_index.Remove(key))
                {
                    _isIndexDirty = true;

                    // Update metrics
                    Interlocked.Increment(ref _totalOperations);
                    _metrics.Enqueue(new OperationMetric("Delete", stopwatch.Elapsed, key));
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            await Task.CompletedTask;
        }

        // ----------------------------------------------------------------------
        // BATCH OPERATIONS
        // ----------------------------------------------------------------------
        /// <summary>
        /// Performs multiple operations in a batch for better performance.
        /// </summary>
        public async Task BatchOperationAsync(IEnumerable<BatchOperation> operations, CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var opsList = operations.ToList();

            _lock.EnterWriteLock();
            try
            {
                foreach (var op in opsList)
                {
                    try
                    {
                        switch (op.Type)
                        {
                            case BatchOperationType.Insert:
                                if (_index.ContainsKey(op.Key))
                                    throw new MiniDBException($"Key '{op.Key}' already exists.");
                                PerformWrite(op.Key, op.Data);
                                break;

                            case BatchOperationType.Upsert:
                                PerformWrite(op.Key, op.Data);
                                break;

                            case BatchOperationType.Delete:
                                _index.Remove(op.Key);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Error in batch operation for key '{op.Key}': {ex.Message}");
                        if (!op.ContinueOnError)
                            throw;
                    }
                }

                _isIndexDirty = true;
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // Update metrics
            Interlocked.Add(ref _totalOperations, opsList.Count);
            _metrics.Enqueue(new OperationMetric("Batch", stopwatch.Elapsed, $"{opsList.Count} operations"));

            await Task.CompletedTask;
        }

        // ----------------------------------------------------------------------
        // UTILITY & FEATURES
        // ----------------------------------------------------------------------
        public async Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        {
            _lock.EnterReadLock();
            try
            {
                return _index.ContainsKey(key);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public async Task<IEnumerable<string>> GetAllKeysAsync(CancellationToken cancellationToken = default)
        {
            _lock.EnterReadLock();
            try
            {
                return _index.Keys.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Gets database statistics and metrics.
        /// </summary>
        public async Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default)
        {
            _lock.EnterReadLock();
            try
            {
                return new DatabaseStats
                {
                    TotalEntries = _index.Count,
                    TotalOperations = _totalOperations,
                    ReadOperations = _readOperations,
                    WriteOperations = _writeOperations,
                    DatabaseFileSize = new FileInfo(_options.DatabaseFile).Length,
                    IndexFileSize = new FileInfo(_options.IndexFile).Length,
                    LastModified = _index.Values.OrderByDescending(e => e.LastModified).FirstOrDefault()?.LastModified ?? DateTime.MinValue,
                    RecentMetrics = _metrics.TakeLast(100).ToList()
                };
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Rebuilds the database file to remove space from deleted records.
        /// This is a long-running operation and should be called during maintenance periods.
        /// </summary>
        public async Task CompactAsync(CancellationToken cancellationToken = default)
        {
            _logger.Log("🗜️ Starting database compaction...");
            string tempDbFile = Path.Combine(Path.GetDirectoryName(_options.DatabaseFile)!, "temp_" + Path.GetFileName(_options.DatabaseFile));
            var newIndex = new Dictionary<string, IndexEntry>();

            _lock.EnterWriteLock();
            try
            {
                using (var sourceFs = new FileStream(_options.DatabaseFile, FileMode.Open, FileAccess.Read))
                using (var destFs = new FileStream(tempDbFile, FileMode.Create, FileAccess.Write))
                {
                    foreach (var (key, entry) in _index)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        sourceFs.Seek(entry.Offset, SeekOrigin.Begin);

                        byte[] recordData = new byte[entry.Length];
                        await sourceFs.ReadAsync(recordData, 0, entry.Length, cancellationToken);

                        long newOffset = destFs.Position;
                        await destFs.WriteAsync(recordData, 0, entry.Length, cancellationToken);

                        newIndex[key] = entry with { Offset = newOffset };
                    }
                }

                // Atomically replace old files with new ones
                File.Replace(tempDbFile, _options.DatabaseFile, null);
                _index.Clear();
                foreach (var kvp in newIndex) _index.Add(kvp.Key, kvp.Value);
                _isIndexDirty = true; // Mark as dirty so the new index is saved.

                _logger.Log("✅ Database compaction completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Database compaction failed: {ex.Message}");
                if (File.Exists(tempDbFile)) File.Delete(tempDbFile);
                throw new MiniDBException("Compaction failed.", ex);
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            await Task.CompletedTask;
        }

        // ----------------------------------------------------------------------
        // PRIVATE HELPERS
        // ----------------------------------------------------------------------
        private void PerformWrite<T>(string key, T obj)
        {
            byte[] data = Serialize(obj);
            int dataLength = data.Length;

            using var fs = new FileStream(_options.DatabaseFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            long offset = fs.Position;

            byte[] lenPrefix = BitConverter.GetBytes(dataLength);
            fs.Write(lenPrefix);
            fs.Write(data);

            _index[key] = new IndexEntry(offset, dataLength + 4, typeof(T).AssemblyQualifiedName!, DateTime.UtcNow);
        }

        private void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or whitespace.", nameof(key));
            if (key.Contains('|'))
                throw new ArgumentException("Key cannot contain the '|' character.", nameof(key));
        }

        private void EnsureFilesExist()
        {
            if (!File.Exists(_options.IndexFile)) File.Create(_options.IndexFile).Dispose();
            if (!File.Exists(_options.DatabaseFile)) File.Create(_options.DatabaseFile).Dispose();
        }

        private async Task LoadIndexAsync()
        {
            _index.Clear();
            var lines = await File.ReadAllLinesAsync(_options.IndexFile);
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length == 5) // Updated to include LastModified
                {
                    _index[parts[0]] = new IndexEntry(
                        long.Parse(parts[1]),
                        int.Parse(parts[2]),
                        parts[3],
                        DateTime.Parse(parts[4])
                    );
                }
            }
            _isIndexDirty = false;
        }

        private async Task SaveIndexAsync()
        {
            if (!_isIndexDirty) return;

            List<string> lines = _index.Select
                (kvp => $"{kvp.Key}|{kvp.Value.Offset}|{kvp.Value.Length}|{kvp.Value.TypeName}|{kvp.Value.LastModified:o}").ToList();

            await File.WriteAllLinesAsync(_options.IndexFile, lines);
            _isIndexDirty = false;
        }

        private static byte[] Serialize<T>(T obj)
        {
            using var ms = new MemoryStream();
            var serializer = new DataContractSerializer(typeof(T));
            serializer.WriteObject(ms, obj);
            return ms.ToArray();
        }

        private static T Deserialize<T>(byte[] data)
        {
            using var ms = new MemoryStream(data);
            var serializer = new DataContractSerializer(typeof(T));
            return (T)serializer.ReadObject(ms)!;
        }

        private async void AutoSaveCallback(object state)
        {
            try
            {
                await SaveIndexAsync();
                //_logger.Log("📜 Auto-saved database index.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Auto-save failed: {ex.Message}");
            }
        }
    }

    // ----------------------------------------------------------------------
    // SUPPORTING CLASSES
    // ----------------------------------------------------------------------
    public class MiniDBOptions
    {
        public string DatabaseFile { get; set; } = "minidb.dat";
        public string IndexFile { get; set; } = "minidb.idx";
        public TimeSpan AutoSaveInterval { get; set; } = TimeSpan.FromMinutes(5);
        public int AutoSaveThreshold { get; set; } = 100;
    }

    public class BatchOperation
    {
        public string Key { get; set; } = string.Empty;
        public object Data { get; set; } = null!;
        public BatchOperationType Type { get; set; }
        public bool ContinueOnError { get; set; } = false;
    }

    public enum BatchOperationType
    {
        Insert,
        Upsert,
        Delete
    }

    public class DatabaseStats
    {
        public int TotalEntries { get; set; }
        public long TotalOperations { get; set; }
        public long ReadOperations { get; set; }
        public long WriteOperations { get; set; }
        public long DatabaseFileSize { get; set; }
        public long IndexFileSize { get; set; }
        public DateTime LastModified { get; set; }
        public List<OperationMetric> RecentMetrics { get; set; } = new();
    }

    public class OperationMetric
    {
        public string OperationType { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public string Target { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public OperationMetric(string operationType, TimeSpan duration, string target)
        {
            OperationType = operationType;
            Duration = duration;
            Target = target;
        }
    }

    // Custom exceptions
    public class MiniDBException : Exception
    {
        public MiniDBException(string message) : base(message) { }
        public MiniDBException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MiniDBKeyNotFoundException : MiniDBException
    {
        public MiniDBKeyNotFoundException(string key) : base($"Key '{key}' not found.") { }
    }

    public class MiniDBTypeMismatchException : MiniDBException
    {
        public MiniDBTypeMismatchException(string key, Type expectedType, Type actualType)
            : base($"Type mismatch for key '{key}'. Expected: {expectedType.Name}, Actual: {actualType.Name}") { }
    }
}