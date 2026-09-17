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

        public static BatteryReading Offline(int? lastPercent)
        {
            return new BatteryReading { Percent = lastPercent, Charging = false, Online = false };
        }
    }

    /// <summary>
    /// GPW2 电量查询设备层（基于 HidSharp）。
    ///
    /// 发现策略（不硬编码 PID，天然兼容其他有电量功能的罗技无线设备）：
    ///   1. 枚举 VID=0x046D 且 usage page 为罗技厂商页（0xFF00 / 0xFF43）的接口
    ///   2. 同一 interface（mi_XX）的多个 collection 归为一组：写走可写句柄，
    ///      读轮询组内全部句柄（真机实测 GPW2 协议 4.2 长报文应答只在 Col02 出现）
    ///   3. 先探测直连形态（device index 0xFF），再逐个探测接收器 slot 1-6
    ///   4. 电量 feature 优先 0x1000（直接百分比），无效则回退 0x1004（电压换算）
    ///   5. 单次查询失败内部快速重试（G HUB 并发通信偶发超时）
    /// </summary>
    internal sealed class Gpw2Device
    {
        private const int LogitechVendorId = 0x046D;
        private const byte DirectIndex = 0xFF;
        private static readonly int[] ReceiverSlots = new int[] { 1, 2, 3, 4, 5, 6 };
        private static readonly int[] UsagePages = new int[] { 0xFF00, 0xFF43 };

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
                        Logger.Write("ReadBattery: 无绑定句柄，开始全量重探测");
                        OpenAndDetect();
                    }
                    if (_streams == null)
                    {
                        Logger.Write(string.Format(
                            "ReadBattery: 重探测后仍无设备，耗时 {0}ms", sw.ElapsedMilliseconds));
                        return BatteryReading.Offline(null);
                    }
                    Logger.Write(string.Format("ReadBattery: 查询 devIdx={0} featureIdx={1}",
                        _deviceIndex, _featureIndex));
                    // 单次查询不内部重试：离线时的快速重试节奏由上层 timer 控制，
                    // 连续失败达到阈值后由上层调用 Reset() 触发全量重探测。
                    // 这样锁的占空比低，手动刷新等并发查询不会被长时间阻塞。
                    byte[] request = LogitechHidpp.BuildBatteryRequest(_deviceIndex, _featureIndex);
                    byte[] resp = QueryOn(_streams, request, 500);
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
                    Logger.Write(string.Format(
                        "ReadBattery: 查询失败（无应答/无效应答），保留句柄快速返回，耗时 {0}ms",
                        sw.ElapsedMilliseconds));
                    return BatteryReading.Offline(_lastPercent);   // 保留句柄，快速失败
                }
                catch (Exception ex)
                {
                    Logger.Write("hidapi IO 异常: " + ex.Message + "，丢弃句柄");
                    CloseStreams();   // 句柄失效（设备拔出等）才丢弃，下次重新探测
                    return BatteryReading.Offline(_lastPercent);
                }
            }
        }

        // ---- 内部实现 ----

        /// <summary>丢弃当前绑定的句柄（保留已知电量），下次查询将全量重新探测。</summary>
        public void RequestReprobe()
        {
            lock (_sync)
            {
                CloseStreams();
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

        /// <summary>枚举罗技厂商接口，按 interface 分组打开，探测直连/接收器形态。</summary>
        private void OpenAndDetect()
        {
            var groups = new Dictionary<string, List<HidDevice>>();
            foreach (HidDevice device in DeviceList.Local.GetHidDevices(LogitechVendorId))
            {
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
            foreach (KeyValuePair<string, List<HidDevice>> groupEntry in groups)
            {
                List<HidDevice> group = groupEntry.Value;
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
                        Logger.Write("  接口被占用，跳过: " + ShortPath(device.DevicePath));
                    }
                }
                if (streams.Count == 0)
                {
                    continue;
                }

                // 直连优先：有线鼠标（GPW2 充电线插电脑）会对 0xFF 和全部 slot 都应答，
                // 必须先判定直连，否则会被误绑到 slot 1-6
                ProbeResult direct = ProbeOn(streams, DirectIndex);
                if (direct != null)
                {
                    _streams = streams;
                    _deviceIndex = DirectIndex;
                    _featureIndex = direct.FeatureIndex;
                    _featureId = direct.FeatureId;
                    Logger.Write(string.Format("直连模式: feature=0x{0:X4}", direct.FeatureId));
                    return;
                }
                foreach (int slot in ReceiverSlots)
                {
                    ProbeResult probed = ProbeOn(streams, (byte)slot);
                    if (probed != null)
                    {
                        _streams = streams;
                        _deviceIndex = (byte)slot;
                        _featureIndex = probed.FeatureIndex;
                        _featureId = probed.FeatureId;
                        Logger.Write(string.Format("接收器模式: slot={0} feature=0x{1:X4}",
                            slot, probed.FeatureId));
                        return;
                    }
                }
                foreach (OpenedStream s in streams)
                {
                    try { s.Stream.Dispose(); }
                    catch { }
                }
            }
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

        private static bool TryOpen(HidDevice device, out OpenedStream opened)
        {
            opened = default(OpenedStream);
            try
            {
                var configuration = new OpenConfiguration();
                configuration.SetOption(OpenOption.Exclusive, false);
                configuration.SetOption(OpenOption.Interruptible, true);
                HidStream stream = device.Open(configuration);
                stream.ReadTimeout = 800;
                stream.WriteTimeout = 800;
                opened.Stream = stream;
                opened.MaxInputLength = Math.Max(device.GetMaxInputReportLength(), 32);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private sealed class ProbeResult
        {
            public byte FeatureIndex;
            public ushort FeatureId;
        }

        /// <summary>探测指定 index 上是否有带电量功能的设备，null 表示离线/无电量功能。</summary>
        private ProbeResult ProbeOn(List<OpenedStream> streams, byte index)
        {
            byte[] pingResp = QueryOn(streams, LogitechHidpp.BuildPing(index), 300);
            if (pingResp == null || LogitechHidpp.IsHidpp1Error(pingResp))
            {
                Logger.Write(string.Format("  probe 0x{0:X2}: 离线（无应答或1.0错误帧）", index));
                return null;   // 无应答或 0x8F 错误帧（空 slot 真机行为）
            }
            Logger.Write(string.Format("  probe 0x{0:X2}: 在线", index));
            foreach (ushort featureId in LogitechHidpp.BatteryFeatureIds)
            {
                byte[] resp = QueryOn(streams, LogitechHidpp.BuildGetFeature(index, featureId), 300);
                if (resp == null || LogitechHidpp.IsError(resp))
                {
                    continue;
                }
                int featureIndex = LogitechHidpp.ParseFeatureIndexResponse(resp);
                if (featureIndex == 0)
                {
                    continue;
                }
                resp = QueryOn(streams, LogitechHidpp.BuildBatteryRequest(index, (byte)featureIndex), 300);
                int percent; bool charging; bool externalPower;
                // 必须验证 func1 应答可解析出有效 SOC，防止误绑到无电量 feature
                if (resp != null && !LogitechHidpp.IsError(resp)
                    && TryParseAnswer(resp, out percent, out charging, out externalPower))
                {
                    return new ProbeResult { FeatureIndex = (byte)featureIndex, FeatureId = featureId };
                }
            }
            return null;
        }

        private byte[] Query(byte[] request)
        {
            return QueryOn(_streams, request, 800);
        }

        /// <summary>发送请求并收取匹配的应答 payload；超时/不匹配返回 null。</summary>
        private static byte[] QueryOn(List<OpenedStream> streams, byte[] request, int timeoutMs)
        {
            bool written = false;
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
                return null;
            }
            for (int round = 0; round < 3; round++)
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
                        return payload;
                    }
                    if ((payload[2] & 0x0F) != LogitechHidpp.SwId)   // 软件标识不匹配
                    {
                        continue;
                    }
                    return payload;
                }
            }
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
