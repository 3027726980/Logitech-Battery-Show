using System;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 罗技 HID++ 2.0 协议纯逻辑层：帧构造、应答解析。
    ///
    /// 报文布局（剥掉 Report ID 后的 payload）：
    ///   payload[0] = 设备索引（有线直连 0xFF，接收器配对 slot 1-6）
    ///   payload[1] = feature index（2.0 错误帧时为 0xFF；1.0 错误帧时为 0x8F）
    ///   payload[2] = (function 左移 4) | SwId
    ///   payload[3:] = 参数/应答数据
    ///
    /// 真机验证过的行为（GPW2 + C547 LIGHTSPEED 接收器）：
    ///   1. 短请求经 Col01 写入；GPW2（协议 4.2）的长报文应答只会出现在 Col02 读队列
    ///   2. 对空 slot 或接收器自身发请求，接收器代回 0x8F 错误帧而非静默超时
    ///   3. GPW2 对 0x1000 声称支持但电量查询回全 0 无效帧，需回退 0x1004 电压
    /// </summary>
    internal static class LogitechHidpp
    {
        public const byte ShortReportId = 0x10;
        public const byte LongReportId = 0x11;
        public const byte SwId = 0x1;                 // 软件标识，请求/应答匹配用（1-15 任取）

        public const ushort RootFeatureId = 0x0000;
        public const ushort BatteryStatusId = 0x1000;
        public const ushort AdcMeasurementId = 0x1004;
        public static readonly ushort[] BatteryFeatureIds = new ushort[] { BatteryStatusId, AdcMeasurementId };

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

        /// <summary>电量 feature func 0 请求（0x1000 与 0x1004 共用 func 0）。</summary>
        public static byte[] BuildBatteryRequest(byte deviceIndex, byte featureIndex)
        {
            return BuildShort(deviceIndex, featureIndex, 0x00);
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
        /// 0x1000 GetBatteryLevelStatus 应答 → 电量百分比与充电状态。
        /// payload[3]=level, payload[5]=status（0=放电 1=充电 2=接近充满）。
        /// </summary>
        public static bool TryParseBatteryStatus(byte[] payload, out int percent, out bool charging)
        {
            percent = 0;
            charging = false;
            if (payload == null || payload.Length < 6)
            {
                return false;
            }
            percent = payload[3];
            if (percent > 100)
            {
                return false;
            }
            byte status = payload[5];
            charging = status == 0x01 || status == 0x02;
            return true;
        }

        /// <summary>
        /// 0x1004 ADC 应答 → payload[3..4] 小端电压 mV，按锂电曲线换算百分比。
        /// 电压超出 [3000, 4500] 视为无效。0x1004 不含充电状态，固定 false。
        /// </summary>
        public static bool TryParseAdcMeasurement(byte[] payload, out int percent, out bool charging)
        {
            percent = 0;
            charging = false;
            if (payload == null || payload.Length < 6)
            {
                return false;
            }
            int mv = payload[3] | (payload[4] << 8);
            if (mv < 3000 || mv > 4500)
            {
                return false;
            }
            percent = VoltageToPercent(mv);
            return true;
        }

        // 锂电（3.7V 标称）分段线性曲线：电压 mV → 百分比
        private static readonly int[] CurveMv = new int[] { 4200, 4050, 3900, 3780, 3680, 3580, 3500, 3350, 3000 };
        private static readonly int[] CurvePercent = new int[] { 100, 90, 75, 55, 35, 18, 8, 1, 0 };

        /// <summary>按分段线性锂电曲线把电压 mV 换算为百分比 0-100。</summary>
        public static int VoltageToPercent(int mv)
        {
            if (mv >= CurveMv[0])
            {
                return 100;
            }
            for (int i = 0; i < CurveMv.Length - 1; i++)
            {
                if (mv >= CurveMv[i + 1])
                {
                    int v1 = CurveMv[i], v2 = CurveMv[i + 1];
                    int p1 = CurvePercent[i], p2 = CurvePercent[i + 1];
                    return (int)Math.Round(p1 + (p2 - p1) * (double)(mv - v1) / (v2 - v1));
                }
            }
            return 0;
        }
    }
}
