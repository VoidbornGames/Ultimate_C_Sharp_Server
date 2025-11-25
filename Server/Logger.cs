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

        // static lock to make the PrepareLogs method thread-safe across all instances.
        private static readonly object _prepareLock = new object();

        public Logger(string logsFolder = "logs")
        {
            _logsFolder = logsFolder;
            _logFile = Path.Combine(_logsFolder, "latest.log");
        }

        public void PrepareLogs()
        {
            // Lock the entire preparation process to prevent race conditions.
            lock (_prepareLock)
            {
                try
                {
                    if (!Directory.Exists(_logsFolder)) Directory.CreateDirectory(_logsFolder);

                    // Double-check inside the lock in case another thread just finished.
                    if (File.Exists(_logFile))
                    {
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        string zipFile = Path.Combine(_logsFolder, $"latest_{timestamp}.zip");

                        // This block is now protected from being run by multiple threads at once.
                        using (var archive = ZipFile.Open(zipFile, ZipArchiveMode.Create))
                        {
                            archive.CreateEntryFromFile(_logFile, "latest.log");
                        }

                        File.Delete(_logFile);

                        // A cleaner way to create an empty file.
                        File.WriteAllText(_logFile, string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error preparing logs: {ex.Message}");
                }
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

            // The lock ensures only one thread writes to the file at a time.
            lock (_logLock)
            {
                // Use the synchronous AppendAllText. The async version was "fire-and-forget"
                // and unsafe. Since we are already in a lock, the sync version is appropriate
                // and guarantees the write completes before the lock is released.
                File.AppendAllText(_logFile, logEntry + Environment.NewLine);
            }
        }
    }
}