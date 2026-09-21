using System;
using System.IO;

namespace BluetoothSafetyLock
{
    /// <summary>
    /// Minimal, dependency-free structured logger writing to
    /// %APPDATA%\BluetoothSafetyLock\logs\log-yyyy-MM-dd.log with 7-day retention.
    /// Logging must never crash the app or leak data: only messages and exceptions are written.
    /// </summary>
    public static class Logger
    {
        private static readonly object _ioLock = new();
        private static string _logDir = string.Empty;
        private static bool _initialized;

        public static void Initialize()
        {
            try
            {
                _logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BluetoothSafetyLock", "logs");
                Directory.CreateDirectory(_logDir);
                CleanupOldLogs(7);
                _initialized = true;
            }
            catch
            {
                // If logging cannot initialize, the app must still run.
                _initialized = false;
            }
        }

        public static void Info(string message) => Write("INFO", message, null);
        public static void Warn(string message) => Write("WARN", message, null);
        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception? ex)
        {
            if (!_initialized || string.IsNullOrEmpty(_logDir)) return;
            try
            {
                lock (_ioLock)
                {
                    var file = Path.Combine(_logDir, $"log-{DateTime.Now:yyyy-MM-dd}.log");
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    if (ex != null) line += Environment.NewLine + ex;
                    File.AppendAllText(file, line + Environment.NewLine);
                }
            }
            catch
            {
                // Swallowing here is safe by design: a logging failure must never take the app down.
            }
        }

        private static void CleanupOldLogs(int keepDays)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (var f in Directory.GetFiles(_logDir, "log-*.log"))
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
                }
            }
            catch
            {
                // Non-critical housekeeping.
            }
        }
    }
}
