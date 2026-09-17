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

        private static readonly Color ChargingColor = Color.FromArgb(76, 175, 80);  // 充电中：绿
        private static readonly Color MidColor = Color.FromArgb(255, 193, 7);       // 中电量：黄
        private static readonly Color LowColor = Color.FromArgb(244, 67, 54);       // 低电量：红
        private static readonly Color NormalColor = Color.White;                     // 正常：白
        private static readonly Color OfflineColor = Color.FromArgb(158, 158, 158); // 离线：灰
        private const int MidThreshold = 50;

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

        /// <summary>状态 → 图标主色：充电绿 / 低电量红(≤阈值) / 中电量黄(≤50) / 正常白。</summary>
        public static Color PickColor(int? percent, bool charging, bool online, int lowThreshold)
        {
            if (!online || !percent.HasValue)
            {
                return OfflineColor;
            }
            if (charging)
            {
                return ChargingColor;
            }
            if (percent.Value <= lowThreshold)
            {
                return LowColor;
            }
            if (percent.Value <= MidThreshold)
            {
                return MidColor;
            }
            return NormalColor;
        }

        /// <summary>状态 → 32×32 托盘图标。调用方负责 Dispose 返回的 Icon（替换前先释放旧的）。</summary>
        public static Icon DrawIcon(int? percent, bool charging, bool online,
                                    string style, bool darkTaskbar, int lowThreshold)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    Color accent = PickColor(percent, charging, online, lowThreshold);

                    if (!online || !percent.HasValue)
                    {
                        DrawText(g, "?", 24, OfflineColor);
                    }
                    else if (style == "simple")
                    {
                        // 白色边框 + 放大版电池（1,7)-(28,25)，充电时填充变青色
                        DrawBatteryShell(g);
                        int innerWidth = (int)Math.Round(22.0 * Math.Min(percent.Value, 100) / 100.0);
                        using (Brush fill = new SolidBrush(accent))
                        {
                            g.FillRectangle(fill, 4, 10, innerWidth, 12);
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

        /// <summary>电池轮廓：白色边框放大版，主体 (1,7)-(28,25) + 右端正极凸起。</summary>
        private static void DrawBatteryShell(Graphics g)
        {
            using (Pen pen = new Pen(Color.White, 2f))
            {
                g.DrawPath(pen, RoundedRect(1, 7, 27, 18, 3));
            }
            using (Brush brush = new SolidBrush(Color.White))
            {
                g.FillRectangle(brush, 28, 12, 3, 8);
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
    }
}
