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
