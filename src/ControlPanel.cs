using System;
using System.Drawing;
using System.Windows.Forms;

namespace GPW2BatteryShow
{
    /// <summary>控制面板：轮询间隔、低电量阈值、图标样式、开机自启。</summary>
    internal sealed class ControlPanel : Form
    {
        private readonly AppSettings _settings;
        private readonly Action _onApplied;

        private readonly NumericUpDown _intervalBox;
        private readonly NumericUpDown _thresholdBox;
        private readonly RadioButton _numericRadio;
        private readonly RadioButton _simpleRadio;
        private readonly CheckBox _autoStartBox;

        public ControlPanel(AppSettings settings, Action onApplied)
        {
            _settings = settings;
            _onApplied = onApplied;

            Text = "GPW2 Battery Show · 控制面板";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(380, 300);
            Font = new Font("Microsoft YaHei UI", 9.5f);

            var intervalLabel = new Label { Text = "轮询间隔（秒）", AutoSize = true, Location = new Point(20, 22) };
            _intervalBox = new NumericUpDown { Location = new Point(230, 18), Size = new Size(110, 26), Minimum = 10, Maximum = 600, Value = settings.PollIntervalSec };

            var thresholdLabel = new Label { Text = "低电量提醒阈值（%）", AutoSize = true, Location = new Point(20, 60) };
            _thresholdBox = new NumericUpDown { Location = new Point(230, 56), Size = new Size(110, 26), Minimum = 5, Maximum = 90, Value = settings.LowBatteryThreshold };

            var styleLabel = new Label { Text = "托盘图标样式", AutoSize = true, Location = new Point(20, 98) };
            _numericRadio = new RadioButton { Text = "数值（推荐）", AutoSize = true, Location = new Point(230, 96), Checked = settings.IconStyle == "numeric" };
            _simpleRadio = new RadioButton { Text = "简约", AutoSize = true, Location = new Point(230, 122), Checked = settings.IconStyle == "simple" };

            _autoStartBox = new CheckBox
            {
                Text = "开机自动运行（当前登录用户）",
                AutoSize = true,
                Location = new Point(20, 156),
                Checked = AppSettings.GetAutoStart()
            };

            var note = new Label
            {
                Text = "提示：轮询过快会干扰鼠标省电休眠，保持默认 60 秒即可。",
                Font = new Font("Microsoft YaHei UI", 8f),
                ForeColor = Color.FromArgb(120, 123, 128),
                AutoSize = true,
                Location = new Point(20, 192)
            };

            var saveButton = new Button { Text = "保存", Location = new Point(196, 224), Size = new Size(72, 30) };
            saveButton.Click += delegate { SaveAndClose(); };

            var cancelButton = new Button { Text = "取消", Location = new Point(274, 224), Size = new Size(72, 30) };
            cancelButton.Click += delegate { Close(); };

            Controls.AddRange(new Control[]
            {
                intervalLabel, _intervalBox, thresholdLabel, _thresholdBox,
                styleLabel, _numericRadio, _simpleRadio, _autoStartBox,
                note, saveButton, cancelButton
            });

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private void SaveAndClose()
        {
            _settings.PollIntervalSec = (int)_intervalBox.Value;
            _settings.LowBatteryThreshold = (int)_thresholdBox.Value;
            _settings.IconStyle = _numericRadio.Checked ? "numeric" : "simple";
            _settings.Save();
            AppSettings.SetAutoStart(_autoStartBox.Checked);
            if (_onApplied != null)
            {
                _onApplied();
            }
            Close();
        }
    }
}
