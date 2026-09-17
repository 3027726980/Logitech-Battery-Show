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
