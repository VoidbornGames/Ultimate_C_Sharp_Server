using System;
using System.Runtime.Serialization;
using UltimateServer.Services;

namespace Server.Services
{
    public class MiniDB
    {
        public class IndexEntry
        {
            public long Offset { get; set; }
            public int Length { get; set; }
            public string TypeName { get; set; } = "";
        }

        private readonly Dictionary<string, IndexEntry> index;
        public readonly string indexFile = "mdb.index";
        public readonly string databaseFile = "server.mdb";
        private readonly Logger _logger;

        public MiniDB(Logger logger)
        {
            index = new();
            _logger = logger;
        }

        // ----------------------------------------------------------------------
        // START
        // ----------------------------------------------------------------------
        public async Task Start()
        {
            if (!File.Exists(indexFile))
                File.Create(indexFile).Dispose();

            if (!File.Exists(databaseFile))
                File.Create(databaseFile).Dispose();

            await LoadIndexAsync();
            _logger.Log("📜 MiniDB has been started");
        }

        // ----------------------------------------------------------------------
        // STOP
        // ----------------------------------------------------------------------
        public async Task Stop()
        {
            await SaveIndexAsync();
            _logger.Log("📜 MiniDB has been stopped");
        }

        // ----------------------------------------------------------------------
        // INDEX SYSTEM
        // ----------------------------------------------------------------------
        private async Task LoadIndexAsync()
        {
            index.Clear();
            var lines = await File.ReadAllLinesAsync(indexFile);

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length != 4) continue;

                index[parts[0]] = new IndexEntry
                {
                    Offset = long.Parse(parts[1]),
                    Length = int.Parse(parts[2]),
                    TypeName = parts[3]
                };
            }
        }

        private async Task SaveIndexAsync()
        {
            List<string> lines = new();

            foreach (var (key, entry) in index)
                lines.Add($"{key}|{entry.Offset}|{entry.Length}|{entry.TypeName}");

            await File.WriteAllLinesAsync(indexFile, lines);
        }

        // ----------------------------------------------------------------------
        // SERIALIZATION
        // ----------------------------------------------------------------------
        private byte[] Serialize<T>(T obj)
        {
            using var ms = new MemoryStream();
            var serializer = new DataContractSerializer(typeof(T));
            serializer.WriteObject(ms, obj);
            return ms.ToArray();
        }

        private T Deserialize<T>(byte[] data)
        {
            using var ms = new MemoryStream(data);
            var serializer = new DataContractSerializer(typeof(T));
            return (T)serializer.ReadObject(ms);
        }

        // ----------------------------------------------------------------------
        // INSERT DATA
        // ----------------------------------------------------------------------
        public async Task InsertDataAsync<T>(string key, T obj)
        {
            if (key.Contains("|"))
            {
                _logger.LogError($"[MiniDB] You cant use character '|' in a key!");
                return;
            }

            try
            {
                byte[] data = Serialize(obj);
                int length = data.Length;

                using var fs = new FileStream(databaseFile, FileMode.Append, FileAccess.Write, FileShare.Read);

                long offset = fs.Position;

                // length prefix
                byte[] lenBytes = BitConverter.GetBytes(length);
                await fs.WriteAsync(lenBytes);

                // actual object bytes
                await fs.WriteAsync(data);

                index[key] = new IndexEntry
                {
                    Offset = offset,
                    Length = length + 4,
                    TypeName = typeof(T).AssemblyQualifiedName!
                };

                await SaveIndexAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError("MiniDB Insert Error: " + ex.Message);
            }
        }

        // ----------------------------------------------------------------------
        // GET DATA (TYPED)
        // ----------------------------------------------------------------------
        public async Task<T?> GetDataAsync<T>(string key)
        {
            try
            {
                if (!index.TryGetValue(key, out var entry))
                    return default;

                if (entry.TypeName != typeof(T).AssemblyQualifiedName)
                    throw new Exception($"Type mismatch: Key '{key}' does not contain a {typeof(T).Name}");

                using var fs = new FileStream(databaseFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(entry.Offset, SeekOrigin.Begin);

                // read length
                byte[] lenBytes = new byte[4];
                await fs.ReadAsync(lenBytes);
                int length = BitConverter.ToInt32(lenBytes);

                // read data
                byte[] data = new byte[length];
                await fs.ReadAsync(data);

                return Deserialize<T>(data);
            }
            catch (Exception ex)
            {
                _logger.LogError("MiniDB Get Error: " + ex.Message);
                return default;
            }
        }

        // ----------------------------------------------------------------------
        // DELETE
        // ----------------------------------------------------------------------
        public async Task DeleteAsync(string key)
        {
            if (index.Remove(key))
                await SaveIndexAsync();
        }
    }
}
