using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UltimateServer.Models;
using UltimateServer.Services;

namespace Server.Services
{
    class DataBackuper
    {
        public List<string> _serverFiles;

        private string _backupPath = "Backups";
        private Logger _logger;


        public DataBackuper(Logger logger, FilePaths filePaths, SitePress sitePress, DataBox dataBox, ConfigManager configManager)
        {
            _logger = logger;
            _serverFiles = new List<string>();

            _serverFiles.Add(filePaths.UsersFile);
            _serverFiles.Add(filePaths.ConfigFile);
            _serverFiles.Add(sitePress.sitesConfig);
            _serverFiles.Add(dataBox.saveFile);
            _serverFiles.Add(configManager.Config.MiniDB_Options.IndexFile);
            _serverFiles.Add(configManager.Config.MiniDB_Options.DatabaseFile);
            _serverFiles.Add("sftp.json");
        }

        public async Task Start()
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await BackupServer();
                    await Task.Delay(TimeSpan.FromHours(12));
                }
            });
        }

        private async Task BackupServer()
        {
            // --- Start the compression process ---
            try
            {
                if (!Directory.Exists(_backupPath)) Directory.CreateDirectory(_backupPath);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var destinationZipFilePath = Path.Combine(_backupPath, $"Backup_{timestamp}.zip");

                // 1. CREATE THE ZIP FILE AND STREAM
                // The 'using' statements ensure that resources are properly closed and disposed.
                using (FileStream zipToCreate = new FileStream(destinationZipFilePath, FileMode.Create))
                using (ZipArchive archive = new ZipArchive(zipToCreate, ZipArchiveMode.Create))
                {
                    // 2. LOOP THROUGH EACH FILE IN YOUR LIST
                    foreach (string fileToAdd in _serverFiles)
                    {
                        // Check if the file actually exists before trying to add it
                        if (File.Exists(fileToAdd))
                        {
                            // Get just the filename (e.g., "report.docx") to use inside the zip
                            string entryName = Path.GetFileName(fileToAdd);

                            // 3. CREATE AN ENTRY FOR THE FILE IN THE ZIP ARCHIVE
                            ZipArchiveEntry zipEntry = archive.CreateEntry(entryName);

                            // 4. COPY THE FILE'S CONTENTS INTO THE ZIP ENTRY
                            using (FileStream fileStream = new FileStream(fileToAdd, FileMode.Open, FileAccess.Read))
                            using (Stream entryStream = zipEntry.Open())
                            {
                                fileStream.CopyTo(entryStream);
                            }
                        }
                        else
                        {
                            _logger.LogWarning($"[DataBackuper] File not found and skipped: {fileToAdd}");
                        }
                    }
                }

                _logger.Log($"📼 Backup complete! Archive created at: {destinationZipFilePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[DataBackuper] An error occurred: {ex.Message}");
            }
        }
    }
}
