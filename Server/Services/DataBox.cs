using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using UltimateServer.Services;

namespace Server.Services
{
    public class DataBox
    {
        private Dictionary<string, object> Data;
        private readonly Logger _logger;
        private readonly string saveFile = "server.data";
        private readonly object _lock = new object(); // For thread safety

        public DataBox(Logger logger)
        {
            _logger = logger;
            Data = new Dictionary<string, object>();
        }

        public async Task StartAsync()
        {
            try
            {
                Data = await Load();
                _logger.Log("📦 DataBox started successfully");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to load data: {e.Message}");
                Data = new Dictionary<string, object>();
            }
        }

        public async Task StopAsync()
        {
            await Save();
            _logger.Log("📦 DataBox stopped and data saved");
        }

        public async Task Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(Data, Formatting.Indented);
                await File.WriteAllTextAsync(saveFile, json);
                //_logger.Log("Data saved successfully");
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to save data: {e.Message}");
                throw;
            }
        }

        public async Task<Dictionary<string, object>> Load()
        {
            if (!File.Exists(saveFile))
            {
                //_logger.Log("Save file not found, creating new data dictionary");
                return new Dictionary<string, object>();
            }

            try
            {
                string json = await File.ReadAllTextAsync(saveFile);
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                return data ?? new Dictionary<string, object>();
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to load data: {e.Message}");
                return new Dictionary<string, object>();
            }
        }

        // Generic method to save data with automatic type conversion
        public async Task<bool> SaveData<T>(string key, T data)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogError("Key cannot be null or empty");
                return false;
            }

            try
            {
                lock (_lock) // Ensure thread safety
                {
                    Data[key] = data; // This will add or update the key
                }

                return true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to save data for key '{key}': {e.Message}");
                return false;
            }
        }

        // Generic method to load data with automatic type conversion
        public async Task<T> LoadData<T>(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogError("Key cannot be null or empty");
                return default(T);
            }

            try
            {
                // First check in-memory data
                if (Data.TryGetValue(key, out object value))
                {
                    // If the value is already of the requested type, return it directly
                    if (value is T directValue)
                        return directValue;

                    // Otherwise, try to convert it
                    string json = JsonConvert.SerializeObject(value);
                    return JsonConvert.DeserializeObject<T>(json);
                }

                // The code below will check the save file to be sure but it will increase I/O and CPU usage!
                // Using it is optional but not recommanded.
                /*
                // If not found, reload from file and check again
                Data = await Load();
                if (Data.TryGetValue(key, out value))
                {
                    // If the value is already of the requested type, return it directly
                    if (value is T directValue)
                        return directValue;

                    // Otherwise, try to convert it
                    string json = JsonConvert.SerializeObject(value);
                    return JsonConvert.DeserializeObject<T>(json);
                }
                */

                return default(T);
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to load data for key '{key}': {e.Message}");
                return default(T);
            }
        }

        // Method to check if key exists
        public async Task<bool> ContainsKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (Data.ContainsKey(key))
                return true;

            return Data.ContainsKey(key);
        }

        // Method to remove data
        public async Task<bool> RemoveData(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            try
            {
                lock (_lock)
                {
                    if (Data.Remove(key))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to remove data for key '{key}': {e.Message}");
                return false;
            }
        }

        // Generic method to load data with a defualt value if it failed or dont exist
        public async Task<T> FirstOrDefault<T>(string Key, T Default)
        {
            if (string.IsNullOrWhiteSpace(Key))
            {
                _logger.LogError("Key cannot be null or empty");
                return default(T);
            }

            try
            {
                if (Data.TryGetValue(Key, out object value))
                {
                    // If the value is already of the requested type, return it directly
                    if (value is T directValue)
                    {
                        return directValue;
                    }
                    else
                    {
                        // Otherwise, try to convert it
                        string json = JsonConvert.SerializeObject(value);
                        T jsonData = JsonConvert.DeserializeObject<T>(json);

                        if (jsonData is T directJsonValue)
                            return directJsonValue;
                        else
                            return Default;
                    }
                }
                else
                    return Default;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to load data for key '{Key}': {e.Message}");
                return default(T);
            }
        }
    }
}

// Hehe