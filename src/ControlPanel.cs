using System;
using System.Drawing;
using System.Windows.Forms;

namespace GPW2BatteryShow
{
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

        public ControlPanel(AppSettings settings, Action onApplied)
        {
            _settings = settings;
            _onApplied = onApplied;

            Text = "GPW2 Battery Show · 控制面板";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 300);
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

            var note = new Label
            {
                Text = "提示：轮询过快会干扰鼠标省电休眠，保持默认 60 秒即可。",
                Font = new Font("Microsoft YaHei UI", 8f),
                ForeColor = Color.FromArgb(120, 123, 128),
                AutoSize = true,
                Location = new Point(labelX, 216)
            };

            var saveButton = new Button { Text = "保存", Location = new Point(288, 254), Size = new Size(84, 32) };
            saveButton.Click += delegate { SaveAndClose(); };

            var cancelButton = new Button { Text = "取消", Location = new Point(378, 254), Size = new Size(84, 32) };
            cancelButton.Click += delegate { Close(); };

            Controls.AddRange(new Control[]
            {
                intervalLabel, _intervalBox, thresholdLabel, _thresholdBox,
                styleLabel, _numericRadio, _simpleRadio,
                _popupOnClickBox, _autoStartBox,
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
            AppSettings.SetAutoStart(_autoStartBox.Checked);
            _settings.PopupOnClick = _popupOnClickBox.Checked;
            _settings.Save();
            if (_onApplied != null)
            {
                _onApplied();
            }
            Close();
        }
    }
}
