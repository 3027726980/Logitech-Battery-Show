using System;
using System.Diagnostics;
using System.IO;

namespace GPW2BatteryShow
{
    /// <summary>简易滚动日志（写到 %LOCALAPPDATA%\GPW2BatteryShow\log.txt）。</summary>
    internal static class Logger
    {
        private static readonly object Sync = new object();

        /// <summary>日志目录：跟随 exe 所在目录（不写入 C 盘用户目录，便携式风格）。</summary>
        public static string LogDirectory
        {
            get
            {
                return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    string path = Path.Combine(LogDirectory, "log.txt");
                    if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        if (info.Length > 1024 * 1024)
                        {
                            string bak = Path.Combine(LogDirectory, "log.txt.1");
                            File.Delete(bak);
                            File.Move(path, bak);
                        }
                    }
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不影响主功能
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
