using System.Collections.Concurrent;
using System.Diagnostics;

namespace EnergyStarZ.Utilities
{
    public static class AppLogger
    {
        private const long MaxLogFileBytes = 1024 * 1024; // 单个日志文件达到 1MB 即轮转
        private const int MaxQueuedLines = 4096;          // 队列积压上限，超出后丢弃新日志
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

        private static readonly string LogFilePath;
        private static readonly BlockingCollection<string> LogQueue = new(MaxQueuedLines);
        private static readonly Thread WriterThread;

        private static volatile bool _enabled = true;

        static AppLogger()
        {
            LogFilePath = Path.Combine(AppContext.BaseDirectory, "energy.log");

            // 单一后台写线程：把多次写盘合并为一次，避免钩子线程被文件 IO 阻塞
            WriterThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "AppLoggerWriter"
            };
            WriterThread.Start();
        }

        /// <summary>
        /// 是否输出日志，对应 appsettings.json 中的 EnableLogging
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
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

        /// <summary>
        /// 等待积压日志落盘（进程退出或崩溃前调用，有界等待避免卡死）
        /// </summary>
        public static void Flush()
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (LogQueue.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }

        private static void WriteLog(string level, string message)
        {
            if (!_enabled)
                return;

            // 队列满时丢弃，保证任何调用方（尤其是 WinEvent 钩子线程）都不会被阻塞
            LogQueue.TryAdd($"[{DateTime.UtcNow:O}] [{level}] {message}");
        }

        private static void WriterLoop()
        {
            var pending = new List<string>(64);

            while (true)
            {
                try
                {
                    if (LogQueue.TryTake(out var line, FlushInterval))
                    {
                        pending.Add(line);

                        // 尽量一次把当前积压写完，进一步减少写盘次数
                        while (pending.Count < 256 && LogQueue.TryTake(out line))
                        {
                            pending.Add(line);
                        }

                        WritePending(pending);
                    }
                }
                catch
                {
                    // 写日志本身失败时丢弃当前批次，绝不影响写线程存活
                    pending.Clear();
                    Thread.Sleep(100);
                }
            }
        }

        private static void WritePending(List<string> pending)
        {
            if (pending.Count == 0)
                return;

            try
            {
                RotateIfNeeded();
                File.AppendAllLines(LogFilePath, pending);
            }
            catch
            {
                // 如果日志写入失败，静默忽略，防止影响主程序
            }
            finally
            {
                pending.Clear();
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(LogFilePath);
                if (!info.Exists || info.Length < MaxLogFileBytes)
                    return;

                var backupPath = LogFilePath + ".1";
                File.Copy(LogFilePath, backupPath, overwrite: true);
                File.Delete(LogFilePath);
            }
            catch
            {
                // 轮转失败时继续写当前文件
            }
        }
    }
}