using System;
using System.Drawing;
using System.Windows.Forms;

namespace GPW2BatteryShow
{
    /// <summary>控制面板实时状态快照（由 TrayContext 提供，真实状态、不含托盘宽限期美化）。</summary>
    internal sealed class BatteryRealtimeStatus
    {
        public bool Busy;              // 后台正在查询/重探测
        public BatteryReading Reading; // 最后一次真实读数（null=尚未检测过）
    }

    /// <summary>控制面板：轮询间隔、低电量阈值、图标样式、开机自启、左键行为。</summary>
    internal sealed class ControlPanel : Form
    {
        private readonly AppSettings _settings;
        private readonly Action _onApplied;

        private readonly NumericUpDown _intervalBox;
        private readonly NumericUpDown _thresholdBox;
        private readonly RadioButton _numericRadio;
        private readonly RadioButton _simpleRadio;
        private readonly CheckBox _autoStartBox;
        private readonly CheckBox _popupOnClickBox;
        private readonly ComboBox _logCombo;
        private readonly Func<BatteryRealtimeStatus> _statusProvider;
        private readonly Label _statusDot;
        private readonly Label _statusText;
        private readonly Timer _statusTimer;
        private int _spinnerFrame;

        private static readonly string[] SpinnerFrames = new string[] { "◐", "◓", "◑", "◒" };

        public ControlPanel(AppSettings settings, Action onApplied, Func<BatteryRealtimeStatus> statusProvider)
        {
            _settings = settings;
            _onApplied = onApplied;
            _statusProvider = statusProvider;

            Text = "GPW2 Battery Show · 控制面板";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 372);
            Font = new Font("Microsoft YaHei UI", 9.5f);

            int labelX = 22;
            int inputX = 265;

            var intervalLabel = new Label { Text = "轮询间隔（秒）", AutoSize = true, Location = new Point(labelX, 24) };
            _intervalBox = new NumericUpDown { Location = new Point(inputX, 20), Size = new Size(120, 28), Minimum = 10, Maximum = 600, Value = settings.PollIntervalSec };

            var thresholdLabel = new Label { Text = "低电量提醒阈值（%）", AutoSize = true, Location = new Point(labelX, 66) };
            _thresholdBox = new NumericUpDown { Location = new Point(inputX, 62), Size = new Size(120, 28), Minimum = 5, Maximum = 90, Value = settings.LowBatteryThreshold };

            var styleLabel = new Label { Text = "托盘图标样式", AutoSize = true, Location = new Point(labelX, 108) };
            _numericRadio = new RadioButton { Text = "数值", AutoSize = true, Location = new Point(inputX, 106), Checked = settings.IconStyle == "numeric" };
            _simpleRadio = new RadioButton { Text = "默认", AutoSize = true, Location = new Point(inputX + 62, 106), Checked = settings.IconStyle == "simple" };

            _popupOnClickBox = new CheckBox
            {
                Text = "左键单击托盘图标时展开电量卡片",
                AutoSize = true,
                Location = new Point(labelX, 148),
                Checked = settings.PopupOnClick
            };

            _autoStartBox = new CheckBox
            {
                Text = "开机自动运行（当前登录用户）",
                AutoSize = true,
                Location = new Point(labelX, 182),
                Checked = AppSettings.GetAutoStart()
            };

            var logLabel = new Label { Text = "日志清理策略", AutoSize = true, Location = new Point(labelX, 222) };            _logCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(inputX, 218),
                Size = new Size(180, 28)
            };
            _logCombo.Items.AddRange(new object[]
            {
                "仅保留本次运行", "保留最近 7 天", "保留最近 30 天", "全部保留"
            });
            _logCombo.SelectedIndex = settings.LogRetention == "session" ? 0
                : settings.LogRetention == "30days" ? 2
                : settings.LogRetention == "all" ? 3 : 1;

            var note = new Label
            {
                Text = "提示：轮询过快会干扰鼠标省电休眠，保持默认 60 秒即可。日志位于程序目录 logs 文件夹。",
                Font = new Font("Microsoft YaHei UI", 8f),
                ForeColor = Color.FromArgb(120, 123, 128),
                AutoSize = true,
                Location = new Point(labelX, 258)
            };

            var saveButton = new Button { Text = "保存", Location = new Point(288, 296), Size = new Size(84, 32) };
            saveButton.Click += delegate { SaveAndClose(); };

            var cancelButton = new Button { Text = "取消", Location = new Point(378, 296), Size = new Size(84, 32) };
            cancelButton.Click += delegate { Close(); };

            // 底部真实状态行：检测中转圈 / 在线绿点 / 离线灰点。
            // 显示的是真实读数（不含托盘的拔线宽限期美化），方便对照真实情况
            _statusDot = new Label
            {
                Text = "◐",
                AutoSize = true,
                Location = new Point(labelX, 338),
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(120, 123, 128)
            };
            _statusText = new Label
            {
                Text = "检测中…",
                AutoSize = true,
                Location = new Point(labelX + 26, 341),
                Font = new Font("Microsoft YaHei UI", 9f),
                ForeColor = Color.FromArgb(120, 123, 128)
            };
            _statusTimer = new Timer { Interval = 300 };
            _statusTimer.Tick += delegate { RefreshStatusLine(); };
            _statusTimer.Start();
            Disposed += delegate
            {
                _statusTimer.Stop();
                _statusTimer.Dispose();   // 防计时器泄漏（面板可反复开关）
            };

            Controls.AddRange(new Control[]
            {
                intervalLabel, _intervalBox, thresholdLabel, _thresholdBox,
                styleLabel, _numericRadio, _simpleRadio,
                _popupOnClickBox, _autoStartBox, logLabel, _logCombo,
                note, saveButton, cancelButton,
                _statusDot, _statusText
            });

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            RefreshStatusLine();   // 打开即刷一次，不等首个 Timer 周期
        }

        /// <summary>刷新底部真实状态行（UI 线程，Timer 300ms 驱动）。</summary>
        private void RefreshStatusLine()
        {
            if (_statusProvider == null || IsDisposed)
            {
                return;
            }
            BatteryRealtimeStatus status = _statusProvider();
            if (status == null)
            {
                return;
            }
            if (status.Busy)
            {
                _statusDot.Text = SpinnerFrames[_spinnerFrame++ % SpinnerFrames.Length];
                _statusDot.ForeColor = Color.FromArgb(33, 150, 243);   // 蓝：检测中
                _statusText.Text = "检测中…";
                _statusText.ForeColor = Color.FromArgb(120, 123, 128);
                return;
            }
            BatteryReading r = status.Reading;
            if (r != null && r.Online)
            {
                _statusDot.Text = "●";
                _statusDot.ForeColor = Color.FromArgb(76, 175, 80);    // 绿：已连接
                _statusText.Text = string.Format("已连接 · {0} · {1}%{2}",
                    string.IsNullOrEmpty(r.Source) ? "设备" : r.Source,
                    r.Percent ?? 0,
                    r.Charging ? " · 充电中" : "");
                _statusText.ForeColor = Color.FromArgb(76, 175, 80);
                return;
            }
            _statusDot.Text = "●";
            _statusDot.ForeColor = Color.FromArgb(158, 158, 158);      // 灰：离线
            _statusText.Text = "未检测到设备（自动重试中）";
            _statusText.ForeColor = Color.FromArgb(120, 123, 128);
        }

        private void SaveAndClose()
        {
            _settings.PollIntervalSec = (int)_intervalBox.Value;
            _settings.LowBatteryThreshold = (int)_thresholdBox.Value;
            _settings.IconStyle = _numericRadio.Checked ? "numeric" : "simple";
            AppSettings.SetAutoStart(_autoStartBox.Checked);
            _settings.PopupOnClick = _popupOnClickBox.Checked;
            _settings.LogRetention = new[]
            {
                "session", "7days", "30days", "all"
            }[_logCombo.SelectedIndex];
            _settings.Save();
            if (_onApplied != null)
            {
                _onApplied();
            }
            Close();
        }
    }
}
