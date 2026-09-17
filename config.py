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
