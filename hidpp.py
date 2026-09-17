"""HID++ 2.0 协议纯逻辑层：帧构造、应答解析。无 IO 依赖，可完全单测。

报文布局（剥掉 Report ID 后的 payload）：
  payload[0] = 设备索引（直连 0xFF，接收器 slot 1-6）
  payload[1] = feature index（错误帧时为 0xFF）
  payload[2] = (function << 4) | SW_ID
  payload[3:] = 参数
"""
SHORT_REPORT_ID = 0x10
LONG_REPORT_ID = 0x11
SW_ID = 0x1  # 软件标识，请求与应答匹配用（1-15 任取）

ROOT_FEATURE_ID = 0x0000
BATTERY_STATUS_ID = 0x1000
ADC_MEASUREMENT_ID = 0x1004
BATTERY_FEATURE_IDS = (BATTERY_STATUS_ID, ADC_MEASUREMENT_ID)


def build_short(device_index: int, feature_index: int, function: int,
                params: bytes = b"") -> bytes:
    """构造 HID++ 短请求帧（含 Report ID 共 7 字节），params 最多 3 字节。"""
    if len(params) > 3:
        raise ValueError("short request params max 3 bytes")
    payload = bytes([device_index, feature_index, (function << 4) | SW_ID]) + params
    return bytes([SHORT_REPORT_ID]) + payload.ljust(6, b"\x00")


def build_ping(device_index: int) -> bytes:
    """Root feature (0x0000) func 1 的 Ping 请求，用于探测设备在线。"""
    return build_short(device_index, ROOT_FEATURE_ID, 0x01)


def build_get_feature(device_index: int, feature_id: int) -> bytes:
    """Root func 0 GetFeature：查询 feature_id 在该设备上分配到的 index。"""
    return build_short(device_index, ROOT_FEATURE_ID, 0x00,
                       bytes([feature_id >> 8, feature_id & 0xFF]))


def build_battery_request(device_index: int, feature_index: int) -> bytes:
    """电量 feature func 0 请求（0x1000 GetBatteryLevelStatus / 0x1004 共用）。"""
    return build_short(device_index, feature_index, 0x00)


def extract_payload(data) -> bytes | None:
    """从 hidapi read 返回的数据剥离 Report ID。

    兼容两种 hidapi 行为：返回含 report id（7/20 字节）或不含（6/19 字节）。
    无法识别时返回 None。
    """
    data = bytes(data)
    if data and data[0] in (SHORT_REPORT_ID, LONG_REPORT_ID):
        data = data[1:]
    if len(data) in (6, 19):
        return data
    return None


def is_error(payload: bytes) -> bool:
    """HID++ 2.0 错误帧特征：payload[1] == 0xFF 且 payload[2] == 0x02。"""
    return len(payload) >= 4 and payload[1] == 0xFF and payload[2] == 0x02
