# GPW2 Battery Show

罗技 G Pro X Superlight 2（GPW2）鼠标电量 Windows 11 托盘显示工具。
直接通过 HID++ 协议读取电量，无需罗技 G HUB / Logi Options+。

**C# + HidSharp + WinForms 实现**，exe 仅 ~29KB。

## 功能

- 任务栏托盘常驻图标，实时显示电量百分比（数值 / 简约两种样式）
- 左键托盘图标：弹出电量详情卡片（电量、连接方式、充电状态、进度条）
- 右键菜单：立即刷新、切换图标样式、开机自启、控制面板、退出
- 充电状态识别（青色）、离线/休眠状态识别（灰色问号）、明暗主题自适应
- 电量 ≤20% 弹出 Windows 气泡通知（只提醒一次，充满后重置）
- 控制面板可调：轮询间隔、低电量阈值、图标样式、开机自启
- 支持有线 USB-C 直连与 LIGHTSPEED 接收器两种连接方式，状态变化自动重新探测

## 使用

1. 下载 `dist/GPW2BatteryShow.exe`（或自行构建），双击运行
2. 左键托盘图标查看详情，右键菜单进入控制面板

## 构建

需要 Windows（自带 .NET Framework 4.8 的 csc.exe，无需安装任何 SDK）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物：`dist/GPW2BatteryShow.exe` + `HidSharp.dll`（需与 exe 同目录）。

## 配置

`%LOCALAPPDATA%\GPW2BatteryShow\config.json`

| 键 | 默认 | 说明 |
|---|---|---|
| poll_interval_sec | 60 | 轮询间隔（秒），过小会干扰鼠标省电休眠 |
| low_battery_threshold | 20 | 低电量提醒阈值（%） |
| icon_style | numeric | 托盘图标样式：numeric / simple |

## 已知限制

- 原生协议电量读数与 G HUB 可能有 ±5% 差异（原生用通用锂电曲线换算电压，
  G HUB 用设备专属查找表）
- G HUB 与本工具并发通信时偶发查询超时，已做内部重试，但极端情况下
  单轮读数可能延迟
- 不支持蓝牙连接（GPW2 无蓝牙）

## 实现说明（真机验证过的协议行为）

- 罗技 HID++ 2.0 协议跑在标准 HID 报文上：短报文 0x10 / 长报文 0x11
- 接收器（C547）MI_02 接口暴露 Col01/Col02 两个 collection：
  GPW2（协议 4.2）的长报文应答只会出现在 Col02 的读队列
- 对空 slot 或接收器自身发请求，接收器代回 HID++ 1.0 错误帧（0x8F）而非静默超时
- GPW2 对 feature 0x1000（电量状态）声称支持但返回全 0 无效帧，
  需回退 feature 0x1004（ADC 电压）按锂电曲线换算百分比

## 参考

- [HidSharp](https://github.com/SeekHisKingdom/HIDSharp)（Apache 2.0）：跨平台 .NET HID 库
- [device-battery-tray](https://github.com/linzifeng07/device-battery-tray)：托盘 UI 与控制面板形态参考
- [Solaar](https://github.com/pwr-Solaar/Solaar)、[LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery)：HID++ 协议实现参考
