# GPW2 电池托盘显示应用 · 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Windows 11 托盘显示罗技 GPW2 鼠标电量，通过原生 HID++ 协议读取，不依赖 G HUB，打包为单 exe。

**Architecture:** 五模块扁平结构：`hidpp.py`（纯协议帧逻辑，可完全单测）→ `devices.py`（hidapi 设备发现与查询，hidapi 可注入）→ `tray.py`（图标绘制纯函数 + pystray 组装）→ `config.py`（配置与注册表自启）→ `main.py`（轮询线程 + 入口）。轮询线程读电量 → 绘制图标 → 更新托盘，低电量经纯函数判定后气泡通知。

**Tech Stack:** Python 3.12、hidapi（pip 包 `hid`）、pystray、Pillow、pytest、PyInstaller。

**设计文档:** `docs/superpowers/specs/2026-09-17-gpw2-battery-tray-design.md`

---

### Task 0: 项目脚手架

**Files:**
- Create: `requirements.txt`
- Create: `cli.py`（占位，Task 3 实现）

- [ ] **Step 0.1: 创建虚拟环境并安装依赖**

```bash
cd "D:/MyProject/Logitech Battery Show"
python -m venv .venv
source .venv/Scripts/activate
pip install hidapi pystray Pillow pytest
```

Expected: 全部安装成功，`pip list` 可见 hidapi、pystray、Pillow、pytest。

- [ ] **Step 0.2: 写 requirements.txt**

```text
# 运行时依赖
hidapi>=0.14.0
pystray>=0.19.5
Pillow>=10.0.0

# 开发依赖
pytest>=8.0.0
```

- [ ] **Step 0.3: 创建空的 cli.py 占位**

```python
"""真机验证脚本：读取一次 GPW2 电量并打印。Task 3 实现。"""
```

- [ ] **Step 0.4: 确认 .gitignore 已包含 `.venv/`（已存在），提交**

```bash
git add requirements.txt cli.py
git commit -m "chore: 项目脚手架与依赖清单"
```

---

### Task 1: hidpp.py — 帧构造与报文识别

**Files:**
- Create: `hidpp.py`
- Test: `tests/test_hidpp_frames.py`

- [ ] **Step 1.1: 写失败测试**

```python
"""hidpp.py 帧构造与报文识别的单元测试。"""
import pytest

from hidpp import (
    build_short, build_ping, build_get_feature, build_battery_request,
    extract_payload, is_error, SW_ID,
)


class TestBuildShort:
    def test_basic_frame(self):
        # [ReportID=0x10, Dev=0xFF, FeatIdx=0x06, Func=0<<4|SW_ID, p0..p2=0]
        assert build_short(0xFF, 0x06, 0x00) == bytes.fromhex("10FF0601000000")

    def test_with_params(self):
        # Root GetFeature 0x1000: feature_index=0x00, func=0, params=[0x10, 0x00]
        assert build_short(0xFF, 0x00, 0x00, b"\x10\x00") == bytes.fromhex("10FF0001100000")

    def test_addr_contains_function(self):
        # ping: func=1 → addr = (1<<4)|SW_ID = 0x11
        frame = build_short(0x02, 0x00, 0x01)
        assert frame[3] == (1 << 4) | SW_ID

    def test_too_many_params_raises(self):
        with pytest.raises(ValueError):
            build_short(0xFF, 0x00, 0x00, b"\x00" * 4)


class TestBuildHelpers:
    def test_build_ping(self):
        assert build_ping(0xFF) == bytes.fromhex("10FF0011000000")

    def test_build_get_feature(self):
        assert build_get_feature(0xFF, 0x1000) == bytes.fromhex("10FF0001100000")

    def test_build_battery_request(self):
        assert build_battery_request(0xFF, 0x06) == bytes.fromhex("10FF0601000000")


class TestExtractPayload:
    def test_strips_report_id(self):
        data = bytes([0x10]) + bytes(range(6))          # 7 字节含 report id
        assert extract_payload(data) == bytes(range(6))

    def test_accepts_long_with_id(self):
        data = bytes([0x11]) + bytes(range(19))         # 20 字节
        assert extract_payload(data) == bytes(range(19))

    def test_accepts_bare_payload(self):
        data = bytes(range(6))                          # 已剥 report id 的 6 字节
        assert extract_payload(data) == data

    def test_invalid_length_returns_none(self):
        assert extract_payload(b"\x00" * 5) is None
        assert extract_payload(b"") is None


class TestIsError:
    def test_error_frame_detected(self):
        # HID++ 2.0 错误帧: payload[1]=0xFF, payload[2]=0x02
        payload = bytes([0xFF, 0xFF, 0x02, 0x10, 0x09, 0x00, 0x00])
        assert is_error(payload) is True

    def test_normal_frame_not_error(self):
        payload = bytes([0xFF, 0x06, 0x01, 86, 85, 0x00])
        assert is_error(payload) is False
```

- [ ] **Step 1.2: 运行测试确认失败**

```bash
python -m pytest tests/test_hidpp_frames.py -v
```

Expected: FAIL / ERROR（`ModuleNotFoundError: No module named 'hidpp'`）。

- [ ] **Step 1.3: 实现 hidpp.py（帧构造部分）**

```python
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
```

- [ ] **Step 1.4: 运行测试确认通过**

```bash
python -m pytest tests/test_hidpp_frames.py -v
```

Expected: 全部 PASS。

- [ ] **Step 1.5: 提交**

```bash
git add hidpp.py tests/test_hidpp_frames.py
git commit -m "feat: HID++ 短帧构造与报文识别"
```

---

### Task 2: hidpp.py — 应答解析与电量换算

**Files:**
- Modify: `hidpp.py`（追加解析函数）
- Test: `tests/test_hidpp_parse.py`

- [ ] **Step 2.1: 写失败测试**

```python
"""hidpp.py 应答解析的单元测试。"""
from hidpp import (
    parse_feature_index_response, parse_battery_status,
    parse_adc_measurement, voltage_to_percent,
)


class TestParseFeatureIndex:
    def test_supported_feature(self):
        # GetFeature 0x1000 应答：分配到 index 0x06
        payload = bytes([0xFF, 0x00, 0x01, 0x06, 0x00, 0x00])
        assert parse_feature_index_response(payload) == 6

    def test_unsupported_feature_returns_zero(self):
        payload = bytes([0xFF, 0x00, 0x01, 0x00, 0x00, 0x00])
        assert parse_feature_index_response(payload) == 0


class TestParseBatteryStatus:
    def test_discharging(self):
        # payload[3]=level, payload[4]=nextLevel, payload[5]=status(0=discharging)
        payload = bytes([0xFF, 0x06, 0x01, 86, 85, 0x00, 0x00, 0x00, 0x00])
        assert parse_battery_status(payload) == (86, False)

    def test_charging_status_codes(self):
        for status in (0x01, 0x02):  # 1=charging, 2=almost full
            payload = bytes([0x02, 0x06, 0x01, 50, 60, status, 0x00, 0x00, 0x00])
            assert parse_battery_status(payload) == (50, True)

    def test_invalid_percent_returns_none(self):
        payload = bytes([0xFF, 0x06, 0x01, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00])
        assert parse_battery_status(payload) is None


class TestParseAdcMeasurement:
    def test_voltage_converted(self):
        # mv = 0x0F84 = 3972 → 曲线落点 82
        payload = bytes([0xFF, 0x06, 0x01, 0x84, 0x0F, 0x00, 0x00, 0x00, 0x00])
        assert parse_adc_measurement(payload) == (82, False)

    def test_invalid_voltage_returns_none(self):
        # mv = 2000，低于 3000 下限
        payload = bytes([0xFF, 0x06, 0x01, 0xD0, 0x07, 0x00, 0x00, 0x00, 0x00])
        assert parse_adc_measurement(payload) is None


class TestVoltageCurve:
    def test_full_voltage(self):
        assert voltage_to_percent(4200) == 100
        assert voltage_to_percent(4300) == 100

    def test_curve_anchor(self):
        assert voltage_to_percent(3900) == 75

    def test_linear_interpolation(self):
        # (4050, 90) 到 (3900, 75) 之间：4000 → 90 - 15*(50/150) = 85
        assert voltage_to_percent(4000) == 85

    def test_low_voltage(self):
        assert voltage_to_percent(3350) == 1
        assert voltage_to_percent(2000) == 0
```

- [ ] **Step 2.2: 运行测试确认失败**

```bash
python -m pytest tests/test_hidpp_parse.py -v
```

Expected: FAIL（ImportError: cannot import name 'parse_feature_index_response'）。

- [ ] **Step 2.3: 在 hidpp.py 追加实现**

```python
# ---- 追加到 hidpp.py 末尾 ----

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
```

- [ ] **Step 2.4: 运行全部测试确认通过**

```bash
python -m pytest tests/ -v
```

Expected: Task 1 + Task 2 全部 PASS。

- [ ] **Step 2.5: 提交**

```bash
git add hidpp.py tests/test_hidpp_parse.py
git commit -m "feat: HID++ 电量应答解析与锂电电压换算"
```

---

### Task 3: devices.py — 设备发现与电量查询

**Files:**
- Create: `devices.py`
- Create: `tests/fakes.py`（FakeHid 测试替身）
- Create: `tests/test_devices.py`
- Modify: `cli.py`（真机验证脚本）

- [ ] **Step 3.1: 写测试替身 tests/fakes.py**

```python
"""hidapi 测试替身：按「完整请求帧 → 应答 payload」脚本化响应。"""


class FakeDevice:
    def __init__(self, responses=None, fail_open=False):
        self.responses = responses or {}   # {完整请求帧 bytes: 应答 payload bytes(不含 report id)}
        self.fail_open = fail_open
        self.opened = False
        self.closed = False
        self.last_write = None

    def open_path(self, path):
        if self.fail_open:
            raise OSError("occupied by another process")
        self.opened = True

    def write(self, data):
        self.last_write = bytes(data)
        return len(data)

    def read(self, n, timeout_ms=None):
        req, self.last_write = self.last_write, None
        if req is None:
            return []
        payload = self.responses.get(req)
        if payload is None:
            return []
        return list(bytes([0x10]) + payload)   # 模拟 Windows hidapi 含 report id 的返回

    def close(self):
        self.closed = True


class FakeHid:
    """interfaces: [(info_dict 含 path/usage_page/vendor_id, FakeDevice), ...]"""

    def __init__(self, interfaces):
        self._by_path = {info["path"]: dev for info, dev in interfaces}
        self._infos = [info for info, _ in interfaces]

    def enumerate(self, vendor_id=0, product_id=0):
        return [dict(i) for i in self._infos
                if not vendor_id or i["vendor_id"] == vendor_id]

    def device(self):
        return _LazyDevice(self._by_path)


class _LazyDevice:
    def __init__(self, by_path):
        self._by_path = by_path
        self._target = None

    def open_path(self, path):
        dev = self._by_path.get(path)
        if dev is None:
            raise OSError("no such path")
        dev.open_path(path)
        self._target = dev

    def write(self, data):
        return self._target.write(data)

    def read(self, n, timeout_ms=None):
        return self._target.read(n, timeout_ms)

    def close(self):
        if self._target:
            self._target.close()
```

- [ ] **Step 3.2: 写失败测试 tests/test_devices.py**

```python
"""devices.py 设备发现与电量查询的单元测试（使用 FakeHid）。"""
from devices import DeviceManager, LOGI_VENDOR_ID
from fakes import FakeDevice, FakeHid

# 常用请求帧
REQ_PING_0xFF = bytes.fromhex("10FF0011000000")        # ping 直连
REQ_PING_SLOT2 = bytes.fromhex("10020011000000")       # ping slot 2
REQ_FEATURE_0xFF = bytes.fromhex("10FF0001100000")     # 0xFF 查询 0x1000
REQ_FEATURE_SLOT2 = bytes.fromhex("10020001100000")    # slot2 查询 0x1000
REQ_BATT_0xFF_I06 = bytes.fromhex("10FF0601000000")    # 0xFF 用 feature index 6 查电量
REQ_BATT_SLOT2_I06 = bytes.fromhex("10020601000000")   # slot2 用 feature index 6 查电量

ERR_NOT_FOUND = bytes([0xFF, 0xFF, 0x02, 0x10, 0x09, 0x00, 0x00])


def make_interface(path, usage_page=0xFF43, dev=None):
    info = {"path": path, "vendor_id": LOGI_VENDOR_ID, "usage_page": usage_page}
    return info, dev or FakeDevice()


class TestDirectDevice:
    def test_found_and_read(self):
        dev = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_0xFF: bytes([0xFF, 0x00, 0x01, 0x06, 0, 0]),
            REQ_BATT_0xFF_I06: bytes([0xFF, 0x06, 0x01, 86, 85, 0x00, 0, 0, 0]),
        })
        mgr = DeviceManager(hidapi=FakeHid([make_interface("\\\\hid#1", dev=dev)]))
        state = mgr.read_battery()
        assert (state.percent, state.charging, state.online) == (86, False, True)

    def test_sleep_keeps_last_percent_and_offline(self):
        dev = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_0xFF: bytes([0xFF, 0x00, 0x01, 0x06, 0, 0]),
            REQ_BATT_0xFF_I06: bytes([0xFF, 0x06, 0x01, 86, 85, 0x00, 0, 0, 0]),
        })
        mgr = DeviceManager(hidapi=FakeHid([make_interface("\\\\hid#1", dev=dev)]))
        assert mgr.read_battery().percent == 86
        dev.responses.pop(REQ_BATT_0xFF_I06)   # 设备进入休眠，不再应答
        state = mgr.read_battery()
        assert (state.percent, state.online) == (86, False)

    def test_receiver_slot_discovery(self):
        # 0xFF ping 有响应但无电量功能（接收器自身），slot 2 上有鼠标
        dev = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_0xFF: ERR_NOT_FOUND,
            REQ_PING_SLOT2: bytes([0x02, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_SLOT2: bytes([0x02, 0x00, 0x01, 0x06, 0, 0]),
            REQ_BATT_SLOT2_I06: bytes([0x02, 0x06, 0x01, 64, 0, 0x01, 0, 0, 0]),
        })
        mgr = DeviceManager(hidapi=FakeHid([make_interface("\\\\hid#recv", 0xFF00, dev)]))
        state = mgr.read_battery()
        assert (state.percent, state.charging, state.online) == (64, True, True)


class TestNoDevice:
    def test_empty_enumerate(self):
        mgr = DeviceManager(hidapi=FakeHid([]))
        state = mgr.read_battery()
        assert (state.percent, state.online) == (None, False)

    def test_open_failure_skips_to_next_interface(self):
        occupied = FakeDevice(fail_open=True)
        good = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_0xFF: bytes([0xFF, 0x00, 0x01, 0x06, 0, 0]),
            REQ_BATT_0xFF_I06: bytes([0xFF, 0x06, 0x01, 86, 85, 0x00, 0, 0, 0]),
        })
        hid = FakeHid([
            make_interface("\\\\hid#busy", dev=occupied),
            make_interface("\\\\hid#ok", dev=good),
        ])
        state = DeviceManager(hidapi=hid).read_battery()
        assert (state.percent, state.online) == (86, False, True)
        assert occupied.closed is False       # 未被成功打开
        assert good.opened is True

    def test_no_battery_feature_is_ignored(self):
        # ping 通但无任何电量 feature 的设备 → 视为无设备
        dev = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_0xFF: ERR_NOT_FOUND,
        })
        mgr = DeviceManager(hidapi=FakeHid([make_interface("\\\\hid#1", dev=dev)]))
        state = mgr.read_battery()
        assert (state.percent, state.online) == (None, False)
```

- [ ] **Step 3.3: 运行测试确认失败**

```bash
python -m pytest tests/test_devices.py -v
```

Expected: FAIL（`ModuleNotFoundError: No module named 'devices'`）。

- [ ] **Step 3.4: 实现 devices.py**

```python
"""设备发现与电量查询（hidapi 交互层）。hidapi 可注入以便测试。

发现策略（不硬编码 PID，天然兼容其他有电量功能的罗技无线设备）：
1. 枚举 VID=0x046D 且 usage page 为罗技厂商页（0xFF00 / 0xFF43）的接口
2. 先探测直连形态（device index 0xFF）
3. 若任一 slot(1-6) 上探测到设备 → 判定为接收器，采用该 slot
4. 未能打开的接口（被 G HUB 等占用）直接跳过
"""
import logging
from dataclasses import dataclass

import hid

from hidpp import (
    BATTERY_FEATURE_IDS, BATTERY_STATUS_ID,
    build_battery_request, build_get_feature, build_ping,
    extract_payload, is_error, parse_battery_status,
    parse_adc_measurement, parse_feature_index_response, SW_ID,
)

log = logging.getLogger("GPW2BatteryShow")

LOGI_VENDOR_ID = 0x046D
DIRECT_INDEX = 0xFF
RECEIVER_SLOTS = tuple(range(1, 7))      # 接收器配对 slot 1-6，离线的静默失败
USAGE_PAGES = (0xFF00, 0xFF43)


@dataclass
class BatteryState:
    percent: int | None = None   # 最后一次成功读取的电量（None=从未读到）
    charging: bool = False
    online: bool = False


class DeviceManager:
    def __init__(self, hidapi=None):
        self._hid = hidapi if hidapi is not None else hid
        self._dev = None
        self._device_index = None
        self._feature_index = None
        self._feature_id = None
        self._last_percent = None

    def read_battery(self) -> BatteryState:
        """顶层入口：按需发现设备并读取电量。失败返回 online=False。"""
        try:
            if self._dev is None:
                self._open_and_detect()
            if self._dev is None:
                return BatteryState(None, False, False)
            resp = self._query(build_battery_request(self._device_index,
                                                     self._feature_index))
            state = self._parse(resp)
            if state is None:
                # 休眠/形态变化：丢弃句柄，下次重新探测；保留已知电量
                self._close()
                return BatteryState(self._last_percent, False, False)
            self._last_percent = state.percent
            return state
        except OSError:
            log.exception("hidapi IO 异常")
            self._close()
            return BatteryState(self._last_percent, False, False)

    # ---- 内部实现 ----

    def _close(self):
        if self._dev is not None:
            try:
                self._dev.close()
            except OSError:
                pass
        self._dev = self._device_index = self._feature_index = None
        self._feature_id = None

    def _open_and_detect(self):
        for info in self._hid.enumerate(vendor_id=LOGI_VENDOR_ID):
            if info.get("usage_page") not in USAGE_PAGES:
                continue
            try:
                dev = self._hid.device()
                dev.open_path(info["path"])
            except (OSError, ValueError):
                log.info("接口被占用，跳过: %s", info.get("path"))
                continue
            direct = self._probe_on(dev, DIRECT_INDEX)
            for slot in RECEIVER_SLOTS:
                probed = self._probe_on(dev, slot)
                if probed is not None:
                    self._bind(dev, slot, probed)
                    log.info("接收器模式: slot=%d feature=%s", slot, probed)
                    return
            if direct is not None:
                self._bind(dev, DIRECT_INDEX, direct)
                log.info("直连模式: feature=%s", direct)
                return
            dev.close()

    def _bind(self, dev, device_index, probed):
        self._dev = dev
        self._device_index = device_index
        self._feature_index, self._feature_id = probed

    def _probe_on(self, dev, index):
        """探测指定 index 上是否有带电量功能的设备 → (feature_index, feature_id) | None"""
        if self._query_on(dev, build_ping(index)) is None:
            return None
        for fid in BATTERY_FEATURE_IDS:
            resp = self._query_on(dev, build_get_feature(index, fid))
            if resp is None or is_error(resp):
                continue
            fidx = parse_feature_index_response(resp)
            if not fidx:
                continue
            resp = self._query_on(dev, build_battery_request(index, fidx))
            if resp is not None and not is_error(resp):
                return fidx, fid
        return None

    def _query(self, request):
        return self._query_on(self._dev, request)

    def _query_on(self, dev, request, timeout_ms=800):
        """发送请求并收取匹配的应答 payload；超时/不匹配返回 None。"""
        try:
            dev.write(request)
        except (OSError, ValueError):
            return None
        for _ in range(3):
            try:
                data = dev.read(32, timeout_ms=timeout_ms // 3)
            except (OSError, ValueError):
                return None
            if not data:
                continue
            payload = extract_payload(data)
            if payload is None or len(payload) < 4:
                continue
            if payload[0] != request[1]:          # 设备索引不匹配
                continue
            if (payload[2] & 0x0F) != SW_ID:      # 软件标识不匹配
                continue
            return payload
        return None

    def _parse(self, resp):
        if resp is None or is_error(resp):
            return None
        parsed = (parse_battery_status(resp)
                  if self._feature_id == BATTERY_STATUS_ID
                  else parse_adc_measurement(resp))
        if parsed is None:
            return None
        percent, charging = parsed
        return BatteryState(percent, charging, True)
```

- [ ] **Step 3.5: 运行全部测试确认通过**

```bash
python -m pytest tests/ -v
```

Expected: Task 1-3 全部 PASS。

- [ ] **Step 3.6: 实现 cli.py 真机验证脚本**

```python
"""真机验证脚本（M1/M2 验收）：python cli.py"""
from devices import DeviceManager

state = DeviceManager().read_battery()
if not state.online:
    print("未检测到设备（可能未连接、休眠或接口被占用）")
elif state.percent is None:
    print("设备在线，但尚未读取到电量")
else:
    tag = "（充电中）" if state.charging else ""
    print(f"电量 {state.percent}%{tag}")
```

- [ ] **Step 3.7: 真机验证（需要 GPW2）**

```bash
python cli.py    # 有线直连场景
python cli.py    # 接收器场景
```

Expected: 打印「电量 NN%」，与鼠标上按 DPI 键查看的指示灯电量、或 G HUB 显示的电量大致相符（±5% 内）。若无设备，检查是否装了 G HUB 占用接口。

- [ ] **Step 3.8: 提交**

```bash
git add devices.py tests/fakes.py tests/test_devices.py cli.py
git commit -m "feat: 设备发现（直连+接收器）与电量查询，含真机验证脚本"
```

---

### Task 4: tray.py — 图标绘制纯函数

**Files:**
- Create: `tray.py`
- Test: `tests/test_tray_icon.py`

- [ ] **Step 4.1: 写失败测试**

```python
"""tray.py 图标绘制纯函数的单元测试。"""
from tray import (
    SIZE, draw_icon, pick_color, read_dark_taskbar,
    COLORS, LIGHT_FG, DARK_FG,
)


def has_color(img, color, tol=8):
    # 文字渲染有抗锯齿，颜色比较留容差；纯色几何图形不受影响
    return any(all(abs(a - b) <= tol for a, b in zip(img.getpixel((x, y)), color))
               for x in range(SIZE) for y in range(SIZE))


class TestPickColor:
    def test_offline_grey(self):
        assert pick_color(None, False, False) == COLORS["offline"]

    def test_charging_cyan(self):
        assert pick_color(30, True, True) == COLORS["charging"]

    def test_critical_red(self):
        assert pick_color(10, False, True) == COLORS["critical"]

    def test_low_yellow(self):
        assert pick_color(20, False, True) == COLORS["low"]

    def test_ok_green(self):
        assert pick_color(86, False, True) == COLORS["ok"]


class TestDrawIcon:
    def test_numeric_has_text_in_color(self):
        img = draw_icon(86, False, True, style="numeric", dark_taskbar=True)
        assert img.size == (SIZE, SIZE)
        assert has_color(img, COLORS["ok"])

    def test_charging_shows_bolt(self):
        img = draw_icon(50, True, True, style="numeric", dark_taskbar=True)
        assert has_color(img, COLORS["charging"])

    def test_offline_grey_question_mark(self):
        img = draw_icon(None, False, False, style="numeric", dark_taskbar=True)
        assert has_color(img, COLORS["offline"])

    def test_simple_style_fills_battery(self):
        img = draw_icon(86, False, True, style="simple", dark_taskbar=True)
        assert has_color(img, COLORS["ok"])

    def test_theme_switches_foreground(self):
        dark = draw_icon(86, False, True, dark_taskbar=True)
        light = draw_icon(86, False, True, dark_taskbar=False)
        assert has_color(dark, LIGHT_FG) and not has_color(light, LIGHT_FG)
        assert has_color(light, DARK_FG) and not has_color(dark, DARK_FG)


class TestReadDarkTaskbar:
    def test_returns_bool(self):
        # 注册表键可能缺失，两种结果都合法，只验证类型与可调用性
        assert isinstance(read_dark_taskbar(), bool)
```

- [ ] **Step 4.2: 运行测试确认失败**

```bash
python -m pytest tests/test_tray_icon.py -v
```

Expected: FAIL（`ModuleNotFoundError: No module named 'tray'`）。

- [ ] **Step 4.3: 实现 tray.py（本任务只实现绘制部分）**

```python
"""托盘 UI 层：图标绘制纯函数 + pystray 组装（组装部分 Task 6 添加）。"""
import winreg
from PIL import Image, ImageDraw, ImageFont

SIZE = 32

COLORS = {
    "ok": (76, 175, 80, 255),        # 绿
    "low": (255, 193, 7, 255),       # 黄
    "critical": (244, 67, 54, 255),  # 红
    "charging": (0, 200, 255, 255),  # 青
    "offline": (158, 158, 158, 255), # 灰
}
LIGHT_FG = (230, 230, 230, 255)      # 深色任务栏上的前景
DARK_FG = (60, 60, 60, 255)          # 浅色任务栏上的前景

_LOW_THRESHOLD = 20
_CRITICAL_THRESHOLD = 10


def pick_color(percent: int | None, charging: bool, online: bool) -> tuple:
    """状态 → 图标主色。"""
    if not online or percent is None:
        return COLORS["offline"]
    if charging:
        return COLORS["charging"]
    if percent <= _CRITICAL_THRESHOLD:
        return COLORS["critical"]
    if percent <= _LOW_THRESHOLD:
        return COLORS["low"]
    return COLORS["ok"]


def read_dark_taskbar() -> bool:
    """读注册表 AppsUseLightTheme 判断任务栏深浅。缺失/异常按 Win11 默认深色处理。"""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as k:
            light = winreg.QueryValueEx(k, "AppsUseLightTheme")[0]
            return not bool(light)
    except OSError:
        return True


def _draw_battery_shell(d: ImageDraw.ImageDraw, fg: tuple):
    """电池轮廓：主体 (3,10)-(26,24) + 右端正极凸起。"""
    d.rounded_rectangle([3, 10, 26, 24], radius=3, outline=fg, width=2)
    d.rectangle([27, 14, 29, 20], fill=fg)


def _draw_bolt(d: ImageDraw.ImageDraw, color: tuple):
    """充电闪电，画在电池上方 (10..19, 1..9)，不与电池区域重叠。"""
    d.polygon([(15, 1), (10, 9), (14, 9), (12, 15), (19, 7), (15, 7), (17, 1)],
              fill=color)


def _draw_text_centered(d: ImageDraw.ImageDraw, box: tuple, text: str,
                        font, fill: tuple):
    x0, y0, x1, y1 = box
    bbox = d.textbbox((0, 0), text, font=font)
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    d.text(((x0 + x1) / 2 - w / 2 - bbox[0], (y0 + y1) / 2 - h / 2 - bbox[1]),
           text, font=font, fill=fill)


def draw_icon(percent: int | None, charging: bool, online: bool,
              style: str = "numeric", dark_taskbar: bool = True) -> Image.Image:
    """状态 → 32×32 RGBA 托盘图标。"""
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    fg = LIGHT_FG if dark_taskbar else DARK_FG
    color = pick_color(percent, charging, online)
    _draw_battery_shell(d, fg)

    font = ImageFont.load_default(size=16)
    if not online or percent is None:
        _draw_text_centered(d, (3, 10, 26, 24), "?", font, COLORS["offline"])
        return img

    if charging:
        _draw_bolt(d, color)

    if style == "numeric":
        _draw_text_centered(d, (3, 10, 26, 24), str(percent), font, color)
    else:  # simple：按百分比填充电池内部
        inner_w = int(19 * min(percent, 100) / 100)
        d.rectangle([6, 13, 6 + inner_w, 21], fill=color)
    return img
```

- [ ] **Step 4.4: 运行全部测试确认通过**

```bash
python -m pytest tests/ -v
```

Expected: Task 1-4 全部 PASS。

- [ ] **Step 4.5: 提交**

```bash
git add tray.py tests/test_tray_icon.py
git commit -m "feat: 托盘电量图标动态绘制（数值/简约/离线/充电/主题自适应）"
```

---

### Task 5: config.py — 配置与开机自启

**Files:**
- Create: `config.py`
- Test: `tests/test_config.py`

- [ ] **Step 5.1: 写失败测试**

```python
"""config.py 配置读写与自启的单元测试（winreg 部分为真机手动验证）。"""
import json
from pathlib import Path

import config


class TestLoadConfig:
    def test_first_run_creates_defaults(self, tmp_path, monkeypatch):
        monkeypatch.setattr(config, "_config_path",
                            lambda: tmp_path / "config.json")
        cfg = config.load_config()
        assert cfg == config.DEFAULTS
        assert (tmp_path / "config.json").exists()

    def test_corrupted_config_backs_up_and_rebuilds(self, tmp_path, monkeypatch):
        p = tmp_path / "config.json"
        p.write_text("{ broken json !!!", encoding="utf-8")
        monkeypatch.setattr(config, "_config_path", lambda: p)
        cfg = config.load_config()
        assert cfg == config.DEFAULTS
        assert p.with_suffix(".json.bak").exists()

    def test_unknown_keys_dropped_known_keys_kept(self, tmp_path, monkeypatch):
        p = tmp_path / "config.json"
        p.write_text(json.dumps({"poll_interval_sec": 30, "hacker": True}),
                     encoding="utf-8")
        monkeypatch.setattr(config, "_config_path", lambda: p)
        cfg = config.load_config()
        assert cfg["poll_interval_sec"] == 30
        assert "hacker" not in cfg


class TestSaveConfig:
    def test_roundtrip(self, tmp_path, monkeypatch):
        monkeypatch.setattr(config, "_config_path",
                            lambda: tmp_path / "config.json")
        cfg = config.load_config()
        cfg["low_battery_threshold"] = 15
        config.save_config(cfg)
        assert json.loads((tmp_path / "config.json").read_text("utf-8"))["low_battery_threshold"] == 15


class TestAutoStart:
    def test_get_set_roundtrip_real_registry(self):
        # 读写真实 HKCU Run 键（用户级，无管理员需求）；测试后清理
        original = config.get_auto_start()
        try:
            assert config.set_auto_start(True) is True
            assert config.get_auto_start() is True
            assert config.set_auto_start(False) is True
            assert config.get_auto_start() is False
        finally:
            config.set_auto_start(original)
```

- [ ] **Step 5.2: 运行测试确认失败**

```bash
python -m pytest tests/test_config.py -v
```

Expected: FAIL（`ModuleNotFoundError: No module named 'config'`）。

- [ ] **Step 5.3: 实现 config.py**

```python
"""配置读写（config.json）与开机自启（HKCU Run 注册表键）。"""
import json
import logging
import os
import shutil
import sys
from pathlib import Path
import winreg

log = logging.getLogger("GPW2BatteryShow")

APP_NAME = "GPW2BatteryShow"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"

DEFAULTS = {
    "poll_interval_sec": 60,
    "low_battery_threshold": 20,
    "icon_style": "numeric",     # numeric | simple
    "auto_start": False,
    "log_level": "INFO",
}


def config_dir() -> Path:
    """打包环境用 %LOCALAPPDATA%\\GPW2BatteryShow，开发环境用项目目录。"""
    if getattr(sys, "frozen", False):
        return Path(os.environ["LOCALAPPDATA"]) / APP_NAME
    return Path(__file__).resolve().parent


def _config_path() -> Path:
    return config_dir() / "config.json"


def load_config() -> dict:
    """加载配置。首次运行创建默认；损坏时备份为 .bak 并重建默认。"""
    p = _config_path()
    if not p.exists():
        cfg = dict(DEFAULTS)
        save_config(cfg)
        return cfg
    try:
        raw = json.loads(p.read_text("utf-8"))
        if not isinstance(raw, dict):
            raise ValueError("config must be a JSON object")
    except (ValueError, OSError):
        shutil.copy2(p, p.with_suffix(".json.bak"))
        log.warning("配置文件损坏，已备份为 config.json.bak 并重建默认配置")
        cfg = dict(DEFAULTS)
        save_config(cfg)
        return cfg
    cfg = dict(DEFAULTS)
    cfg.update({k: v for k, v in raw.items() if k in DEFAULTS})
    return cfg


def save_config(cfg: dict) -> None:
    p = _config_path()
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), "utf-8")
    os.replace(tmp, p)   # 原子替换，避免写一半损坏


def _exe_command() -> str:
    """写进 Run 键的启动命令。打包后是 exe 自身；开发态用 pythonw 跑 main.py。"""
    if getattr(sys, "frozen", False):
        return f'"{sys.executable}"'
    pythonw = sys.executable.replace("python.exe", "pythonw.exe")
    main_py = Path(__file__).resolve().parent / "main.py"
    return f'"{pythonw}" "{main_py}"'


def get_auto_start() -> bool:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_READ) as k:
            winreg.QueryValueEx(k, APP_NAME)
            return True
    except OSError:
        return False


def set_auto_start(enable: bool) -> bool:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
            if enable:
                winreg.SetValueEx(k, APP_NAME, 0, winreg.REG_SZ, _exe_command())
            else:
                try:
                    winreg.DeleteValue(k, APP_NAME)
                except FileNotFoundError:
                    pass
        return True
    except OSError:
        log.exception("设置开机自启失败")
        return False
```

- [ ] **Step 5.4: 运行全部测试确认通过**

```bash
python -m pytest tests/ -v
```

Expected: Task 1-5 全部 PASS。

- [ ] **Step 5.5: 提交**

```bash
git add config.py tests/test_config.py
git commit -m "feat: 配置读写（损坏自愈）与开机自启注册表"
```

---

### Task 6: tray.py 组装 + main.py — 托盘应用与轮询

**Files:**
- Modify: `tray.py`（追加 TrayApp）
- Create: `main.py`

- [ ] **Step 6.1: 在 tray.py 追加 TrayApp 类**

```python
# ---- 追加到 tray.py 末尾 ----
import threading
import pystray

from config import get_auto_start, save_config, set_auto_start
from devices import BatteryState


class TrayApp:
    """pystray 托盘应用：图标、tooltip、右键菜单、气泡通知。"""

    def __init__(self, cfg: dict):
        self.cfg = cfg
        self._state = BatteryState()
        self.icon = pystray.Icon(
            "GPW2BatteryShow",
            icon=draw_icon(None, False, False, cfg["icon_style"]),
            title="GPW2 电量显示",
            menu=self._build_menu(),
        )

    # ---- 菜单 ----
    def _build_menu(self) -> pystray.Menu:
        return pystray.Menu(
            pystray.MenuItem("立即刷新", self._refresh, default=True),
            pystray.MenuItem(
                "数值图标", self._set_numeric, radio=True,
                checked=lambda item: self.cfg["icon_style"] == "numeric"),
            pystray.MenuItem(
                "简约图标", self._set_simple, radio=True,
                checked=lambda item: self.cfg["icon_style"] == "simple"),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("开机自启", self._toggle_autostart,
                             checked=lambda item: get_auto_start()),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("退出", self._quit),
        )

    def _refresh(self, icon, item):
        # 直接同步读一次并刷新（hid 查询耗时约 0.1-0.8s，可接受）
        from devices import DeviceManager
        self.update(DeviceManager().read_battery())

    def _set_numeric(self, icon, item):
        self._set_style("numeric")

    def _set_simple(self, icon, item):
        self._set_style("simple")

    def _set_style(self, style: str):
        self.cfg["icon_style"] = style
        save_config(self.cfg)
        self.update(self._state)

    def _toggle_autostart(self, icon, item):
        self.cfg["auto_start"] = not get_auto_start()
        set_auto_start(self.cfg["auto_start"])
        save_config(self.cfg)

    def _quit(self, icon, item):
        icon.stop()

    # ---- 由轮询线程调用 ----
    def update(self, state: BatteryState):
        """线程安全更新图标与 tooltip（pystray 内部调度到 UI 线程）。"""
        self._state = state
        self.icon.icon = draw_icon(state.percent, state.charging, state.online,
                                   self.cfg["icon_style"], read_dark_taskbar())
        if state.percent is None:
            self.icon.title = "未检测到设备"
        elif not state.online:
            self.icon.title = f"GPW2 · {state.percent}%（休眠/离线）"
        elif state.charging:
            self.icon.title = f"GPW2 · {state.percent}%（充电中）"
        else:
            self.icon.title = f"GPW2 · {state.percent}%"

    def notify_low_battery(self, percent: int):
        self.icon.notify(f"鼠标电量仅剩 {percent}%，请及时充电", "GPW2 电量提醒")

    def run(self):
        self.icon.run()   # 必须在主线程调用
```

- [ ] **Step 6.2: 写 main.py**

```python
"""GPW2 Battery Show 入口：配置 → 轮询线程 → 托盘主循环。"""
import logging
import threading
import time
from logging.handlers import RotatingFileHandler

from config import config_dir, load_config
from devices import DeviceManager
from tray import TrayApp


def _setup_logging(cfg: dict):
    handler = RotatingFileHandler(config_dir() / "log.txt",
                                  maxBytes=1_000_000, backupCount=1,
                                  encoding="utf-8")
    logging.basicConfig(
        level=getattr(logging, cfg["log_level"].upper(), logging.INFO),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
        handlers=[handler])


def poll_loop(app: TrayApp, manager: DeviceManager, cfg: dict):
    """后台轮询：离线时 10s 快速重试 3 次后回落常规间隔（防协议压力）。"""
    fail_streak = 0
    notified = False
    last_ok_percent = None
    while True:
        state = manager.read_battery()
        fail_streak = 0 if state.online else fail_streak + 1

        if state.online and state.percent is not None:
            from notifier import should_notify, should_reset_notified
            if should_notify(last_ok_percent, state.percent, notified,
                             cfg["low_battery_threshold"]):
                app.notify_low_battery(state.percent)
                notified = True
            if should_reset_notified(state.percent,
                                     cfg["low_battery_threshold"], notified):
                notified = False
            last_ok_percent = state.percent

        app.update(state)
        interval = (cfg["poll_interval_sec"]
                    if state.online or fail_streak > 3 else 10)
        time.sleep(interval)


def main():
    cfg = load_config()
    _setup_logging(cfg)
    app = TrayApp(cfg)
    threading.Thread(target=poll_loop, args=(app, DeviceManager(), cfg),
                     daemon=True).start()
    app.run()


if __name__ == "__main__":
    main()
```

- [ ] **Step 6.3: 手动验收（无 UI 自动化，YAGNI）**

```bash
python main.py
```

Expected checklist:
- 托盘出现电池图标，显示当前电量数字
- 鼠标插上充电线 → 图标变青色 + 闪电
- tooltip 显示「GPW2 · NN%（充电中）」
- 右键菜单：立即刷新可用；图标样式切换生效；退出正常

- [ ] **Step 6.4: 提交**

```bash
git add tray.py main.py
git commit -m "feat: 托盘应用组装与轮询状态机"
```

---

### Task 7: notifier.py — 低电量通知判定

**Files:**
- Create: `notifier.py`
- Test: `tests/test_notifier.py`

- [ ] **Step 7.1: 写失败测试**

```python
"""notifier.py 低电量通知判定的单元测试。"""
from notifier import should_notify, should_reset_notified


class TestShouldNotify:
    def test_first_reading_below_threshold(self):
        assert should_notify(None, 15, False, 20) is True

    def test_first_reading_above_threshold(self):
        assert should_notify(None, 85, False, 20) is False

    def test_crossing_down_triggers(self):
        assert should_notify(86, 18, False, 20) is True

    def test_already_below_no_repeat(self):
        assert should_notify(15, 12, True, 20) is False

    def test_repeated_below_threshold(self):
        assert should_notify(18, 12, False, 20) is True

    def test_staying_above_no_notify(self):
        assert should_notify(50, 40, False, 20) is False


class TestShouldResetNotified:
    def test_recovered_above_threshold_plus_five(self):
        assert should_reset_notified(26, 20, True) is True

    def test_still_below_threshold(self):
        assert should_reset_notified(15, 20, True) is False

    def test_not_notified_yet(self):
        assert should_reset_notified(26, 20, False) is False
```

- [ ] **Step 7.2: 运行测试确认失败**

```bash
python -m pytest tests/test_notifier.py -v
```

Expected: FAIL（`ModuleNotFoundError: No module named 'notifier'`）。

- [ ] **Step 7.3: 实现 notifier.py**

```python
"""低电量通知判定纯函数。防骚扰策略：跌破阈值只通知一次，
电量回升到 阈值+5 以上后重置，允许下次再次通知。"""


def should_notify(prev: int | None, now: int, already_notified: bool,
                  threshold: int) -> bool:
    """prev=None 表示本次是首次读到电量。"""
    if already_notified:
        return False
    if now > threshold:
        return False
    return prev is None or prev > threshold


def should_reset_notified(now: int, threshold: int,
                          already_notified: bool) -> bool:
    return already_notified and now > threshold + 5
```

- [ ] **Step 7.4: 运行全部测试确认通过**

```bash
python -m pytest tests/ -v
```

Expected: Task 1-7 全部 PASS（main.py 中对 notifier 的 import 此时已可用）。

- [ ] **Step 7.5: 手动验收通知**

```bash
# 临时把阈值改大触发
python - <<'EOF'
import config
cfg = config.load_config()
cfg["low_battery_threshold"] = 100
config.save_config(cfg)
EOF
python main.py   # 轮询到电量后应弹出「电量不足」气泡；确认后再把阈值改回 20
python - <<'EOF'
import config
cfg = config.load_config()
cfg["low_battery_threshold"] = 20
config.save_config(cfg)
EOF
```

Expected: 首次轮询即弹气泡一次，且不再重复弹出。

- [ ] **Step 7.6: 提交**

```bash
git add notifier.py tests/test_notifier.py
git commit -m "feat: 低电量通知判定（防骚扰重置机制）"
```

---

### Task 8: 打包与 README

**Files:**
- Create: `README.md`

- [ ] **Step 8.1: 安装 PyInstaller 并打包**

```bash
pip install pyinstaller
pyinstaller --onefile --noconsole --name GPW2BatteryShow --collect-all hid main.py
```

Expected: 生成 `dist/GPW2BatteryShow.exe`，无报错。

- [ ] **Step 8.2: 真机验证 exe**

```bash
./dist/GPW2BatteryShow.exe &
sleep 5
```

Expected: 托盘出现图标且显示电量；`tasklist | grep -i GPW2` 可见进程。

- [ ] **Step 8.3: 写 README.md**

```markdown
# GPW2 Battery Show

罗技 G Pro X Superlight 2（GPW2）鼠标电量 Windows 11 托盘显示工具。
直接通过 HID++ 协议读取电量，无需罗技 G HUB / Logi Options+。

## 功能

- 托盘图标实时显示电量百分比（数值 / 简约两种样式）
- 充电状态、离线/休眠状态识别，明暗主题自适应
- 电量 ≤20% 弹出系统通知（只提醒一次，充满后重置）
- 可选开机自启，配置文件可调轮询间隔与阈值
- 支持有线 USB-C 直连与 LIGHTSPEED 接收器两种连接方式

## 使用

1. 下载 `dist/GPW2BatteryShow.exe`（或自行构建），双击运行
2. 托盘右键可切换图标样式、开启开机自启

## 配置

`%LOCALAPPDATA%\GPW2BatteryShow\config.json`

| 键 | 默认 | 说明 |
|---|---|---|
| poll_interval_sec | 60 | 轮询间隔（秒），过小会干扰鼠标省电休眠 |
| low_battery_threshold | 20 | 低电量通知阈值（%） |
| icon_style | numeric | 图标样式：numeric / simple |
| auto_start | false | 开机自启（也可在托盘菜单切换） |
| log_level | INFO | 日志级别 |

## 开发

```bash
python -m venv .venv && source .venv/Scripts/activate
pip install -r requirements.txt
python cli.py                      # 真机读一次电量
python -m pytest tests/ -v         # 单元测试
pyinstaller --onefile --noconsole --name GPW2BatteryShow --collect-all hid main.py
```

## 已知限制

- 原生协议电量读数与 G HUB 显示可能有几个百分点差异
  （原生用通用锂电曲线，G HUB 用设备专属查找表）
- G HUB 运行时可能占用设备接口导致读不到，请退出 G HUB 重试
- 不支持蓝牙连接（GPW2 无蓝牙）

## 参考

协议实现参考了 [Solaar](https://github.com/pwr-Solaar/Solaar) 与
[LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery) 的公开实现，感谢原作者。
```

- [ ] **Step 8.4: 提交**

```bash
git add README.md
git commit -m "docs: README 使用与开发说明"
```

---

### Task 9: 最终真机验收

**Files:** 无新增（验收清单执行）

- [ ] **Step 9.1: 跑全部单元测试**

```bash
python -m pytest tests/ -v
```

Expected: 全部 PASS，0 failed。

- [ ] **Step 9.2: 执行手动验收清单（对照设计文档第 8 节）**

| # | 场景 | 预期 |
|---|---|---|
| 1 | 有线直连读数 vs G HUB | 差异 ≤5% |
| 2 | 接收器模式读数 vs G HUB | 差异 ≤5% |
| 3 | 插充电线 | 图标变青+闪电，tooltip「充电中」 |
| 4 | 拔接收器 | 图标灰化「?」，tooltip「休眠/离线」 |
| 5 | 鼠标休眠后唤醒 | 60s 内自动恢复读数 |
| 6 | 阈值调 100 | 只弹一次通知 |
| 7 | 勾选自启 → 重启 | 自动出现在托盘 |
| 8 | 手动改坏 config.json 再启动 | 自动重建默认并留 .bak |

- [ ] **Step 9.3: 记录验收结果**

在 README「已知限制」下方追加「验收记录」小节（日期 + 各场景 PASS/FAIL），提交：

```bash
git add README.md
git commit -m "docs: 记录真机验收结果"
```

---

## 任务依赖图

```
Task 0 → Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 6 → Task 7 → Task 8 → Task 9
                                    (cli 真机验证)              (main 引用 notifier，T7 补齐)
```

注意：Task 6 的 `main.py` 引用了 Task 7 的 `notifier`，因此 **Task 6 与 Task 7 必须连续执行**（Task 7 Step 7.4 之后程序才完整可运行；若分开执行，Task 6 结束时程序无法启动属预期）。
