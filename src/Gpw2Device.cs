using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using HidSharp;

namespace GPW2BatteryShow
{
    /// <summary>一次电量读取的结果。</summary>
    internal sealed class BatteryReading
    {
        public int? Percent;      // 最后一次成功读取的电量（null=从未读到）
        public bool Charging;
        public bool Online;
        public string Source;     // “有线直连” / “接收器” / null
        public bool NeedReprobe;  // 快速失败（写失败/否定应答/无效应答）：句柄链路已死的强信号，
                                  // 上层应立即 RequestReprobe 重新发现，而非按失败节奏等待

        public static BatteryReading Offline(int? lastPercent)
        {
            return new BatteryReading { Percent = lastPercent, Charging = false, Online = false };
        }
    }

    /// <summary>单次 HID++ 请求/应答的结局判定（用于区分偶发超时与链路死亡）。</summary>
    internal enum QueryOutcome
    {
        Answer,     // 收到应答（含 0x8F / 0xFF 错误帧——它们是接收器的"否定应答"）
        Timeout,    // deadline 内无应答（可能偶发抖动，也可能设备不在）
        WriteFail   // 全部句柄写入失败（句柄失效，链路已死）
    }

    /// <summary>
    /// GPW2 电量查询设备层（基于 HidSharp）。
    ///
    /// 发现策略（不硬编码 PID，天然兼容其他有电量功能的罗技无线设备）：
    ///   1. 枚举 VID=0x046D 且 usage page 为罗技厂商页（0xFF00 / 0xFF43）的接口
    ///   2. 同一 interface（mi_XX）的多个 collection 归为一组：写走可写句柄，
    ///      读轮询组内全部句柄（真机实测 GPW2 协议 4.2 长报文应答只在 Col02 出现）
    ///   3. 先探测直连形态（device index 0xFF），再逐个探测接收器 slot 1-6
    ///   4. 电量 feature 走 UnifiedBattery 0x1004；探测直接发 GetFeature，
    ///      应答本身等价于在线判定（省掉 ping 的一次 ~800ms 设备应答延迟）
    ///   5. 记忆上次成功绑定的组（跨启动持久化 device_state.json）：
    ///      重探测优先只扫该组，命中即 1 次请求完成绑定，其余组完全不扫
    ///   6. 全量扫描时对整组无绑定的组做冷却（跳过 N 轮后复检），对 open 必失败的
    ///      接口路径记入黑名单，消除对已知无效组的重复扫描
    /// </summary>
    internal sealed class Gpw2Device
    {
        private const int LogitechVendorId = 0x046D;
        private const byte DirectIndex = 0xFF;
        private static readonly int[] ReceiverSlots = new int[] { 1, 2, 3, 4, 5, 6 };
        private static readonly int[] UsagePages = new int[] { 0xFF00, 0xFF43 };
        private const int GroupCooldownSkips = 6;   // 组冷却：全量扫描跳过轮数，之后复检一轮

        private readonly object _sync = new object();

        private struct OpenedStream
        {
            public HidStream Stream;
            public int MaxInputLength;
        }

        private List<OpenedStream> _streams;      // 当前绑定接口的全部 collection
        private byte _deviceIndex;
        private byte _featureIndex;
        private ushort _featureId;
        private int? _lastPercent;
        private BatteryReading _probeReading;     // 探测时顺带读到的电量（复用，省一次查询）
        private readonly Dictionary<string, byte> _featureIndexCache =
            new Dictionary<string, byte>();       // 组 key → feature index（重探测免查 GetFeature）

        // ---- 探测记忆（消除重复无效扫描） ----
        private string _lastGoodGroup;            // 上次成功绑定的组键（重探测优先只扫它）
        private byte _lastGoodIndex;              // 上次成功绑定的设备索引
        private readonly Dictionary<string, int> _groupCooldown =
            new Dictionary<string, int>();        // 组 key → 剩余跳过轮数（复检后刷新）
        private readonly HashSet<string> _openBlockedPaths =
            new HashSet<string>();                // open 必失败的接口路径（本次运行内跳过）

        public Gpw2Device()
        {
            // 启动时恢复上次绑定记忆：优先扫记忆组 + feature index 免查（冷启动快路径）
            DeviceState saved = DeviceState.Load();
            if (saved != null)
            {
                _lastGoodGroup = saved.LastGroup;
                _lastGoodIndex = (byte)saved.LastIndex;
                Logger.Write(string.Format(
                    "已恢复设备记忆: 组={0} devIdx=0x{1:X2}", _lastGoodGroup, _lastGoodIndex));
            }
        }

        /// <summary>顶层入口：按需发现设备并读取电量。失败返回 Online=false。</summary>
        public BatteryReading ReadBattery()
        {
            lock (_sync)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (_streams == null)
                    {
                        Logger.Write("ReadBattery: 无绑定句柄，开始重探测");
                        OpenAndDetect();
                        if (_probeReading != null)
                        {
                            // 探测阶段已顺带读到电量，复用省一次 ~805ms 设备应答延迟
                            BatteryReading r = _probeReading;
                            _probeReading = null;
                            Logger.Write(string.Format(
                                "ReadBattery: 复用探测读数 {0}%（探测查询一次到位）", r.Percent));
                            _lastPercent = r.Percent;
                            return r;
                        }
                    }
                    if (_streams == null)
                    {
                        Logger.Write(string.Format(
                            "ReadBattery: 重探测后仍无设备，耗时 {0}ms", sw.ElapsedMilliseconds));
                        return BatteryReading.Offline(null);
                    }
                    _probeReading = null;
                    Logger.Write(string.Format("ReadBattery: 查询 devIdx={0} featureIdx={1}",
                        _deviceIndex, _featureIndex));
                    // 单次查询不内部重试：离线时的快速重试节奏由上层 timer 控制，
                    // 连续失败达到阈值后由上层调用 Reset() 触发全量重探测。
                    // 这样锁的占空比低，手动刷新等并发查询不会被长时间阻塞。
                    byte[] request = LogitechHidpp.BuildBatteryRequest(_deviceIndex, _featureIndex);
                    QueryOutcome outcome;
                    byte[] resp = QueryOn(_streams, request, 1200, out outcome);
                    int percent; bool charging; bool externalPower;
                    if (TryParseAnswer(resp, out percent, out charging, out externalPower))
                    {
                        Logger.Write(string.Format(
                            "ReadBattery: 成功 {0}% charging={1} ext={2} 耗时 {3}ms",
                            percent, charging, externalPower, sw.ElapsedMilliseconds));
                        _lastPercent = percent;
                        return new BatteryReading
                        {
                            Percent = percent,
                            Charging = charging,
                            Online = true,
                            Source = _deviceIndex == DirectIndex ? "有线直连" : "接收器"
                        };
                    }
                    // 失败定性：写失败 / 否定应答（0x8F、0xFF 错误帧）/ 收到无效应答，
                    // 都是链路异常的强信号 → 让上层立即重探测；纯超时可能是偶发抖动
                    // （真机日志：超时后 3 秒重试即恢复），保留句柄按节奏重试即可
                    bool needReprobe = outcome != QueryOutcome.Timeout;
                    Logger.Write(string.Format(
                        "ReadBattery: 查询失败（{0}），耗时 {1}ms{2}",
                        DescribeOutcome(outcome), sw.ElapsedMilliseconds,
                        needReprobe ? "，标记立即重探测" : "，保留句柄按节奏重试"));
                    return new BatteryReading
                    {
                        Percent = _lastPercent, Charging = false, Online = false,
                        NeedReprobe = needReprobe
                    };
                }
                catch (Exception ex)
                {
                    Logger.Write("hidapi IO 异常: " + ex.Message + "，丢弃句柄");
                    CloseStreams();   // 句柄失效（设备拔出等）才丢弃，下次重新探测
                    BatteryReading r = BatteryReading.Offline(_lastPercent);
                    r.NeedReprobe = true;   // 异常=句柄已死，让上层立即重探（立即确认设备是否回来）
                    return r;
                }
            }
        }

        // ---- 内部实现 ----

        /// <summary>
        /// 丢弃当前绑定的句柄（保留已知电量），下次查询将重新探测。
        /// 冷却与 open 黑名单保留——链路抖动不改变设备格局。
        /// </summary>
        public void RequestReprobe()
        {
            lock (_sync)
            {
                CloseStreams();
            }
        }

        /// <summary>
        /// 设备格局可能变化（热插拔/手动刷新）：丢句柄 + 清除组冷却与 open 黑名单，
        /// 下次查询做一次真正意义的全量重探测。
        /// </summary>
        public void InvalidateDiscovery()
        {
            lock (_sync)
            {
                CloseStreams();
                _groupCooldown.Clear();
                _openBlockedPaths.Clear();
            }
        }

        private void CloseStreams()
        {
            if (_streams != null)
            {
                foreach (OpenedStream s in _streams)
                {
                    try { s.Stream.Dispose(); }
                    catch { }
                }
            }
            _streams = null;
            _featureIndex = 0;
            _featureId = 0;
        }

        /// <summary>打开一个接口组的全部 collection；全部失败返回空列表。</summary>
        private List<OpenedStream> OpenGroup(List<HidDevice> group)
        {
            var streams = new List<OpenedStream>();
            foreach (HidDevice device in group)
            {
                OpenedStream opened;
                if (TryOpen(device, out opened))
                {
                    streams.Add(opened);
                    Logger.Write("  打开接口: " + ShortPath(device.DevicePath));
                }
                else
                {
                    _openBlockedPaths.Add(device.DevicePath);   // 本次运行内不再尝试
                    Logger.Write("  接口被占用，跳过: " + ShortPath(device.DevicePath));
                }
            }
            return streams;
        }

        private static void DisposeStreams(List<OpenedStream> streams)
        {
            if (streams == null)
            {
                return;
            }
            foreach (OpenedStream s in streams)
            {
                try { s.Stream.Dispose(); }
                catch { }
            }
        }

        /// <summary>绑定成功：登记句柄/索引/feature，记录 lastGood 记忆并持久化。</summary>
        private void Bind(List<OpenedStream> streams, string groupKey, byte deviceIndex,
            ProbeResult result, string source)
        {
            _streams = streams;
            _deviceIndex = deviceIndex;
            _featureIndex = result.FeatureIndex;
            _featureId = result.FeatureId;
            _probeReading = new BatteryReading
            {
                Percent = result.Percent, Charging = result.Charging,
                Online = true, Source = source
            };
            if (result.HasReading)
            {
                _probeReading = new BatteryReading
                {
                    Percent = result.Percent, Charging = result.Charging,
                    Online = true, Source = source
                };
            }
            // else：在线已确认但电量读取失败（链路重建中）——不设探测读数，
            // ReadBattery 走常规查询路径补读；句柄保留，失败由 failStreak 3s 节奏重试
            _lastGoodGroup = groupKey;
            _lastGoodIndex = deviceIndex;
            _groupCooldown.Remove(groupKey);   // 绑定成功 = 该组有效，清除冷却残留
            DeviceState.Save(groupKey, deviceIndex, _featureIndexCache);
        }

        /// <summary>枚举罗技厂商接口，按 interface 分组，探测直连/接收器形态。</summary>
        private void OpenAndDetect()
        {
            var groups = new Dictionary<string, List<HidDevice>>();
            foreach (HidDevice device in DeviceList.Local.GetHidDevices(LogitechVendorId))
            {
                if (_openBlockedPaths.Contains(device.DevicePath))
                {
                    continue;   // 已确认 open 必失败的路径（如系统独占键盘接口），跳过
                }
                // 全部罗技接口进组探测：标准键盘/鼠标接口写 HID++ 无应答会被自然跳过；
                // 本版 HidSharp.dll 无 GetUsagePage API，不做 usage page 预过滤
                string key = InterfaceKey(device.DevicePath);
                List<HidDevice> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<HidDevice>();
                    groups[key] = list;
                }
                list.Add(device);
            }

            Logger.Write(string.Format("OpenAndDetect: 枚举到 {0} 个接口组", groups.Count));

            // ---- 快路径：上次成功组优先 ----
            // 命中则只开这一个组、只做 1-2 次探测请求；其余组（含已知无效组）完全不扫。
            // 启动冷启动 / 热插拔 / 离线恢复都走这里，探测代价从 14 个 probe 降到 1 个。
            if (_lastGoodGroup != null && groups.ContainsKey(_lastGoodGroup))
            {
                List<HidDevice> group = groups[_lastGoodGroup];
                List<OpenedStream> streams = OpenGroup(group);
                if (streams.Count > 0)
                {
                    byte cached = _featureIndexCache.ContainsKey(_lastGoodGroup)
                        ? _featureIndexCache[_lastGoodGroup] : (byte)0;
                    bool anyUncertain;
                    ProbeResult probed = ProbeDirectOrSlots(streams, _lastGoodGroup, cached, out anyUncertain);
                    if (probed != null)
                    {
                        return;   // Bind 已在 ProbeDirectOrSlots 内完成
                    }
                }
                DisposeStreams(streams);
                Logger.Write("  上次成功组未命中，转入全量扫描: " + _lastGoodGroup);
                _lastGoodGroup = null;   // 失效；本轮若在别处成功会重新记录
            }
            else if (_lastGoodGroup != null)
            {
                Logger.Write("  上次成功组已不在枚举结果中，转入全量扫描: " + _lastGoodGroup);
                _lastGoodGroup = null;
            }

            // ---- 全量扫描（冷却组跳过） ----
            foreach (KeyValuePair<string, List<HidDevice>> groupEntry in groups)
            {
                int skipsLeft;
                if (_groupCooldown.TryGetValue(groupEntry.Key, out skipsLeft) && skipsLeft > 0)
                {
                    _groupCooldown[groupEntry.Key] = skipsLeft - 1;
                    Logger.Write(string.Format("  跳过冷却组 {0}（剩余 {1} 轮后复检）",
                        groupEntry.Key, skipsLeft - 1));
                    continue;
                }

                List<HidDevice> group = groupEntry.Value;
                List<OpenedStream> streams = OpenGroup(group);
                if (streams.Count == 0)
                {
                    _groupCooldown[groupEntry.Key] = GroupCooldownSkips;
                    continue;
                }

                byte cached = _featureIndexCache.ContainsKey(groupEntry.Key)
                    ? _featureIndexCache[groupEntry.Key] : (byte)0;
                bool anyUncertain;
                ProbeResult bound = ProbeDirectOrSlots(streams, groupEntry.Key, cached, out anyUncertain);
                if (bound != null)
                {
                    return;   // Bind 已在 ProbeDirectOrSlots 内完成
                }
                DisposeStreams(streams);
                // 整组（0xFF + slot1-6）无绑定时的冷却决策：
                //   全部为否定应答（接收器立即代答的 0x8F）或写入失败 → 确定性离线，冷却；
                //   存在超时 → 设备可能在但链路重建中（拔线切回无线的头几秒），
                //   不冷却，交由 failStreak 3s 节奏重试，链路一好即恢复
                if (anyUncertain)
                {
                    Logger.Write(string.Format(
                        "  组 {0} 存在探测超时（设备可能仍在链路重建中），不进入冷却", groupEntry.Key));
                }
                else
                {
                    _groupCooldown[groupEntry.Key] = GroupCooldownSkips;
                    Logger.Write(string.Format("  组 {0} 全部探测无绑定（均为确定性离线），进入冷却 {1} 轮",
                        groupEntry.Key, GroupCooldownSkips));
                }
            }
        }

        /// <summary>对一个已打开的组做"直连优先，其次 slot 1-6"的探测与绑定。</summary>
        private ProbeResult ProbeDirectOrSlots(List<OpenedStream> streams, string groupKey, byte cached, out bool anyUncertain)
        {
            anyUncertain = false;
            bool uncertain;
            // 直连优先：有线鼠标（GPW2 充电线插电脑）会对 0xFF 和全部 slot 都应答，
            // 必须先判定直连，否则会被误绑到 slot 1-6
            ProbeResult direct = ProbeOn(streams, DirectIndex, groupKey, cached, out uncertain);
            if (uncertain)
            {
                anyUncertain = true;
            }
            if (direct != null)
            {
                Bind(streams, groupKey, DirectIndex, direct, "有线直连");
                Logger.Write(string.Format("直连模式: feature=0x{0:X4} 电量={1}",
                    direct.FeatureId, direct.HasReading ? direct.Percent + "%" : "待补读"));
                return direct;
            }
            foreach (int slot in ReceiverSlots)
            {
                ProbeResult probed = ProbeOn(streams, (byte)slot, groupKey, cached, out uncertain);
                if (uncertain)
                {
                    anyUncertain = true;
                }
                if (probed != null)
                {
                    Bind(streams, groupKey, (byte)slot, probed, "接收器");
                    Logger.Write(string.Format("接收器模式: slot={0} feature=0x{1:X4} 电量={2}",
                        slot, probed.FeatureId, probed.HasReading ? probed.Percent + "%" : "待补读"));
                    return probed;
                }
            }
            return null;
        }

        private static string InterfaceKey(string devicePath)
        {
            // 分组必须同时含 PID 与 interface：真机实测充电线插入后出现有线鼠标
            // 接口（PID 0xC09B），与接收器（PID 0xC547）同为 mi_02，若只按 mi 分组
            // 会把两个物理设备混进同一组导致应答交错
            string path = devicePath ?? string.Empty;
            Match pid = Regex.Match(path, @"pid_([0-9a-f]+)", RegexOptions.IgnoreCase);
            Match mi = Regex.Match(path, @"mi_(\d+)", RegexOptions.IgnoreCase);
            if (pid.Success && mi.Success)
            {
                return pid.Groups[1].Value + "&mi_" + mi.Groups[1].Value;
            }
            return path;
        }


        private static string ShortPath(string path)
        {
            if (path == null)
            {
                return "";
            }
            int i = path.IndexOf("hid#", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? path.Substring(i, Math.Min(46, path.Length - i)) : path;
        }

        private bool TryOpen(HidDevice device, out OpenedStream opened)
        {
            opened = default(OpenedStream);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var configuration = new OpenConfiguration();
                configuration.SetOption(OpenOption.Exclusive, false);
                configuration.SetOption(OpenOption.Interruptible, true);
                HidStream stream = device.Open(configuration);
                // 短 read 粒度让 QueryOn 的 deadline 真实生效（小步轮询）：
                // 设备应答约 ~800ms（唤醒延迟），deadline 1200ms 内必然被某次 read 捕获
                stream.ReadTimeout = 150;
                stream.WriteTimeout = 200;
                opened.Stream = stream;
                opened.MaxInputLength = Math.Max(device.GetMaxInputReportLength(), 32);
                Logger.Write(string.Format("  open 耗时 {0}ms: {1}",
                    sw.ElapsedMilliseconds, ShortPath(device.DevicePath)));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write(string.Format("  open 失败({0}ms): {1}",
                    sw.ElapsedMilliseconds, ex.Message));
                return false;
            }
        }

        private sealed class ProbeResult
        {
            public byte FeatureIndex;
            public ushort FeatureId;
            public int Percent;          // 探测时顺带读到的 func1 数据（省一次 805ms 查询）
            public bool Charging;
            public bool ExternalPower;
            public bool HasReading;      // 探测是否顺带读到了有效电量；false=在线已确认但电量待常规查询补读
        }

        /// <summary>
        /// 探测指定 index 上是否有带电量功能的设备，null 表示离线/无电量功能。
        /// 不发 ping：GetFeature/GetBattery 的应答本身等价于在线判定（0x8F=接收器否定
        /// 应答、超时=设备不在），省一次 ~805ms 设备唤醒延迟。
        ///
        /// uncertain 语义：探测中出现"超时"（区别于接收器立即代答的 0x8F 否定应答）。
        /// 超时只说明"设备暂时未应答"（如拔线后无线链路重建中），不代表设备不在，
        /// 上层据此决定该组是否允许进入冷却（真机教训：把超时当确定性离线送进冷却，
        /// 导致拔线后鼠标明明在 slot1 上却被跳过 40+ 秒无法恢复）。
        /// </summary>
        private ProbeResult ProbeOn(List<OpenedStream> streams, byte index, string groupKey, byte cachedFeatureIndex, out bool uncertain)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            uncertain = false;
            int percent; bool charging; bool externalPower;

            // feature index 缓存命中：直接读电量，1 次请求同时完成在线判定与读数
            if (cachedFeatureIndex != 0)
            {
                QueryOutcome outcome;
                byte[] resp = QueryOn(streams, LogitechHidpp.BuildBatteryRequest(index, cachedFeatureIndex), 1200, out outcome);
                if (outcome == QueryOutcome.Timeout)
                {
                    uncertain = true;
                }
                if (resp != null && !LogitechHidpp.IsError(resp) && !LogitechHidpp.IsHidpp1Error(resp)
                    && TryParseAnswer(resp, out percent, out charging, out externalPower))
                {
                    Logger.Write(string.Format(
                        "  probe 0x{0:X2}: 在线（缓存 feature=0x{1:X2}，耗时 {2}ms）",
                        index, cachedFeatureIndex, sw.ElapsedMilliseconds));
                    return new ProbeResult
                    {
                        FeatureIndex = cachedFeatureIndex,
                        FeatureId = LogitechHidpp.UnifiedBatteryId,
                        Percent = percent, Charging = charging, ExternalPower = externalPower,
                        HasReading = true
                    };
                }
                // 缓存失配（固件重枚举后 index 变化等）→ 落入 GetFeature 流程重查
            }

            foreach (ushort featureId in LogitechHidpp.BatteryFeatureIds)
            {
                QueryOutcome outcome;
                byte[] resp = QueryOn(streams, LogitechHidpp.BuildGetFeature(index, featureId), 1200, out outcome);
                if (outcome == QueryOutcome.Timeout)
                {
                    uncertain = true;
                }
                if (resp == null || LogitechHidpp.IsError(resp) || LogitechHidpp.IsHidpp1Error(resp))
                {
                    Logger.Write(string.Format("  probe 0x{0:X2}: 离线（{1}，耗时 {2}ms）",
                        index, DescribeOutcome(outcome), sw.ElapsedMilliseconds));
                    return null;
                }
                int featureIndex = LogitechHidpp.ParseFeatureIndexResponse(resp);
                if (featureIndex == 0)
                {
                    Logger.Write(string.Format(
                        "  probe 0x{0:X2}: 在线但无电量 feature（耗时 {1}ms）",
                        index, sw.ElapsedMilliseconds));
                    continue;
                }
                resp = QueryOn(streams, LogitechHidpp.BuildBatteryRequest(index, (byte)featureIndex), 1200, out outcome);
                if (outcome == QueryOutcome.Timeout)
                {
                    uncertain = true;
                }
                // 必须验证 func1 应答可解析出有效 SOC，防止误绑到无电量 feature
                if (resp != null && !LogitechHidpp.IsError(resp) && !LogitechHidpp.IsHidpp1Error(resp)
                    && TryParseAnswer(resp, out percent, out charging, out externalPower))
                {
                    _featureIndexCache[groupKey] = (byte)featureIndex;   // 缓存，重探测免查
                    Logger.Write(string.Format("  probe 0x{0:X2}: 在线（耗时 {1}ms）",
                        index, sw.ElapsedMilliseconds));
                    return new ProbeResult
                    {
                        FeatureIndex = (byte)featureIndex,
                        FeatureId = featureId,
                        Percent = percent, Charging = charging, ExternalPower = externalPower,
                        HasReading = true
                    };
                }
                // GetFeature 成功 = 设备在线实锤；电量读取失败（链路重建中等原因）
                // 不否定在线事实：仍绑定，电量由绑定后的常规查询补读
                Logger.Write(string.Format(
                    "  probe 0x{0:X2}: 在线但电量读取失败（{1}），绑定后补读电量，耗时 {2}ms",
                    index, DescribeOutcome(outcome), sw.ElapsedMilliseconds));
                return new ProbeResult
                {
                    FeatureIndex = (byte)featureIndex,
                    FeatureId = featureId,
                    HasReading = false
                };
            }
            return null;
        }

        private static string DescribeOutcome(QueryOutcome outcome)
        {
            switch (outcome)
            {
                case QueryOutcome.WriteFail: return "写入失败";
                case QueryOutcome.Answer: return "否定应答/无效应答";
                default: return "无应答超时";
            }
        }

        /// <summary>发送请求并收取匹配的应答 payload；超时/不匹配返回 null。</summary>
        private static byte[] QueryOn(List<OpenedStream> streams, byte[] request, int timeoutMs, out QueryOutcome outcome)
        {
            outcome = QueryOutcome.Timeout;
            bool written = false;
            var qsw = System.Diagnostics.Stopwatch.StartNew();
            foreach (OpenedStream s in streams)
            {
                try
                {
                    s.Stream.Write(request, 0, request.Length);
                    written = true;
                    break;
                }
                catch { }
            }
            if (!written)
            {
                outcome = QueryOutcome.WriteFail;
                return null;
            }
            long writeMs = qsw.ElapsedMilliseconds;
            // 小步快轮询：每句柄单次 read 最多阻塞 120ms（ReadTimeout），
            // 总时长受 deadline 约束。应答无论先出现在哪个 collection、何时到达，
            // 都能在 120ms 内被捕获（长帧在 Col02、短帧在 Col01 的真机行为下
            // 不再因读取顺序而白等半轮）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                foreach (OpenedStream s in streams)
                {
                    byte[] buffer = new byte[s.MaxInputLength];
                    int count;
                    try
                    {
                        count = s.Stream.Read(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        continue;   // TimeoutException 等
                    }
                    if (count <= 0)
                    {
                        continue;
                    }
                    byte[] payload = LogitechHidpp.ExtractPayload(buffer, count);
                    if (payload == null || payload.Length < 4)
                    {
                        continue;
                    }
                    if (payload[0] != request[1])            // 设备索引不匹配
                    {
                        continue;
                    }
                    // 错误帧立即返回：0x8F/0xFF 是"否定应答"（如接收器对 0xFF 的拒绝），
                    // 快速交给上层判定离线，避免空转等满超时（真机实测探测慢的主因）
                    if (payload[1] == 0x8F || (payload[1] == 0xFF && payload[2] == 0x02))
                    {
                        outcome = QueryOutcome.Answer;
                        return payload;
                    }
                    if ((payload[2] & 0x0F) != LogitechHidpp.SwId)   // 软件标识不匹配
                    {
                        continue;
                    }
                    Logger.Write(string.Format(
                        "  QueryOn: write={0}ms, 应答在 {1}ms 到达（长帧={2}）",
                        writeMs, qsw.ElapsedMilliseconds, buffer[0] == 0x11));
                    outcome = QueryOutcome.Answer;
                    return payload;
                }
            }
            Logger.Write(string.Format("  QueryOn: 超时 {0}ms 无应答（write={1}ms）",
                timeoutMs, writeMs));
            return null;
        }

        private static bool TryParseAnswer(byte[] payload, out int percent, out bool charging, out bool externalPower)
        {
            bool ok = LogitechHidpp.TryParseUnifiedBattery(payload, out percent, out charging, out externalPower);
            if (!ok || percent <= 0)   // 全 0 帧等无效应答
            {
                percent = 0;
                charging = false;
                externalPower = false;
                return false;
            }
            return true;
        }
    }
}
