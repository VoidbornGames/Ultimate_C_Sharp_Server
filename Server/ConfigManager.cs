﻿using System;
using System.IO;
using Newtonsoft.Json;
using UltimateServer.Models;

namespace UltimateServer.Services
{
    public class ConfigManager
    {
        private readonly string _configFile;
        private readonly Logger _logger;

        public ServerConfig Config { get; private set; }

        public ConfigManager(string configFile = "config.json", Logger logger = null)
        {
            _configFile = configFile;
            _logger = logger ?? new Logger();
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configFile))
                {
                    string json = File.ReadAllText(_configFile);
                    Config = JsonConvert.DeserializeObject<ServerConfig>(json) ?? new ServerConfig();
                }
                else
                {
                    Config = new ServerConfig();
                    File.WriteAllText(_configFile, JsonConvert.SerializeObject(Config, Formatting.Indented));
                }
                _logger.Log("✅ Config loaded.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading config: {ex.Message}");
                Config = new ServerConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                string json = JsonConvert.SerializeObject(Config, Formatting.Indented);
                File.WriteAllText(_configFile, json);
                _logger.Log("✅ Config saved.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving config: {ex.Message}");
            }
        }
    }
}