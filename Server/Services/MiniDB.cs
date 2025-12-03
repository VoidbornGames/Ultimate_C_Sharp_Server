using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services; // Assuming Logger is here

namespace Server.Services
{

    /// <summary>
    /// A simple, robust, file-based key-value database.
    /// It is thread-safe for all public operations.
    /// </summary>
    public class MiniDB
    {
        // A record is perfect for an immutable data carrier like this.
        public record IndexEntry(long Offset, int Length, string TypeName);
        public readonly MiniDBOptions _options;

        private readonly Dictionary<string, IndexEntry> _index = new();
        private readonly object _lock = new object(); // For thread safety
        private readonly Logger _logger;
        private bool _isIndexDirty = false;

        public MiniDB(ConfigManager configManager, Logger logger)
        {
            _options = configManager.Config.MiniDB_Options ?? throw new ArgumentNullException(nameof(configManager.Config.MiniDB_Options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        public async Task InsertDataAsync<T>(string key, T obj)
        {
            ValidateKey(key);
            lock (_lock)
            {
                if (_index.ContainsKey(key))
                {
                    throw new MiniDBException($"An entry with key '{key}' already exists. Use UpsertAsync to update.");
                }
                // We use a synchronous write inside the lock to prevent other threads
                // from interfering with the file pointer. The overall method is still async.
                PerformWrite(key, obj);
            }
            // The index is now dirty, but we save lazily.
            _isIndexDirty = true;
            await Task.CompletedTask; // Keep the signature async.
        }

        /// <summary>
        /// Inserts a new object or updates it if the key already exists.
        /// </summary>
        public async Task UpsertDataAsync<T>(string key, T obj)
        {
            ValidateKey(key);
            lock (_lock)
            {
                // If key exists, we are overwriting the data. The old data becomes garbage.
                // The new entry will simply point to the new data at the end of the file.
                PerformWrite(key, obj);
            }
            _isIndexDirty = true;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Retrieves an object by its key.
        /// </summary>
        public async Task<T?> GetDataAsync<T>(string key)
        {
            lock (_lock)
            {
                if (!_index.TryGetValue(key, out var entry))
                {
                    throw new MiniDBKeyNotFoundException(key);
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
                fs.Read(lenBytes, 0, 4);
                int dataLength = BitConverter.ToInt32(lenBytes);

                byte[] data = new byte[dataLength];
                fs.Read(data, 0, dataLength);

                return Deserialize<T>(data);
            }
        }

        /// <summary>
        /// Deletes a key from the index. The data remains in the file until compaction.
        /// </summary>
        public async Task DeleteAsync(string key)
        {
            lock (_lock)
            {
                if (_index.Remove(key))
                {
                    _isIndexDirty = true;
                }
            }
            await Task.CompletedTask;
        }

        // ----------------------------------------------------------------------
        // UTILITY & FEATURES
        // ----------------------------------------------------------------------
        public async Task<bool> ContainsKeyAsync(string key)
        {
            lock (_lock)
            {
                return _index.ContainsKey(key);
            }
        }

        public async Task<IEnumerable<string>> GetAllKeysAsync()
        {
            lock (_lock)
            {
                return _index.Keys.ToList();
            }
        }

        /// <summary>
        /// Rebuilds the database file to remove space from deleted records.
        /// This is a long-running operation and should be called during maintenance periods.
        /// </summary>
        public async Task CompactAsync()
        {
            _logger.Log("🗜️ Starting database compaction...");
            string tempDbFile = Path.Combine(Path.GetDirectoryName(_options.DatabaseFile)!, "temp_" + Path.GetFileName(_options.DatabaseFile));
            var newIndex = new Dictionary<string, IndexEntry>();

            lock (_lock)
            {
                try
                {
                    using (var sourceFs = new FileStream(_options.DatabaseFile, FileMode.Open, FileAccess.Read))
                    using (var destFs = new FileStream(tempDbFile, FileMode.Create, FileAccess.Write))
                    {
                        foreach (var (key, entry) in _index)
                        {
                            sourceFs.Seek(entry.Offset, SeekOrigin.Begin);

                            byte[] recordData = new byte[entry.Length];
                            sourceFs.Read(recordData, 0, entry.Length);

                            long newOffset = destFs.Position;
                            destFs.Write(recordData, 0, entry.Length);

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

            _index[key] = new IndexEntry(offset, dataLength + 4, typeof(T).AssemblyQualifiedName!);
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
                if (parts.Length == 4)
                {
                    _index[parts[0]] = new IndexEntry(
                        long.Parse(parts[1]),
                        int.Parse(parts[2]),
                        parts[3]
                    );
                }
            }
            _isIndexDirty = false;
        }

        private async Task SaveIndexAsync()
        {
            if (!_isIndexDirty) return;

            List<string> lines = _index.Select(kvp =>
                $"{kvp.Key}|{kvp.Value.Offset}|{kvp.Value.Length}|{kvp.Value.TypeName}"
            ).ToList();

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
    }
}