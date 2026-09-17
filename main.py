"""GPW2 Battery Show 入口：配置 → 轮询线程 → 托盘主循环。"""
import logging
import threading
import time
from logging.handlers import RotatingFileHandler

from config import config_dir, load_config
from devices import DeviceManager
from notifier import should_notify, should_reset_notified
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
