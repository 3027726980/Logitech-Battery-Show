"""hidpp.py 帧构造与报文识别的单元测试。"""
import pytest

from hidpp import (
    build_short, build_ping, build_get_feature, build_battery_request,
    extract_payload, is_error, is_hidpp1_error, SW_ID,
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


class TestHidpp1Error:
    """真机实测（C547 LIGHTSPEED 接收器）：空 slot / 不支持请求会代回 0x8F 错误帧。"""

    def test_hidpp1_error_detected(self):
        # [ReportID剥除后] payload[1]=0x8F, payload[4]=错误码
        assert is_hidpp1_error(bytes([0x02, 0x8F, 0x00, 0x11, 0x08, 0x00])) is True

    def test_normal_ping_not_error(self):
        assert is_hidpp1_error(bytes([0xFF, 0x00, 0x11, 0x55, 0x00, 0x00])) is False

    def test_error_frame_also_detected_by_is_error(self):
        # 2.0 错误帧与 1.0 错误帧互不误判
        assert is_error(bytes([0x02, 0x8F, 0x00, 0x11, 0x08, 0x00])) is False
