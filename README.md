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

`%LOCALAPPDATA%\GPW2BatteryShow\config.json`（开发态为项目目录下 `config.json`）

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
