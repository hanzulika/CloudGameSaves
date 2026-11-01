using System;
using System.IO;

namespace SaveCloud.Core
{
    public static class Logger
    {
        private static readonly object Lock = new object();

        /// <summary>Raised for every log line so the status window can show a live tail.</summary>
        public static event Action<string> LineWritten;

        public static string LogFilePath => Path.Combine(AppPaths.LogDir, $"SaveCloud_{DateTime.Now:yyyy-MM-dd}.log");

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            lock (Lock)
            {
                try { File.AppendAllText(LogFilePath, line + Environment.NewLine); }
                catch { /* logging must never crash the app */ }
            }
            try { LineWritten?.Invoke(line); } catch { }
        }

        /// <summary>Deletes log files older than the given number of days.</summary>
        public static void Prune(int keepDays = 14)
        {
            try
            {
                foreach (var f in new DirectoryInfo(AppPaths.LogDir).GetFiles("SaveCloud_*.log"))
                    if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-keepDays)) f.Delete();
            }
            catch { }
        }
    }
}
