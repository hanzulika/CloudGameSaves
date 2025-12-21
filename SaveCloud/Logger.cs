using System;
using System.IO;

namespace SaveCloud
{
    internal static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;

        public static string LogFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_logFilePath))
                {
                    string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                    Directory.CreateDirectory(logDir);
                    _logFilePath = Path.Combine(logDir, $"SaveCloud_{DateTime.Now:yyyy-MM-dd}.log");
                }
                return _logFilePath;
            }
        }

        public static void Log(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] {message}";

            lock (_lock)
            {
                File.AppendAllText(LogFilePath, logEntry + Environment.NewLine);
            }

            Console.WriteLine(logEntry);
        }

        public static bool LogConnection(bool connected)
        {
            Log("Connecting...");
            if (!connected)
            {
                LogError("Server unreachable");
                return false;
            }
            LogSuccess("Connected to server");
            return true;
        }

        public static void LogSaves(bool success, Cloud cloud)
        {
            Log("Reading saves...");
            if (!success || cloud.FileNames.Count == 0)
            {
                LogWarning("No saves found");
            }
            else
            {
                foreach (var fileName in cloud.FileNames)
                {
                    LogInfo($"Found: {fileName}");
                }
            }

            if (cloud.Paths.Count > 0)
                LogInfo("Paths: " + cloud.Paths[0]);
        }

        public static void LogUpload(UploadResult result)
        {
            Log("--- Upload Complete ---");
            Log($"Success: {result.Success}");
            Log($"Files uploaded: {result.FilesUploaded}");

            if (result.Created.Count > 0)
            {
                LogInfo($"Created ({result.Created.Count}):");
                foreach (var game in result.Created)
                    Log($"  + {game}");
            }

            if (result.Updated.Count > 0)
            {
                LogInfo($"Updated ({result.Updated.Count}):");
                foreach (var game in result.Updated)
                    Log($"  ~ {game}");
            }

            if (result.Skipped.Count > 0)
            {
                LogWarning($"Skipped ({result.Skipped.Count}):");
                foreach (var game in result.Skipped)
                    Log($"  - {game}");
            }

            if (result.Errors.Count > 0)
            {
                LogError($"Errors ({result.Errors.Count}):");
                foreach (var error in result.Errors)
                    Log($"  ! {error}");
            }
        }

        public static void LogGameFound(string gameName, string savePath, int fileCount)
        {
            LogInfo($"Game: {gameName} - Save Folder: {savePath} ({fileCount} files)");
        }

        public static void LogGameNoSaves(string gameName)
        {
            LogWarning($"Game: {gameName} - No save folder found");
        }

        public static void LogInfo(string message) => Log($"[INFO] {message}");
        public static void LogError(string message) => Log($"[ERROR] {message}");
        public static void LogWarning(string message) => Log($"[WARN] {message}");
        public static void LogSuccess(string message) => Log($"[SUCCESS] {message}");
    }
}