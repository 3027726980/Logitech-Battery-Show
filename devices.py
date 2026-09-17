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
    extract_payload, is_error, is_hidpp1_error, parse_battery_status,
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
        self._handles = []          # 同一接口的多个 collection 句柄（见下）
        self._device_index = None
        self._feature_index = None
        self._feature_id = None
        self._last_percent = None

    def read_battery(self) -> BatteryState:
        """顶层入口：按需发现设备并读取电量。失败返回 online=False。"""
        try:
            if not self._handles:
                self._open_and_detect()
            if not self._handles:
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
        for h in self._handles:
            try:
                h.close()
            except OSError:
                pass
        self._handles = []
        self._device_index = self._feature_index = None
        self._feature_id = None

    def _open_and_detect(self):
        """按 interface_number 分组打开全部 collection。

        真机实测（C547 接收器 + GPW2 协议 4.2）：同一接口暴露 Col01/Col02
        两个 collection，短报文走 Col01，长报文应答只会出现在 Col02 的读队列，
        因此 write 选可写句柄，read 必须轮询组内全部句柄。
        """
        groups = {}
        for info in self._hid.enumerate(vendor_id=LOGI_VENDOR_ID):
            if info.get("usage_page") not in USAGE_PAGES:
                continue
            groups.setdefault(info.get("interface_number"), []).append(info)
        for infos in groups.values():
            handles = []
            for info in infos:
                try:
                    dev = self._hid.device()
                    dev.open_path(info["path"])
                    handles.append(dev)
                except (OSError, ValueError):
                    log.info("接口被占用，跳过: %s", info.get("path"))
            if not handles:
                continue
            direct = self._probe_on(handles, DIRECT_INDEX)
            for slot in RECEIVER_SLOTS:
                probed = self._probe_on(handles, slot)
                if probed is not None:
                    self._bind(handles, slot, probed)
                    log.info("接收器模式: slot=%d feature=%s", slot, probed)
                    return
            if direct is not None:
                self._bind(handles, DIRECT_INDEX, direct)
                log.info("直连模式: feature=%s", direct)
                return
            for h in handles:
                try:
                    h.close()
                except OSError:
                    pass

    def _bind(self, handles, device_index, probed):
        self._handles = handles
        self._device_index = device_index
        self._feature_index, self._feature_id = probed

    def _probe_on(self, handles, index):
        """探测指定 index 上是否有带电量功能的设备 → (feature_index, feature_id) | None

        注意：ping 无应答或收到 0x8F 错误帧（真机实测空 slot 行为）都视为离线。
        """
        ping_resp = self._query_on(handles, build_ping(index), timeout_ms=400)
        if ping_resp is None or is_hidpp1_error(ping_resp):
            return None
        for fid in BATTERY_FEATURE_IDS:
            resp = self._query_on(handles, build_get_feature(index, fid), timeout_ms=400)
            if resp is None or is_error(resp):
                continue
            fidx = parse_feature_index_response(resp)
            if not fidx:
                continue
            resp = self._query_on(handles, build_battery_request(index, fidx), timeout_ms=400)
            if resp is not None and not is_error(resp):
                return fidx, fid
        return None

    def _query(self, request):
        return self._query_on(self._handles, request)

    def _query_on(self, handles, request, timeout_ms=800):
        """发送请求并收取匹配的应答 payload；超时/不匹配返回 None。"""
        for h in handles:
            try:
                if h.write(request) > 0:
                    break
            except (OSError, ValueError):
                continue
        else:
            return None                      # 所有句柄都写不进去（只读）
        for _ in range(3):
            for h in handles:
                try:
                    data = h.read(32, timeout_ms=timeout_ms // 3)
                except (OSError, ValueError):
                    continue
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
