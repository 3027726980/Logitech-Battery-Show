using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 托盘图标动态绘制（GDI+，32×32）。
    /// 数值模式：大号百分比数字占满画布（可读性优先），充电时青色；
    /// 简约模式：电池轮廓 + 填充；离线：灰色问号。
    /// </summary>
    internal static class TrayIconRenderer
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly Color OkColor = Color.FromArgb(76, 175, 80);        // 绿
        private static readonly Color LowColor = Color.FromArgb(255, 193, 7);       // 黄
        private static readonly Color CriticalColor = Color.FromArgb(244, 67, 54);  // 红
        private static readonly Color ChargingColor = Color.FromArgb(0, 200, 255);  // 青
        private static readonly Color OfflineColor = Color.FromArgb(158, 158, 158); // 灰
        private static readonly Color LightForeground = Color.FromArgb(230, 230, 230); // 深色任务栏前景
        private static readonly Color DarkForeground = Color.FromArgb(60, 60, 60);     // 浅色任务栏前景

        private const int LowThreshold = 20;
        private const int CriticalThreshold = 10;

        /// <summary>读注册表 AppsUseLightTheme；缺失/异常按 Win11 默认深色处理。</summary>
        public static bool IsDarkTaskbar()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object value = key.GetValue("AppsUseLightTheme");
                        if (value is int)
                        {
                            return ((int)value) == 0;   // 1=浅色主题
                        }
                    }
                }
            }
            catch
            {
            }
            return true;
        }

        public static Color PickColor(int? percent, bool charging, bool online)
        {
            if (!online || !percent.HasValue)
            {
                return OfflineColor;
            }
            if (charging)
            {
                return ChargingColor;
            }
            if (percent.Value <= CriticalThreshold)
            {
                return CriticalColor;
            }
            if (percent.Value <= LowThreshold)
            {
                return LowColor;
            }
            return OkColor;
        }

        /// <summary>状态 → 32×32 托盘图标。调用方负责 Dispose 返回的 Icon（替换前先释放旧的）。</summary>
        public static Icon DrawIcon(int? percent, bool charging, bool online,
                                    string style, bool darkTaskbar)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    Color foreground = darkTaskbar ? LightForeground : DarkForeground;
                    Color accent = PickColor(percent, charging, online);

                    if (!online || !percent.HasValue)
                    {
                        DrawText(g, "?", 24, OfflineColor);
                    }
                    else if (style == "simple")
                    {
                        DrawBatteryShell(g, foreground);
                        if (charging)
                        {
                            DrawBolt(g, accent);
                        }
                        int innerWidth = (int)Math.Round(19.0 * Math.Min(percent.Value, 100) / 100.0);
                        using (Brush fill = new SolidBrush(accent))
                        {
                            g.FillRectangle(fill, 6, 13, innerWidth, 9);
                        }
                    }
                    else // numeric：大号数字占满画布
                    {
                        DrawText(g, percent.Value.ToString(), 26, accent);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return Icon.FromHandle(handle).Clone() as Icon;   // 独立副本，原句柄销毁
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static void DrawText(Graphics g, string text, int fontSize, Color color)
        {
            using (Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                SizeF size = g.MeasureString(text, font);
                using (Brush brush = new SolidBrush(color))
                {
                    g.DrawString(text, font, brush, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
                }
            }
        }

        /// <summary>电池轮廓：主体 (3,10)-(26,24) + 右端正极凸起（简约模式用）。</summary>
        private static void DrawBatteryShell(Graphics g, Color foreground)
        {
            using (Pen pen = new Pen(foreground, 2f))
            {
                g.DrawPath(pen, RoundedRect(3, 10, 23, 14, 3));
            }
            using (Brush brush = new SolidBrush(foreground))
            {
                g.FillRectangle(brush, 27, 14, 3, 7);
            }
        }

        private static GraphicsPath RoundedRect(int x, int y, int w, int h, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>充电闪电（简约模式画在电池上方）。</summary>
        private static void DrawBolt(Graphics g, Color color)
        {
            using (Brush brush = new SolidBrush(color))
            {
                var points = new[]
                {
                    new Point(15, 1), new Point(10, 9), new Point(14, 9),
                    new Point(12, 15), new Point(19, 7), new Point(15, 7), new Point(17, 1)
                };
                g.FillPolygon(brush, points);
            }
        }
    }
}
