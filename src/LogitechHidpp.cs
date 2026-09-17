using System;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 罗技 HID++ 2.0 协议纯逻辑层：帧构造、应答解析。无 IO 依赖。
    ///
    /// 报文布局（剥掉 Report ID 后的 payload）：
    ///   payload[0] = 设备索引（有线直连 0xFF，接收器配对 slot 1-6）
    ///   payload[1] = feature index（2.0 错误帧时为 0xFF；1.0 错误帧时为 0x8F）
    ///   payload[2] = (function 左移 4) | SwId
    ///   payload[3:] = 参数/应答数据
    ///
    /// 真机验证过的行为（GPW2 + C547 LIGHTSPEED 接收器，2026-09-17）：
    ///   1. 短请求经 Col01 写入；GPW2（协议 4.2）的长报文应答只会出现在 Col02 读队列
    ///   2. 对空 slot 或接收器自身发请求，接收器代回 0x8F 错误帧而非静默超时
    ///   3. GPW2 电量走 UnifiedBattery feature（ID 0x1004，index 动态分配）：
    ///      func 0 是能力描述（不是电量！），func 1 GetBatteryInfo：
    ///        参数区[0] = SOC 百分比 0-100
    ///        参数区[1] = 电量档位枚举
    ///        参数区[2] = 充电状态（0=放电 1=充电）
    ///        参数区[3] = 外接电源（0=未接 1=已接）
    ///   4. 有线鼠标（GPW2 充电线插电脑，PID 0xC09B）会对 0xFF 和全部 slot 都应答
    /// </summary>
    internal static class LogitechHidpp
    {
        public const byte ShortReportId = 0x10;
        public const byte LongReportId = 0x11;
        public const byte SwId = 0x1;                 // 软件标识，请求/应答匹配用（1-15 任取）

        public const ushort RootFeatureId = 0x0000;
        public const ushort UnifiedBatteryId = 0x1004;   // UnifiedBattery（GPW2 唯一可用电量 feature）
        public static readonly ushort[] BatteryFeatureIds = new ushort[] { UnifiedBatteryId };

        /// <summary>构造 HID++ 短请求帧（含 Report ID 共 7 字节），parameters 最多 3 字节。</summary>
        public static byte[] BuildShort(byte deviceIndex, byte featureIndex, int function, params byte[] parameters)
        {
            if (parameters != null && parameters.Length > 3)
            {
                throw new ArgumentException("短请求参数最多 3 字节");
            }
            byte[] payload = new byte[6];
            payload[0] = deviceIndex;
            payload[1] = featureIndex;
            payload[2] = (byte)((function << 4) | SwId);
            if (parameters != null)
            {
                Array.Copy(parameters, 0, payload, 3, parameters.Length);
            }
            byte[] frame = new byte[7];
            frame[0] = ShortReportId;
            Array.Copy(payload, 0, frame, 1, 6);
            return frame;
        }

        /// <summary>Root feature (0x0000) func 1 的 Ping 请求，用于探测设备在线。</summary>
        public static byte[] BuildPing(byte deviceIndex)
        {
            return BuildShort(deviceIndex, 0x00, 0x01);
        }

        /// <summary>Root func 0 GetFeature：查询 featureId 在该设备上分配到的 index。</summary>
        public static byte[] BuildGetFeature(byte deviceIndex, ushort featureId)
        {
            return BuildShort(deviceIndex, 0x00, 0x00,
                (byte)((featureId >> 8) & 0xFF), (byte)(featureId & 0xFF));
        }

        /// <summary>UnifiedBattery func 1 GetBatteryStatus：读 SOC 百分比与充电状态。</summary>
        public static byte[] BuildBatteryRequest(byte deviceIndex, byte featureIndex)
        {
            return BuildShort(deviceIndex, featureIndex, 0x01);
        }

        /// <summary>
        /// 从读取缓冲剥离 Report ID，返回 payload（6/19 字节）。
        /// 兼容 hidapi 两种行为：含 report id（7/20 字节）或不含（6/19 字节）。
        /// 无法识别时返回 null。
        /// </summary>
        public static byte[] ExtractPayload(byte[] data, int length)
        {
            if (data == null || length <= 0)
            {
                return null;
            }
            int offset = 0;
            if (data[0] == ShortReportId || data[0] == LongReportId)
            {
                offset = 1;
            }
            int count = length - offset;
            if (count != 6 && count != 19)
            {
                return null;
            }
            byte[] payload = new byte[count];
            Array.Copy(data, offset, payload, 0, count);
            return payload;
        }

        /// <summary>HID++ 2.0 错误帧特征：payload[1] == 0xFF 且 payload[2] == 0x02。</summary>
        public static bool IsError(byte[] payload)
        {
            return payload != null && payload.Length >= 4 && payload[1] == 0xFF && payload[2] == 0x02;
        }

        /// <summary>HID++ 1.0 错误应答：payload[1] == 0x8F（真机实测空 slot 行为）。</summary>
        public static bool IsHidpp1Error(byte[] payload)
        {
            return payload != null && payload.Length >= 4 && payload[1] == 0x8F;
        }

        /// <summary>GetFeature 应答 → 分配的 feature index；0 表示设备不支持该 feature。</summary>
        public static int ParseFeatureIndexResponse(byte[] payload)
        {
            return payload[3];
        }

        /// <summary>
        /// UnifiedBattery func 1 GetBatteryStatus 应答 → 电量与充电状态。
        /// 参数区（payload[3] 起）：[0]=SOC 百分比、[2]=充电状态（0=放电 1=充电）、[3]=外接电源。
        /// </summary>
        public static bool TryParseUnifiedBattery(byte[] payload, out int percent, out bool charging, out bool externalPower)
        {
            percent = 0;
            charging = false;
            externalPower = false;
            if (payload == null || payload.Length < 7)
            {
                return false;
            }
            percent = payload[3];
            byte chargeStatus = payload[5];
            byte powerFlag = payload[6];
            if (percent > 100)
            {
                return false;
            }
            charging = chargeStatus == 0x01;              // 0=放电 1=充电（0x02 充满亦不算充电中）
            externalPower = powerFlag != 0;
            return true;
        }
    }
}
