using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace UltimateServer.Services
{
    public class DataBoxOptions
    {
        public string SaveFile { get; set; } = "server.data";
        public bool AutoSave { get; set; } = true;
        public int AutoSaveIntervalMs { get; set; } = 30000;
        public int MaxBackupFiles { get; set; } = 5;
        public bool EnableCompression { get; set; } = false;
    }

    public class DataBox : IDisposable
    {
        private readonly ConcurrentDictionary<string, object> _data;
        private readonly Logger _logger;
        public readonly DataBoxOptions _options;
        private readonly object _lock = new object();
        private Timer _autoSaveTimer;
        private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);
        private bool _disposed = false;

        public DataBox(Logger logger, DataBoxOptions options = null)
        {
            _logger = logger;
            _options = options ?? new DataBoxOptions();
            _data = new ConcurrentDictionary<string, object>();

            if (_options.AutoSave)
            {
                _autoSaveTimer = new Timer(AutoSaveCallback, null,
                    TimeSpan.FromMicroseconds(_options.AutoSaveIntervalMs),
                    TimeSpan.FromMilliseconds(_options.AutoSaveIntervalMs));
            }
        }

        public async Task Start()
        {
            try
            {
                await LoadAsync();
                _logger.Log("📦 DataBox started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start DataBox: {ex.Message}");
                _data.Clear();
            }
        }

        public async Task Stop()
        {
            try
            {
                if (_options.AutoSave)
                {
                    _autoSaveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }

                await Save();
                _logger.Log("📦 DataBox stopped and data saved");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during DataBox shutdown: {ex.Message}");
            }
        }

        public async Task Save()
        {
            if (_disposed) return;

            await _saveSemaphore.WaitAsync();
            try
            {
                string json = JsonConvert.SerializeObject(_data, Formatting.Indented);

                await File.WriteAllTextAsync(_options.SaveFile, json);
                //_logger.Log("Data saved successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save data: {ex.Message}");
                throw;
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        public async Task LoadAsync()
        {
            if (!File.Exists(_options.SaveFile))
            {
                //_logger.LogDebug("Save file not found, creating new data dictionary");
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(_options.SaveFile);
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                if (data != null)
                {
                    _data.Clear();
                    foreach (var kvp in data)
                    {
                        _data.TryAdd(kvp.Key, kvp.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load data: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> SaveData<T>(string key, T data)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogError("Key cannot be null or empty");
                return false;
            }

            try
            {
                _data.AddOrUpdate(key, data, (k, v) => data);

                if (_options.AutoSave)
                {
                    _ = Task.Run(async () => await Save());
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save data for key '{key}': {ex.Message}");
                return false;
            }
        }

        public async Task<T> LoadData<T>(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogError("Key cannot be null or empty");
                return default(T);
            }

            try
            {
                if (_data.TryGetValue(key, out object value))
                {
                    if (value is T directValue)
                        return directValue;

                    string json = JsonConvert.SerializeObject(value);
                    return JsonConvert.DeserializeObject<T>(json);
                }

                return default(T);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load data for key '{key}': {ex.Message}");
                return default(T);
            }
        }

        public async Task<T> LoadDataAsync<T>(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogError("Key cannot be null or empty");
                return default(T);
            }

            try
            {
                if (_data.TryGetValue(key, out object value))
                {
                    if (value is T directValue)
                        return directValue;

                    string json = JsonConvert.SerializeObject(value);
                    return JsonConvert.DeserializeObject<T>(json);
                }

                await LoadAsync();

                if (_data.TryGetValue(key, out value))
                {
                    if (value is T directValue)
                        return directValue;

                    string json = JsonConvert.SerializeObject(value);
                    return JsonConvert.DeserializeObject<T>(json);
                }

                return default(T);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load data for key '{key}': {ex.Message}");
                return default(T);
            }
        }

        public async Task<bool> ContainsKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            return _data.ContainsKey(key);
        }

        public  async Task<bool>  RemoveData(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            try
            {
                bool removed = _data.TryRemove(key, out _);

                if (removed && _options.AutoSave)
                {
                    _ = Task.Run(async () => await Save());
                }

                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to remove data for key '{key}': {ex.Message}");
                return false;
            }
        }

        public async Task<T> FirstOrDefault<T>(string key, T defaultValue = default(T))
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogError("Key cannot be null or empty");
                return defaultValue;
            }

            try
            {
                if (_data.TryGetValue(key, out object value))
                {
                    if (value is T directValue)
                        return directValue;

                    try
                    {
                        string json = JsonConvert.SerializeObject(value);
                        return JsonConvert.DeserializeObject<T>(json);
                    }
                    catch
                    {
                        return defaultValue;
                    }
                }

                return defaultValue;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load data for key '{key}': {ex.Message}");
                return defaultValue;
            }
        }

        public IEnumerable<string> GetAllKeys()
        {
            return _data.Keys.ToList();
        }

        public void Clear()
        {
            _data.Clear();

            if (_options.AutoSave)
            {
                _ = Save();
            }
        }

        private async void AutoSaveCallback(object state)
        {
            if (_disposed) return;

            try
            {
                await Save();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Auto-save failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _autoSaveTimer?.Dispose();
            _saveSemaphore?.Dispose();

            _ = Task.Run(async () => await Save());
        }
    }

    public static class FileExtensions
    {
        public static Task CopyAsync(string sourceFileName, string destFileName, bool overwrite)
        {
            return Task.Run(() => File.Copy(sourceFileName, destFileName, overwrite));
        }
    }
}