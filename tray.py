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
    """充电闪电，画在电池上方 (10..19, 1..15)，不与电池内部数字重叠区域冲突。"""
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
