using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace GPW2BatteryShow
{
    /// <summary>
    /// 设备发现状态持久化（device_state.json，运行时数据而非用户配置）。
    ///
    /// 记录两类信息，消除冷启动与重探测时的重复无效扫描：
    ///   1. 上次成功绑定的接口组 + 设备索引 → 启动/重探测优先只扫该组，
    ///      命中即 1 次请求完成绑定（其余组完全不扫）
    ///   2. 组键 → 电量 feature index 缓存 → 免查 GetFeature，再省一次 ~800ms 应答
    ///
    /// 文件损坏/缺失/写失败一律静默降级为无状态，不影响主功能。
    /// 内容仅为 HID 技术标识（pid/mi 与 feature index），不含用户隐私。
    /// </summary>
    internal sealed class DeviceState
    {
        /// <summary>上次成功绑定的接口组键（如 "c547&mi_02"、"c09b&mi_02"）；null=无记忆。</summary>
        public string LastGroup;
        /// <summary>上次成功绑定的设备索引（0xFF 直连或 1-6 接收器 slot）。</summary>
        public int LastIndex;

        private static string StatePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "device_state.json"); }
        }

        /// <summary>读取持久化状态；无文件/损坏返回 null。</summary>
        public static DeviceState Load()
        {
            try
            {
                if (!File.Exists(StatePath))
                {
                    return null;
                }
                var serializer = new JavaScriptSerializer();
                var raw = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(StatePath));
                if (raw == null)
                {
                    return null;
                }
                var state = new DeviceState();
                object value;
                if (raw.TryGetValue("last_group", out value))
                {
                    state.LastGroup = Convert.ToString(value);
                    if (string.IsNullOrEmpty(state.LastGroup))
                    {
                        state.LastGroup = null;
                    }
                }
                if (raw.TryGetValue("last_index", out value))
                {
                    state.LastIndex = Convert.ToInt32(value);
                }
                if (state.LastGroup == null)
                {
                    return null;
                }
                return state;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存成功绑定记忆与 feature index 缓存。失败静默（下次探测自愈）。</summary>
        public static void Save(string lastGroup, int lastIndex, Dictionary<string, byte> featureCache)
        {
            try
            {
                var raw = new Dictionary<string, object>();
                raw["last_group"] = lastGroup ?? "";
                raw["last_index"] = lastIndex;
                var features = new Dictionary<string, object>();
                if (featureCache != null)
                {
                    foreach (KeyValuePair<string, byte> entry in featureCache)
                    {
                        features[entry.Key] = entry.Value;
                    }
                }
                raw["features"] = features;
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(StatePath, serializer.Serialize(raw));
            }
            catch
            {
                // 写失败不影响主功能
            }
        }
    }
}
