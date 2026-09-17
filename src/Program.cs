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
            // 显式安装 WinForms 同步上下文：ApplicationContext 构造早于消息循环，
            // 否则 TaskScheduler.FromCurrentSynchronizationContext 会抛异常
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Forms.WindowsFormsSynchronizationContext());
            try
            {
                AppSettings settings = AppSettings.Load();
                Logger.Initialize(settings.LogRetention);   // 每次运行生成独立日志文件
                Application.Run(new TrayContext(settings));
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
