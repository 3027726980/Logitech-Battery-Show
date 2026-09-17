using System;
using System.IO;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 日志：每次运行在 logs/ 子目录下生成独立文件（按启动时间命名，如 20260917_162000.log），
    /// 并支持按保留策略自动清理历史日志。
    /// </summary>
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _logFile;   // 本次运行的日志文件路径

        /// <summary>日志目录：跟随 exe 所在目录（不写入 C 盘用户目录，便携式风格）。</summary>
        public static string LogDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); }
        }

        /// <summary>启动时调用：生成本次运行日志文件并按策略清理历史日志。</summary>
        public static void Initialize(string retention)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    _logFile = Path.Combine(LogDirectory,
                        DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                    Cleanup(retention);
                    Write("日志初始化完成，保留策略=" + retention);
                }
                catch
                {
                    // 日志初始化失败不影响主功能
                }
            }
        }

        /// <summary>按策略清理历史日志（不删除本次运行的文件）。</summary>
        /// 策略：session=仅保留本次运行；7days/30days=按修改时间保留；all=全部保留。
        public static void Cleanup(string retention)
        {
            try
            {
                if (retention == "all")
                {
                    RemoveLegacyFiles();
                    return;
                }
                int keepDays = retention == "30days" ? 30 : 7;
                if (retention == "session")
                {
                    keepDays = 0;
                }
                DateTime cutoff = DateTime.Now.AddDays(-keepDays);
                if (Directory.Exists(LogDirectory))
                {
                    foreach (string file in Directory.GetFiles(LogDirectory, "*.log"))
                    {
                        if (file == _logFile)
                        {
                            continue;   // 本次运行的文件不删
                        }
                        if (retention == "session" || File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                }
                RemoveLegacyFiles();   // 旧版本单文件日志遗留（log.txt / log.txt.1）
            }
            catch
            {
                // 清理失败不影响主功能
            }
        }

        /// <summary>删除旧版本在 exe 目录留下的单文件日志。</summary>
        private static void RemoveLegacyFiles()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string old1 = Path.Combine(baseDir, "log.txt");
                string old2 = Path.Combine(baseDir, "log.txt.1");
                if (File.Exists(old1))
                {
                    File.Delete(old1);
                }
                if (File.Exists(old2))
                {
                    File.Delete(old2);
                }
            }
            catch
            {
            }
        }

        /// <summary>写一条日志（Initialize 之前的调用静默忽略）。</summary>
        public static void Write(string message)
        {
            lock (Sync)
            {
                if (_logFile == null)
                {
                    return;
                }
                try
                {
                    File.AppendAllText(_logFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
                catch
                {
                    // 日志失败不影响主功能
                }
            }
        }

        public static void Write(string format, params object[] args)
        {
            Write(string.Format(format, args));
        }

        /// <summary>全局异常兜底：写日志（用户反馈问题时取证）。</summary>
        public static void HookUnhandled(Exception ex)
        {
            if (ex != null)
            {
                Write("Unhandled: " + ex);
            }
        }
    }
}
