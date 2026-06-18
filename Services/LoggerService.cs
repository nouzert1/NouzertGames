using System;
using System.IO;
using System.Text;

namespace NouzertGames.Services
{
    /// <summary>
    /// Service responsible for managing application logging to a dynamic 'app.log' file.
    /// Stores logs in AppData so the executable folder stays clean.
    /// Provides thread-safe logging with timestamps and log levels.
    /// </summary>
    public class LoggerService
    {
        private readonly string _logFilePath;
        private readonly object _lockObject = new();
        private const int MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5MB

        /// <summary>
        /// Initializes a new instance of the LoggerService.
        /// Logs are stored in the .system hidden folder.
        /// </summary>
        /// <param name="logFileName">Name of the log file (default: app.log)</param>
        public LoggerService(string logFileName = "app.log")
        {
            var systemFolder = GetSystemFolder();
            _logFilePath = Path.Combine(systemFolder, logFileName);
        }

        /// <summary>
        /// Gets or creates the .system hidden folder.
        /// </summary>
        private static string GetSystemFolder()
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NouzertGames");
            var systemFolder = Path.Combine(baseDir, ".system");

            if (!Directory.Exists(systemFolder))
            {
                var di = Directory.CreateDirectory(systemFolder);
                di.Attributes |= FileAttributes.Hidden;
            }
            else
            {
                // Ensure the folder is hidden
                var di = new DirectoryInfo(systemFolder);
                di.Attributes |= FileAttributes.Hidden;
            }

            return systemFolder;
        }

        /// <summary>
        /// Logs an informational message.
        /// </summary>
        public void Info(string message) => Log("INFO", message);

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public void Warning(string message) => Log("WARN", message);

        /// <summary>
        /// Logs an error message.
        /// </summary>
        public void Error(string message) => Log("ERROR", message);

        /// <summary>
        /// Logs an error message with exception details.
        /// </summary>
        public void Error(string message, Exception ex)
        {
            var fullMessage = $"{message}{Environment.NewLine}Exception: {ex.GetType().Name}{Environment.NewLine}Message: {ex.Message}{Environment.NewLine}StackTrace: {ex.StackTrace}";
            Log("ERROR", fullMessage);
        }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        public void Debug(string message) => Log("DEBUG", message);

        private void Log(string level, string message)
        {
            lock (_lockObject)
            {
                try
                {
                    // Check if log file is too large and rotate if needed
                    if (File.Exists(_logFilePath))
                    {
                        var fileInfo = new FileInfo(_logFilePath);
                        if (fileInfo.Length > MaxLogFileSizeBytes)
                        {
                            RotateLogFile();
                        }
                    }

                    var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {RedactSensitiveValues(message)}";
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    // Prevent infinite recursion by not logging to file in case of logging failure
                    System.Diagnostics.Debug.WriteLine($"Failed to write to log file: {ex.Message}");
                }
            }
        }

        private static string RedactSensitiveValues(string message)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                message,
                @"(?i)(auth_code=)[^&\s]+",
                "$1***");
        }

        private void RotateLogFile()
        {
            try
            {
                var archivePath = $"{_logFilePath}.{DateTime.Now:yyyyMMdd_HHmmss}.old";
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
                File.Move(_logFilePath, archivePath);
                Info("Log file rotated");
            }
            catch (Exception ex)
            {
                Debug($"Failed to rotate log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the path to the current log file.
        /// </summary>
        public string GetLogFilePath() => _logFilePath;

        /// <summary>
        /// Clears the log file.
        /// </summary>
        public void Clear()
        {
            lock (_lockObject)
            {
                if (File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }
            }
        }

        /// <summary>
        /// Gets the current log content as a string.
        /// </summary>
        public string GetLogContent()
        {
            lock (_lockObject)
            {
                return File.Exists(_logFilePath) 
                    ? File.ReadAllText(_logFilePath, Encoding.UTF8) 
                    : string.Empty;
            }
        }
    }
}
