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


def is_hidpp1_error(payload: bytes) -> bool:
    """HID++ 1.0 错误应答：payload[1] == 0x8F，payload[4] 为错误码。

    真机实测（C547 LIGHTSPEED 接收器）：对空 slot 或接收器自身发请求，
    接收器会代回 0x8F 错误帧而非静默超时，需与有效应答严格区分。
    """
    return len(payload) >= 4 and payload[1] == 0x8F


# ---- 应答解析 ----

# 锂电 (3.7V 标称) 分段线性曲线: (电压 mV, 百分比)
_VOLTAGE_CURVE = (
    (4200, 100), (4050, 90), (3900, 75), (3780, 55), (3680, 35),
    (3580, 18), (3500, 8), (3350, 1), (3000, 0),
)


def parse_feature_index_response(payload: bytes) -> int:
    """GetFeature 应答 → 分配的 feature index；0 表示设备不支持该 feature。"""
    return payload[3]


def parse_battery_status(payload: bytes) -> tuple[int, bool] | None:
    """0x1000 GetBatteryLevelStatus 应答 → (percent 0-100, charging)。

    status: 0=discharging, 1=charging, 2=almost full, 3=full。
    电量越界视为无效，返回 None。
    """
    percent = payload[3]
    status = payload[5]
    if percent > 100:
        return None
    return percent, status in (0x01, 0x02)


def parse_adc_measurement(payload: bytes) -> tuple[int, bool] | None:
    """0x1004 ADC 应答 → 电压 mV（payload[3..4] 小端）按锂电曲线换算。

    电压超出 [3000, 4500] 视为无效。0x1004 不含充电状态，固定 False。
    """
    mv = payload[3] | (payload[4] << 8)
    if not (3000 <= mv <= 4500):
        return None
    return voltage_to_percent(mv), False


def voltage_to_percent(mv: int) -> int:
    """按分段线性锂电曲线把电压 mV 换算为百分比 0-100。"""
    if mv >= _VOLTAGE_CURVE[0][0]:
        return 100
    for (v1, p1), (v2, p2) in zip(_VOLTAGE_CURVE, _VOLTAGE_CURVE[1:]):
        if mv >= v2:
            return round(p1 + (p2 - p1) * (mv - v1) / (v2 - v1))
    return 0
