using System;
using System.Drawing;
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
        private readonly ProgressBar _bar;
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

            _bar = new ProgressBar
            {
                Location = new Point(140, 56),
                Size = new Size(106, 20),
                Minimum = 0,
                Maximum = 100
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
                _bar.Value = 0;
                return;
            }
            int percent = reading.Percent ?? 0;
            _percentLabel.Text = percent + "%";
            _percentLabel.ForeColor = reading.Charging
                ? Color.FromArgb(0, 150, 199)
                : TrayIconRenderer.PickColor(percent, false, true);
            string source = string.IsNullOrEmpty(reading.Source) ? "" : reading.Source + " · ";
            _statusLabel.Text = source + (reading.Charging ? "充电中" : "使用中");
            _bar.Value = Math.Min(percent, 100);
        }
    }
}
