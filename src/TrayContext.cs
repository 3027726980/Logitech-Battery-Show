using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using HidSharp;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 托盘应用上下文：常驻图标、定时轮询（后台线程）、左键弹窗、右键菜单、低电量通知。
    /// HID 查询全部在后台线程执行，绝不阻塞托盘 UI（真机教训：同步查询会卡死托盘）。
    /// </summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly Timer _timer;                 // WinForms Timer（UI 线程调度）
        private readonly Gpw2Device _device = new Gpw2Device();
        private readonly AppSettings _settings;

        private BatteryPopup _popup;
        private ControlPanel _panel;
        private Icon _currentIcon;
        private bool _busy;                            // 后台查询去重
        private bool _pendingRefresh;                  // 忙碌期间有热插拔事件请求刷新
        private volatile bool _hotplugSeen;            // 热插拔事件合并标志（USB 枚举时 26ms 内可达 6 连发）
        private bool _notified;                        // 低电量通知防骚扰
        private int? _lastOkPercent;
        private int _failStreak;

        // 拔线宽限期：有线拔出（或接收器链路瞬断）后，鼠标切回无线需数秒链路重建，
        // 期间托盘保持最后在线显示，避免"在线→离线→在线"跳变；
        // 宽限期内若探测到接收器在线则无缝切换为接收器状态。
        // 时长 10s = 热插拔即时重试 + 3s×3 定时重试的窗口（链路重建实测 <3s）；
        // 再长会让真被拿去充电的设备长时间假在线。
        private static readonly TimeSpan OfflineGracePeriod = TimeSpan.FromSeconds(10);
        private BatteryReading _lastOnlineReading;     // 最后一次在线读数（宽限期显示用）
        private BatteryReading _displayReading;        // 当前应显示的读数（宽限期内 = 最后在线读数）
        private bool _offline;                         // 当前离线态（检测离线起始沿）
        private DateTime _offlineSince;                // 连续离线起始时刻
        private readonly TaskScheduler _uiScheduler;

        public TrayContext(AppSettings settings)
        {
            _settings = settings;
            _uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();

            // 热插拔事件驱动：接收器/鼠标插拔立即触发刷新（不再苦等轮询周期）。
            // 事件风暴只置标志：多个事件合并为一次重探测，由 BeginRefresh 消费。
            DeviceList.Local.Changed += delegate
            {
                _hotplugSeen = true;
                Logger.Write("热插拔事件触发");
                Task.Factory.StartNew(delegate { BeginRefresh("热插拔"); },
                    System.Threading.CancellationToken.None,
                    System.Threading.Tasks.TaskCreationOptions.None, _uiScheduler);
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("立即刷新", null, delegate { ManualRefresh(); });
            menu.Items.Add(new ToolStripSeparator());
            var numericItem = new ToolStripMenuItem("数值")
            {
                Checked = _settings.IconStyle == "numeric"
            };
            var simpleItem = new ToolStripMenuItem("默认")
            {
                Checked = _settings.IconStyle == "simple"
            };
            numericItem.Click += delegate
            {
                _settings.IconStyle = "numeric";
                _settings.Save();
                numericItem.Checked = true;
                simpleItem.Checked = false;
                RedrawIcon(LastReading);
            };
            simpleItem.Click += delegate
            {
                _settings.IconStyle = "simple";
                _settings.Save();
                simpleItem.Checked = true;
                numericItem.Checked = false;
                RedrawIcon(LastReading);
            };
            menu.Items.Add(numericItem);
            menu.Items.Add(simpleItem);
            menu.Items.Add(new ToolStripSeparator());
            var autoStartItem = new ToolStripMenuItem("开机自启")
            {
                Checked = AppSettings.GetAutoStart()
            };
            autoStartItem.Click += delegate
            {
                bool enable = !AppSettings.GetAutoStart();
                AppSettings.SetAutoStart(enable);
                autoStartItem.Checked = enable;
            };
            menu.Items.Add(autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("控制面板", null, delegate { ShowControlPanel(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate
            {
                _tray.Visible = false;
                Application.Exit();
            });

            _tray = new NotifyIcon
            {
                Icon = _currentIcon = TrayIconRenderer.DrawIcon(null, false, false, _settings.IconStyle, TrayIconRenderer.IsDarkTaskbar(), _settings.LowBatteryThreshold),
                Text = "GPW2 电量显示",
                ContextMenuStrip = menu,
                Visible = true
            };
            _tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && _settings.PopupOnClick)
                {
                    ShowPopup();
                }
            };

            _timer = new Timer { Interval = 2000 };   // 启动后 2 秒内出首次读数
            _timer.Tick += delegate { BeginRefresh("定时轮询"); };
            _timer.Start();
        }

        private BatteryReading LastReading { get; set; }

        /// <summary>后台查询 → 回 UI 线程更新（查询去重）。</summary>
        /// <summary>手动刷新：强制全量重探测（丢弃旧句柄）+ 即时反馈，语义上区别于定时轮询。</summary>
        private void ManualRefresh()
        {
            Logger.Write("手动刷新触发");
            _device.InvalidateDiscovery();   // 手动刷新=强制全量重探测（清除冷却与黑名单）
            _tray.Text = "正在刷新…";
            BeginRefresh("手动");
        }

        private void BeginRefresh(string source)
        {
            Logger.Write(string.Format(
                "BeginRefresh({0}): busy={1} failStreak={2}", source, _busy, _failStreak));
            if (_busy)
            {
                _pendingRefresh = true;   // 热插拔事件密集时首个事件处理完后立即补跑
                return;
            }
            _busy = true;
            if (_hotplugSeen)
            {
                // 热插拔意味着设备格局可能变化（插线转直连/换口/重新配对）：
                // 丢弃旧句柄并清除冷却与黑名单，本次查询直接全量重探测，
                // 不再带着绑定在旧链路上的句柄空转（真机日志：旧句柄重试浪费 ~6 秒）
                _hotplugSeen = false;
                Logger.Write("热插拔: 丢弃旧句柄，全量重探测");
                _device.InvalidateDiscovery();
            }
            Task.Factory.StartNew(delegate
            {
                return _device.ReadBattery();
            }).ContinueWith(delegate(Task<BatteryReading> task)
            {
                _busy = false;
                BatteryReading reading = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : BatteryReading.Offline(null);
                ApplyReading(reading);
                if (_pendingRefresh)
                {
                    _pendingRefresh = false;
                    BeginRefresh("pending补跑");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ApplyReading(BatteryReading reading)
        {
            LastReading = reading;

            // 宽限期判定：仅影响显示层（display），重试节奏与 failStreak 始终用真实 reading。
            // 从未在线过（启动后无设备）不适用宽限期，直接显示未检测到设备。
            BatteryReading display = reading;
            if (reading.Online)
            {
                _offline = false;
                _lastOnlineReading = reading;
            }
            else
            {
                if (!_offline)
                {
                    _offline = true;
                    _offlineSince = DateTime.Now;
                }
                // 宽限期仅覆盖"有线直连拔线"场景：拔线后鼠标几乎必然切回无线接收器，
                // 值得等待；接收器模式断联（关机/拿远/接收器拔出）无从判断何时回来，
                // 直接显示离线（真实优先，控制面板底部可对照真实状态）。
                if (_lastOnlineReading != null
                    && _lastOnlineReading.Source == "有线直连"
                    && DateTime.Now - _offlineSince < OfflineGracePeriod)
                {
                    // 拔线即视为回到接收器模式：立刻把显示源切为"接收器"（数值保持最后已知，
                    // 充电标志清除——线已拔不可能仍在充电），而不是继续显示"有线直连·充电中"
                    // 造成"一直没变成接收器"的错觉。宽限期内探测到接收器在线数据后无缝
                    // 替换为真实读数；超时仍未收到才显示断开。
                    display = new BatteryReading
                    {
                        Percent = _lastOnlineReading.Percent,
                        Charging = false,
                        Online = true,
                        Source = "接收器"
                    };
                }
            }
            _displayReading = display;

            // 轮询节奏：在线常规间隔；离线 3s 快速重试×3（覆盖鼠标切回无线的物理重连窗口）
            // → 10s×5 → 回落常规间隔；连续失败 3 轮时重置设备句柄强制全量重探测
            // （真机日志：不重置则句柄绑在已拔出的旧接口上，永远无法恢复）
            _failStreak = reading.Online ? 0 : _failStreak + 1;
            if (!reading.Online && _failStreak == 3)
            {
                _device.RequestReprobe();
            }
            int seconds;
            if (reading.Online)
            {
                seconds = _settings.PollIntervalSec;
            }
            else if (_failStreak <= 3)
            {
                seconds = 3;
            }
            else if (_failStreak <= 8)
            {
                seconds = 10;
            }
            else
            {
                seconds = _settings.PollIntervalSec;
            }
            _timer.Interval = seconds * 1000;
            Logger.Write(string.Format("ApplyReading: online={0} percent={1} failStreak={2} 下一轮={3}s",
                reading.Online, reading.Percent.HasValue ? reading.Percent.Value.ToString() : "null",
                _failStreak, seconds));

            // 低电量通知：跌破阈值只提醒一次，回升 阈值+5 后重置
            if (reading.Online && reading.Percent.HasValue)
            {
                int percent = reading.Percent.Value;
                if (!_notified && percent <= _settings.LowBatteryThreshold)
                {
                    _tray.ShowBalloonTip(5000, "GPW2 电量提醒",
                        string.Format("鼠标电量仅剩 {0}%，请及时充电", percent),
                        ToolTipIcon.Warning);
                    _notified = true;
                }
                if (_notified && percent > _settings.LowBatteryThreshold + 5)
                {
                    _notified = false;
                }
                _lastOkPercent = percent;
            }

            RedrawIcon(display);

            if (_popup != null && !_popup.IsDisposed && _popup.Visible)
            {
                _popup.UpdateReading(display);
            }

            // 快速失败（写失败/否定应答/无效应答）：句柄链路已死的强信号，立即重探测补跑，
            // 不等 failStreak 节奏（真机日志：等 failStreak=3 才重探浪费 ~6 秒）。
            // 纯超时不会置此标记，偶发抖动仍走 3s 节奏，不会造成探测风暴。
            if (reading.NeedReprobe)
            {
                _device.RequestReprobe();
                BeginRefresh("快速失败补跑");
            }
        }

        private void RedrawIcon(BatteryReading reading)
        {
            bool dark = TrayIconRenderer.IsDarkTaskbar();
            int? percent = reading != null ? reading.Percent : null;
            bool charging = reading != null && reading.Charging;
            bool online = reading != null && reading.Online;

            Icon newIcon = TrayIconRenderer.DrawIcon(percent, charging, online,
                _settings.IconStyle, dark, _settings.LowBatteryThreshold);
            var old = _currentIcon;
            _tray.Icon = _currentIcon = newIcon;
            if (old != null)
            {
                old.Dispose();   // 防 GDI 句柄泄漏
            }

            if (reading == null || !reading.Online)
            {
                _tray.Text = (reading != null && reading.Percent.HasValue)
                    ? string.Format("GPW2 · {0}%（休眠/离线）", reading.Percent.Value)
                    : "未检测到设备";
            }
            else
            {
                _tray.Text = string.Format("GPW2 · {0}%{1}",
                    reading.Percent ?? 0, reading.Charging ? "（充电中）" : "");
            }
        }

        private void ShowPopup()
        {
            if (_popup == null || _popup.IsDisposed)
            {
                _popup = new BatteryPopup(delegate { BeginRefresh("popup"); });
            }
            if (_displayReading != null)
            {
                _popup.UpdateReading(_displayReading);   // 与托盘图标显示保持一致（含宽限期状态）
            }
            _popup.ShowNearTray();
            BeginRefresh("popup");   // 打开即刷新
        }

        private void ShowControlPanel()
        {
            if (_panel != null && !_panel.IsDisposed)
            {
                _panel.Activate();
                return;
            }
            _panel = new ControlPanel(_settings, delegate
            {
                _failStreak = 0;
                _timer.Interval = 1000;   // 保存后 1 秒内按新配置刷新
                Logger.Cleanup(_settings.LogRetention);   // 立即按新策略清理
                RedrawIcon(LastReading);
            }, GetRealtimeStatus);
            _panel.Show();
        }

        /// <summary>提供控制面板底部的真实状态快照（不含托盘宽限期美化）。</summary>
        private BatteryRealtimeStatus GetRealtimeStatus()
        {
            return new BatteryRealtimeStatus
            {
                Busy = _busy,
                Reading = LastReading
            };
        }
    }
}
