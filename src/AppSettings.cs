using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using System.Web.Script.Serialization;

namespace GPW2BatteryShow
{
    /// <summary>应用配置（JSON 持久化）+ 开机自启（HKCU Run 注册表键）。</summary>
    internal sealed class AppSettings
    {
        public int PollIntervalSec = 60;          // 轮询间隔（过小会干扰鼠标省电休眠）
        public int LowBatteryThreshold = 20;      // 低电量通知阈值（%）
        public string IconStyle = "numeric";      // numeric | simple
        public bool PopupOnClick = true;          // 左键单击是否展开电量卡片
        public string LogRetention = "7days";     // session | 7days | 30days | all

        /// <summary>配置目录：跟随 exe 所在目录（便携式风格；日志则在同级的 logs 子目录）。</summary>
        private static string ConfigDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'); }
        }

        private static string ConfigPath
        {
            get { return Path.Combine(ConfigDirectory, "config.json"); }
        }

        public static AppSettings Load()
        {
            try
            {
                string path = ConfigPath;
                if (File.Exists(path))
                {
                    var serializer = new JavaScriptSerializer();
                    var raw = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    var settings = new AppSettings();
                    if (raw != null)
                    {
                        object value;
                        if (raw.TryGetValue("poll_interval_sec", out value))
                        {
                            settings.PollIntervalSec = Convert.ToInt32(value);
                        }
                        if (raw.TryGetValue("low_battery_threshold", out value))
                        {
                            settings.LowBatteryThreshold = Convert.ToInt32(value);
                        }
                        if (raw.TryGetValue("icon_style", out value))
                        {
                            settings.IconStyle = Convert.ToString(value);
                        }
                        if (raw.TryGetValue("popup_on_click", out value))
                        {
                            settings.PopupOnClick = Convert.ToBoolean(value);
                        }
                        if (raw.TryGetValue("log_retention", out value))
                        {
                            settings.LogRetention = Convert.ToString(value);
                        }
                    }
                    if (settings.PollIntervalSec < 10) settings.PollIntervalSec = 10;
                    if (settings.LowBatteryThreshold < 5) settings.LowBatteryThreshold = 5;
                    if (settings.LowBatteryThreshold > 90) settings.LowBatteryThreshold = 90;
                    if (settings.IconStyle != "numeric" && settings.IconStyle != "simple")
                    {
                        settings.IconStyle = "numeric";   // combo 样式已移除，存量配置回退
                    }
                    if (settings.LogRetention != "session" && settings.LogRetention != "7days"
                        && settings.LogRetention != "30days" && settings.LogRetention != "all")
                    {
                        settings.LogRetention = "7days";
                    }
                    return settings;
                }
            }
            catch (Exception ex)
            {
                Logger.Write("配置读取失败，使用默认值: " + ex.Message);
                TryBackupCorruptedConfig();
            }
            var defaults = new AppSettings();
            defaults.Save();
            return defaults;
        }

        private static void TryBackupCorruptedConfig()
        {
            try
            {
                string path = ConfigPath;
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDirectory);
                var serializer = new JavaScriptSerializer();
                var raw = new Dictionary<string, object>
                {
                    { "poll_interval_sec", PollIntervalSec },
                    { "low_battery_threshold", LowBatteryThreshold },
                    { "icon_style", IconStyle },
                    { "popup_on_click", PopupOnClick },
                    { "log_retention", LogRetention }
                };
                File.WriteAllText(ConfigPath, serializer.Serialize(raw));
            }
            catch (Exception ex)
            {
                Logger.Write("配置保存失败: " + ex.Message);
            }
        }

        // ---- 开机自启（HKCU\...\Run）----

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "GPW2BatteryShow";

        private static string ExeCommand
        {
            get
            {
                return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"";
            }
        }

        public static bool GetAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    return key.GetValue(AppName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    if (enable)
                    {
                        key.SetValue(AppName, ExeCommand);
                    }
                    else if (key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write("设置开机自启失败: " + ex.Message);
                return false;
            }
        }
    }
}
