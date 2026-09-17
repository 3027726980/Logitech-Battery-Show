using System;
using System.Threading;
using System.Windows.Forms;

namespace GPW2BatteryShow
{
    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main(string[] args)
        {
            // 单实例互斥（重复启动只保留第一个）
            bool created;
            _mutex = new Mutex(true, "Local\\GPW2BatteryShow", out created);
            if (!created)
            {
                return;
            }

            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Logger.HookUnhandled(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Logger.HookUnhandled(e.ExceptionObject as Exception);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new TrayContext(AppSettings.Load()));
            }
            catch (Exception ex)
            {
                Logger.HookUnhandled(ex);
            }
            finally
            {
                GC.KeepAlive(_mutex);
            }
        }
    }
}
