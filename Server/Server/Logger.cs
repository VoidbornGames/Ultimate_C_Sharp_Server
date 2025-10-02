using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace UltimateServer.Services
{
    public class Logger
    {
        private readonly string _logsFolder;
        private readonly string _logFile;
        private readonly object _logLock = new();

        public Logger(string logsFolder = "logs")
        {
            _logsFolder = logsFolder;
            _logFile = Path.Combine(_logsFolder, "latest.log");
            PrepareLogs();
        }

        private void PrepareLogs()
        {
            try
            {
                if (!Directory.Exists(_logsFolder)) Directory.CreateDirectory(_logsFolder);

                if (File.Exists(_logFile))
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string zipFile = Path.Combine(_logsFolder, $"latest_{timestamp}.zip");

                    using var archive = System.IO.Compression.ZipFile.Open(zipFile, System.IO.Compression.ZipArchiveMode.Create);
                    archive.CreateEntryFromFile(_logFile, "latest.log");

                    File.Delete(_logFile);
                    File.Create(_logFile).Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error preparing logs: {ex.Message}");
            }
        }

        public void Log(string message) => WriteLog("INFO", message);
        public void LogError(string message) => WriteLog("ERROR", message);
        public void LogWarning(string message) => WriteLog("WARNING", message);
        public void LogSecurity(string message) => WriteLog("SECURITY", message);

        private void WriteLog(string level, string message)
        {
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            Console.WriteLine(logEntry);
            lock (_logLock)
            {
                File.AppendAllTextAsync(_logFile, logEntry + Environment.NewLine);
            }
        }
    }
}