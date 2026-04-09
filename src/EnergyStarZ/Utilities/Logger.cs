using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace EnergyStarZ.Utilities
{
    public static class AppLogger
    {
        private static readonly string LogFilePath;
        private static readonly object _logLock = new();

        static AppLogger()
        {
            var appDir = AppContext.BaseDirectory;
            LogFilePath = Path.Combine(appDir, "energy.log");
        }

        public static void Info(string message)
        {
            WriteLog("INFO", message);
        }

        public static void Warn(string message)
        {
            WriteLog("WARN", message);
        }

        public static void Error(string message)
        {
            WriteLog("ERROR", message);
        }

        private static void WriteLog(string level, string message)
        {
            lock (_logLock)
            {
                try
                {
                    var logLine = $"[{DateTime.UtcNow:O}] [{level}] {message}";
                    File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
                }
                catch
                {
                    // 如果日志写入失败，静默忽略，防止影响主程序
                }
            }
        }
    }
}