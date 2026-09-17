// 罗技 HID++ 真机诊断工具：枚举接口 → 分组多 collection 探测 → 读取全部电量类 feature 原始数据
// 用法：LogiDiag.exe   （把输出完整复制回来）
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HidSharp;

namespace LogiDiag
{
    internal static class Program
    {
        private const byte SwId = 0x1;

        private sealed class Channel
        {
            public HidStream Stream;
            public int MaxInput;
        }

        private static void Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== Logitech HID interfaces (VID 0x046D) ===");
            foreach (HidDevice d in DeviceList.Local.GetHidDevices(0x046D))
            {
                Console.WriteLine(string.Format(
                    "  PID=0x{0:X4} path={1} in={2} out={3}",
                    d.ProductID,
                    Shorten(d.DevicePath),
                    d.GetMaxInputReportLength(),
                    d.GetMaxOutputReportLength()));
            }

            var groups = new Dictionary<string, List<HidDevice>>();
            foreach (HidDevice device in DeviceList.Local.GetHidDevices(0x046D))
            {
                Match m = Regex.Match(device.DevicePath.ToLowerInvariant(), @"mi_(\d+)");
                string key = m.Success ? ("mi_" + m.Groups[1].Value) : device.DevicePath;
                List<HidDevice> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<HidDevice>();
                    groups[key] = list;
                }
                list.Add(device);
            }

            Console.WriteLine();
            Console.WriteLine("=== grouped probing ===");
            foreach (KeyValuePair<string, List<HidDevice>> group in groups)
            {
                Console.WriteLine("--- group " + group.Key + " ---");
                var channels = new List<Channel>();
                foreach (HidDevice device in group.Value)
                {
                    try
                    {
                        var config = new OpenConfiguration();
                        config.SetOption(OpenOption.Exclusive, false);
                        config.SetOption(OpenOption.Interruptible, true);
                        HidStream s = device.Open(config);
                        s.ReadTimeout = 500;
                        s.WriteTimeout = 500;
                        channels.Add(new Channel
                        {
                            Stream = s,
                            MaxInput = Math.Max(device.GetMaxInputReportLength(), 32)
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  open fail: " + Shorten(device.DevicePath) + " -> " + ex.Message);
                    }
                }
                if (channels.Count == 0)
                {
                    continue;
                }

                ProbeDevice(channels, 0xFF);
                for (byte slot = 1; slot <= 6; slot++)
                {
                    ProbeDevice(channels, slot);
                }
                foreach (Channel c in channels)
                {
                    try { c.Stream.Dispose(); }
                    catch { }
                }
            }
            Console.WriteLine();
            Console.WriteLine("=== diag done ===");
        }

        private static void ProbeDevice(List<Channel> channels, byte index)
        {
            byte[] resp = Query(channels, Frame(index, 0x00, 0x01));
            if (resp == null)
            {
                Console.WriteLine("  ping 0x" + index.ToString("X2") + ": no reply");
                return;
            }
            if (resp[1] == 0x8F)
            {
                Console.WriteLine("  ping 0x" + index.ToString("X2") + ": HID++1.0 error code=0x" +
                    resp[4].ToString("X2"));
                return;
            }
            Console.WriteLine("  ping 0x" + index.ToString("X2") + ": ONLINE protocol=" +
                resp[4] + "." + resp[5]);

            ushort[] features = new ushort[] { 0x1000, 0x1001, 0x1002, 0x1004 };
            foreach (ushort fid in features)
            {
                byte[] get = Frame(index, 0x00, 0x00, (byte)((fid >> 8) & 0xFF), (byte)(fid & 0xFF));
                byte[] gr = Query(channels, get);
                if (gr == null || IsError(gr) || gr[3] == 0)
                {
                    continue;
                }
                int fidx = gr[3];
                byte[] br = Query(channels, Frame(index, (byte)fidx, 0x00));
                Console.WriteLine("  feature 0x" + fid.ToString("X4") + " -> idx=0x" +
                    fidx.ToString("X2") + " raw: " +
                    (br == null ? "no reply" : BitConverter.ToString(br)));
            }
        }

        private static byte[] Frame(byte deviceIndex, byte featureIndex, int function, params byte[] parameters)
        {
            byte[] f = new byte[7];
            f[0] = 0x10;
            f[1] = deviceIndex;
            f[2] = featureIndex;
            f[3] = (byte)((function << 4) | SwId);
            if (parameters != null) Array.Copy(parameters, 0, f, 4, parameters.Length);
            return f;
        }

        private static bool IsError(byte[] payload)
        {
            return payload.Length >= 4 && payload[1] == 0xFF && payload[2] == 0x02;
        }

        private static byte[] Query(List<Channel> channels, byte[] request)
        {
            bool written = false;
            foreach (Channel c in channels)
            {
                try
                {
                    c.Stream.Write(request, 0, request.Length);
                    written = true;
                    break;
                }
                catch { }
            }
            if (!written)
            {
                return null;
            }
            for (int round = 0; round < 4; round++)
            {
                foreach (Channel c in channels)
                {
                    byte[] buffer = new byte[c.MaxInput];
                    int n;
                    try
                    {
                        n = c.Stream.Read(buffer, 0, buffer.Length);
                    }
                    catch
                    {
                        continue;
                    }
                    if (n <= 0) continue;
                    int offset = (buffer[0] == 0x10 || buffer[0] == 0x11) ? 1 : 0;
                    int count = n - offset;
                    if (count != 6 && count != 19) continue;
                    byte[] payload = new byte[count];
                    Array.Copy(buffer, offset, payload, 0, count);
                    if (payload[0] != request[1]) continue;
                    return payload;   // 诊断工具不过滤 SW_ID，保留全部原始数据
                }
            }
            return null;
        }

        private static string Shorten(string path)
        {
            int i = path.IndexOf("hid#", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? path.Substring(i, Math.Min(52, path.Length - i)) : path;
        }
    }
}
