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
