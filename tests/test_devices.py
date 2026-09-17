"""devices.py 设备发现与电量查询的单元测试（使用 FakeHid）。"""
from devices import DeviceManager, LOGI_VENDOR_ID
from fakes import FakeDevice, FakeDeviceV2, FakeHid

# 常用请求帧
REQ_PING_0xFF = bytes.fromhex("10FF0011000000")        # ping 直连
REQ_PING_SLOT1 = bytes.fromhex("10010011000000")       # ping slot 1
REQ_PING_SLOT2 = bytes.fromhex("10020011000000")       # ping slot 2
REQ_PING_SLOT3 = bytes.fromhex("10030011000000")       # ping slot 3
REQ_FEATURE_0xFF = bytes.fromhex("10FF0001100000")     # 0xFF 查询 0x1000
REQ_FEATURE_SLOT1 = bytes.fromhex("10010001100000")    # slot1 查询 0x1000
REQ_FEATURE_SLOT2 = bytes.fromhex("10020001100000")    # slot2 查询 0x1000
REQ_BATT_SLOT1_I06 = bytes.fromhex("10010601000000")   # slot1 用 feature index 6 查电量
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
            REQ_BATT_0xFF_I06: bytes([0xFF, 0x06, 0x01, 86, 85, 0x00]),
        })
        mgr = DeviceManager(hidapi=FakeHid([make_interface("\\\\hid#1", dev=dev)]))
        state = mgr.read_battery()
        assert (state.percent, state.charging, state.online) == (86, False, True)

    def test_sleep_keeps_last_percent_and_offline(self):
        dev = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x00, 0x11, 0x55, 0, 0]),
            REQ_FEATURE_0xFF: bytes([0xFF, 0x00, 0x01, 0x06, 0, 0]),
            REQ_BATT_0xFF_I06: bytes([0xFF, 0x06, 0x01, 86, 85, 0x00]),
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
            REQ_BATT_SLOT2_I06: bytes([0x02, 0x06, 0x01, 64, 0, 0x01]),
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
            REQ_BATT_0xFF_I06: bytes([0xFF, 0x06, 0x01, 86, 85, 0x00]),
        })
        hid = FakeHid([
            make_interface("\\\\hid#busy", dev=occupied),
            make_interface("\\\\hid#ok", dev=good),
        ])
        state = DeviceManager(hidapi=hid).read_battery()
        assert (state.percent, state.charging, state.online) == (86, False, True)
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


class TestRealWorldReceiverBehavior:
    """基于 C547 LIGHTSPEED 接收器真机实测的回归用例。"""

    def test_empty_slot_hidpp1_error_frame_not_online(self):
        # 真机实测：空 slot 的 ping 会收到 0x8F 错误帧（而非静默超时），
        # 必须判定为离线，绝不能绑定到空 slot
        dev = FakeDevice(responses={
            REQ_PING_0xFF: bytes([0xFF, 0x8F, 0x00, 0x11, 0x01, 0x00]),
            REQ_PING_SLOT2: bytes([0x02, 0x8F, 0x00, 0x11, 0x08, 0x00]),
            REQ_PING_SLOT3: bytes([0x03, 0x8F, 0x00, 0x11, 0x08, 0x00]),
        })
        mgr = DeviceManager(hidapi=FakeHid([make_interface("\\hid#recv", 0xFF00, dev)]))
        state = mgr.read_battery()
        assert (state.percent, state.online) == (None, False)


class TestMultiCollectionLongReports:
    """GPW2（协议 4.2）经 LIGHTSPEED 接收器以长报文 (0x11) 应答；
    接收器 MI_02 暴露 Col01(可写)/Col02(只读) 两个 collection，
    write 走 Col01，read 必须轮询全部句柄。"""

    def make_receiver(self):
        # Col01 与 Col02 共享同一应答表；Col02 模拟只读
        responses = {}
        col1 = FakeDeviceV2(responses)
        col2 = FakeDeviceV2(responses, fail_write=True)
        hid = FakeHid([
            make_interface("\\hid#recv&Col01", 0xFF00, col1),
            make_interface("\\hid#recv&Col02", 0xFF00, col2),
        ])
        # 两个 collection 的 interface_number 相同，需视为一组
        return hid, responses

    def test_long_report_answer_discovered_and_read(self):
        # 注意：make_interface 的 path 不同 → interface_number 缺省 None，
        # DeviceManager 需按 path 中的 Col 分组或按 interface_number 分组；
        # 此用例先验证"长帧应答可被解析"
        hid, responses = self.make_receiver()
        long_ping_answer = bytes([0x11]) + bytes(
            [0x01, 0x00, 0x11, 0x04, 0x02] + [0x00] * 14)          # 19 字节 payload
        long_feature_answer = bytes([0x11]) + bytes(
            [0x01, 0x00, 0x01, 0x06] + [0x00] * 15)
        long_battery_answer = bytes([0x11]) + bytes(
            [0x01, 0x06, 0x01, 76, 0, 0x00] + [0x00] * 13)
        from test_devices import REQ_PING_SLOT1, REQ_FEATURE_SLOT1, REQ_BATT_SLOT1_I06
        responses[REQ_PING_SLOT1] = long_ping_answer
        responses[REQ_FEATURE_SLOT1] = long_feature_answer
        responses[REQ_BATT_SLOT1_I06] = long_battery_answer
        state = DeviceManager(hidapi=hid).read_battery()
        assert (state.percent, state.charging, state.online) == (76, False, True)

    def test_same_interface_collections_grouped(self):
        # Col01/Col02 共享 interface_number=2 → 必须作为一组句柄同时打开，
        # ping 应答（从任一句柄读出）都能被发现
        hid, responses = self.make_receiver()
        infos = hid._infos
        infos[0]["interface_number"] = 2
        infos[1]["interface_number"] = 2
        long_ping_answer = bytes([0x11]) + bytes(
            [0x01, 0x00, 0x11, 0x04, 0x02] + [0x00] * 14)
        from test_devices import REQ_PING_SLOT1
        responses[REQ_PING_SLOT1] = long_ping_answer
        # 只发 ping 探测：mock 电量 feature 不存在 → 不绑定但不应误判接收器自身
        state = DeviceManager(hidapi=hid).read_battery()
        assert state.online is False   # 无电量 feature，不绑定
