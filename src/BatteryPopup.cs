using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 左键托盘图标弹出的电量详情卡片（无边框、点击外部自动关闭）。
    /// 参考社区实践 device-battery-tray 的弹窗形态，为 GPW2 场景定制。
    /// </summary>
    internal sealed class BatteryPopup : Form
    {
        private readonly Action _refreshCallback;
        private readonly Label _titleLabel;
        private readonly Label _percentLabel;
        private readonly Label _statusLabel;
        private readonly BatteryBar _bar;
        private readonly Panel _card;

        public BatteryPopup(Action refreshCallback)
        {
            _refreshCallback = refreshCallback;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(264, 148);
            BackColor = Color.FromArgb(242, 243, 245);
            KeyPreview = true;

            _card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(18, 14, 18, 12)
            };
            Controls.Add(_card);

            _titleLabel = new Label
            {
                Text = "罗技 G Pro X Superlight 2",
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(32, 33, 36),
                AutoSize = true,
                Location = new Point(18, 12)
            };
            _card.Controls.Add(_titleLabel);

            _percentLabel = new Label
            {
                Text = "--",
                Font = new Font("Segoe UI", 30f, FontStyle.Bold),
                ForeColor = Color.FromArgb(76, 175, 80),
                AutoSize = true,
                Location = new Point(18, 38)
            };
            _card.Controls.Add(_percentLabel);

            _statusLabel = new Label
            {
                Text = "正在读取…",
                Font = new Font("Microsoft YaHei UI", 9.5f),
                ForeColor = Color.FromArgb(95, 99, 104),
                AutoSize = true,
                Location = new Point(18, 100)
            };
            _card.Controls.Add(_statusLabel);

            // 左侧 14px 为闪电预留位（充电时显示小闪电），条体位置固定不跳动
            _bar = new BatteryBar
            {
                Location = new Point(128, 56),
                Size = new Size(118, 20)
            };
            _card.Controls.Add(_bar);

            var hint = new Label
            {
                Text = "点击外部关闭 · 右键托盘打开控制面板",
                Font = new Font("Microsoft YaHei UI", 7.5f),
                ForeColor = Color.FromArgb(160, 163, 168),
                AutoSize = true,
                Location = new Point(18, 124)
            };
            _card.Controls.Add(hint);

            Deactivate += delegate { Close(); };
            Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(222, 224, 227)))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                }
            };
        }

        /// <summary>显示在托盘图标附近（任务栏右下角上方）。</summary>
        public void ShowNearTray()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - Width - 12, work.Bottom - Height - 8);
            Show();
            Activate();
        }

        /// <summary>用最新读数更新卡片（UI 线程调用）。</summary>
        public void UpdateReading(BatteryReading reading)
        {
            if (reading == null || IsDisposed)
            {
                return;
            }
            if (!reading.Online)
            {
                _percentLabel.Text = reading.Percent.HasValue
                    ? reading.Percent.Value + " ?"
                    : "--";
                _percentLabel.ForeColor = Color.FromArgb(158, 158, 158);
                _statusLabel.Text = reading.Percent.HasValue ? "鼠标休眠 / 离线" : "未检测到设备";
                _bar.Charging = false;
                _bar.Value = 0;
                return;
            }
            int percent = reading.Percent ?? 0;
            _percentLabel.Text = percent + "%";
            _percentLabel.ForeColor = Color.FromArgb(32, 33, 36);   // 常规黑色（此前误用深色任务栏配色，白字在白底隐形）
            string source = string.IsNullOrEmpty(reading.Source) ? "" : reading.Source + " · ";
            _statusLabel.Text = source + (reading.Charging ? "充电中" : "使用中");
            _bar.Charging = reading.Charging;
            _bar.Value = Math.Min(percent, 100);
        }
    }

    /// <summary>
    /// 自绘电量条：系统 ProgressBar 的平滑动画无法关闭（非充电也要静态），
    /// 且填充色固定绿色无法变白，故自绘。
    /// 非充电：灰色轨道 + 白色静态填充；充电：绿色填充 + 流光动效 + 左侧小闪电。
    /// </summary>
    internal sealed class BatteryBar : Control
    {
        private const int BoltWidth = 14;          // 左侧闪电预留位
        private const int CornerRadius = 3;        // 小圆角（胶囊形过圆，毛毛要求调小）
        private const int GlowWidth = 18;          // 充电流光块宽度
        private static readonly Color TrackColor = Color.FromArgb(208, 210, 214);   // 轨道：灰
        private static readonly Color ChargingColor = Color.FromArgb(76, 175, 80);  // 充电：绿
        private static readonly Color IdleColor = Color.White;                      // 非充电：白
        private static readonly Color GlowColor = Color.FromArgb(120, 255, 255, 255);

        private readonly Timer _animTimer;   // 充电流光帧驱动（非充电完全停止）
        private float _phase;                // 流光位置 0..1
        private int _value;
        private bool _charging;

        public BatteryBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            TabStop = false;
            _animTimer = new Timer { Interval = 33 };
            _animTimer.Tick += delegate
            {
                _phase += 0.045f;
                if (_phase > 1f)
                {
                    _phase = 0f;
                }
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _animTimer != null)
            {
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>电量 0-100，越界自动收敛。</summary>
        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(0, Math.Min(100, value));
                if (clamped != _value)
                {
                    _value = clamped;
                    UpdateAnimState();
                    Invalidate();
                }
            }
        }

        /// <summary>是否充电（决定填充色、动效与闪电）。变化时必须触发重绘。</summary>
        public bool Charging
        {
            get { return _charging; }
            set
            {
                if (value != _charging)
                {
                    _charging = value;
                    _phase = 0f;
                    UpdateAnimState();
                    Invalidate();
                }
            }
        }

        /// <summary>仅充电且有填充时运行动效，其余情况停止。</summary>
        private void UpdateAnimState()
        {
            bool shouldRun = _charging && _value > 0;
            if (shouldRun != _animTimer.Enabled)
            {
                if (shouldRun)
                {
                    _animTimer.Start();
                }
                else
                {
                    _animTimer.Stop();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 条体：闪电位右侧固定区域
            Rectangle track = new Rectangle(BoltWidth, 0, Width - BoltWidth - 1, Height - 1);
            using (GraphicsPath path = RoundedPath(track, CornerRadius))
            using (var brush = new SolidBrush(TrackColor))
            {
                g.FillPath(brush, path);
            }

            if (_value > 0)
            {
                int fillWidth = Math.Max((int)Math.Round((track.Width - 2) * _value / 100.0), 1);
                Rectangle fill = new Rectangle(track.X + 1, 1, fillWidth, Height - 3);
                using (GraphicsPath fillPath = RoundedPath(fill, CornerRadius))
                using (var brush = new SolidBrush(_charging ? ChargingColor : IdleColor))
                {
                    g.FillPath(brush, fillPath);
                }

                // 充电流光：半透明白块在填充区内循环扫过
                if (_charging)
                {
                    float span = fill.Width + GlowWidth;
                    float glowX = fill.Left - GlowWidth + span * _phase;
                    using (GraphicsPath clipPath = RoundedPath(fill, CornerRadius))
                    {
                        g.SetClip(clipPath);
                        using (var brush = new SolidBrush(GlowColor))
                        {
                            g.FillRectangle(brush, glowX, 2, GlowWidth, Height - 4);
                        }
                        g.ResetClip();
                    }
                }
            }

            if (_charging)
            {
                DrawBolt(g);
            }
        }

        /// <summary>绿色小闪电，居中于左侧预留位。</summary>
        private void DrawBolt(Graphics g)
        {
            int w = 10, h = 16;
            int x0 = (BoltWidth - w) / 2;
            int y0 = (Height - h) / 2;
            Point[] pts =
            {
                new Point(x0 + 6, y0), new Point(x0 + 1, y0 + 9), new Point(x0 + 5, y0 + 9),
                new Point(x0 + 3, y0 + h), new Point(x0 + 10, y0 + 6), new Point(x0 + 5, y0 + 6),
                new Point(x0 + 8, y0)
            };
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddPolygon(pts);
                using (var brush = new SolidBrush(ChargingColor))
                {
                    g.FillPath(brush, path);
                }
            }
        }

        /// <summary>圆角矩形，半径自动收敛到短边避免变形。</summary>
        private static GraphicsPath RoundedPath(Rectangle r, int radius)
        {
            int d = Math.Max(Math.Min(radius * 2, Math.Min(r.Width, r.Height)), 1);
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
