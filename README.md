# GPW2 Battery Show · GPW2 电量托盘

**中文** | [English](README.en.md)

在 Windows 11 任务栏托盘实时显示罗技 G Pro X Superlight 2（GPW2）鼠标电量。
通过原生 HID++ 协议直读设备，**无需罗技 G HUB / Logi Options+**。

C# + HidSharp + WinForms 实现，主程序仅 ~29KB。

## 功能

- 任务栏托盘常驻图标，实时显示电量百分比（数值 / 默认电池两种样式，明暗主题自适应）
- 左键单击托盘图标：弹出电量详情卡片（电量、连接方式、充电状态、进度条）
- 右键菜单：立即刷新（强制重探测）、切换样式、开机自启、控制面板、退出
- 充电状态识别（绿色）：支持有线直连充电与**外置充电头无线充电**两种场景
- 低电量 / 中电量 / 正常三级配色（红 / 黄 / 白），低于阈值弹系统通知（只提醒一次）
- 有线 USB-C 直连与 LIGHTSPEED 接收器两种连接方式**自动识别**，插拔秒级响应（热插拔事件驱动）
- 设备休眠 / 离线自动灰化，恢复后自动重新探测

## 使用

1. 双击 `dist/GPW2BatteryShow.exe`（需与 `HidSharp.dll` 同目录）
2. 左键托盘图标查看详情；右键菜单进入控制面板

## 构建

需要 Windows（系统自带 .NET Framework 4.8 的 csc.exe，**无需安装任何 SDK**）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物：`dist/GPW2BatteryShow.exe` + `HidSharp.dll`。

## 配置

`%LOCALAPPDATA%\GPW2BatteryShow\config.json`

| 键 | 默认 | 说明 |
|---|---|---|
| poll_interval_sec | 60 | 轮询间隔（秒），过小会干扰鼠标省电休眠 |
| low_battery_threshold | 20 | 低电量提醒阈值（%），同时决定红色配色分界 |
| icon_style | numeric | 托盘图标样式：numeric / simple |
| popup_on_click | true | 左键单击托盘图标时是否展开电量卡片 |

## 项目结构

```
src/
├── Program.cs            # 入口：单实例互斥、全局异常兜底
├── TrayContext.cs        # 托盘上下文：菜单/定时轮询/热插拔事件/通知
├── BatteryPopup.cs       # 左键电量详情卡片
├── ControlPanel.cs       # 控制面板
├── LogitechHidpp.cs      # HID++ 2.0 协议纯逻辑层（帧构造/应答解析）
├── Gpw2Device.cs         # 设备发现与电量查询（HidSharp 多 collection 读写）
├── TrayIconRenderer.cs   # GDI+ 托盘图标动态绘制
├── AppSettings.cs        # 配置读写 + 开机自启注册表
└── Logger.cs             # 滚动日志
tools/LogiDiag.cs         # 真机协议诊断工具（独立编译）
lib/HidSharp.dll          # HidSharp 2.x（Apache 2.0）
```

## 实现要点（真机验证过的协议行为）

罗技设备使用专有 HID++ 协议，跑在标准 USB HID 报文上（短报文 0x10 / 长报文 0x11）。本项目在真机调试中确认了以下关键行为：

1. **电量读取**：GPW2 支持 UnifiedBattery feature（ID `0x1004`）。`func 0` 是能力描述（不是电量！），**`func 1` 才返回 SOC 百分比、充电状态与外接电源标志**——读错 function 会导致电量完全不准
2. **多 collection**：接收器（PID 0xC547）MI_02 接口暴露 Col01/Col02 两个 collection，GPW2（协议 4.2）的长报文应答**只会出现在 Col02 的读队列**，写入则走 Col01
3. **否定应答**：对接收器自身或空 slot 发请求，接收器代回 HID++ 1.0 错误帧（`0x8F`）而非静默超时，可用于快速判定离线
4. **有线模式怪癖**：充电线插电脑后鼠标切换为直连设备（PID 0xC09B），且对 `0xFF` 与全部 slot 1-6 都应答，探测时必须直连优先
5. **固件延迟**：电量查询的应答固定延迟 ~800ms（有线/无线一致），为设备固件特性，客户端无法优化；软件侧通过"探测即读数 + feature index 缓存"将重探测压缩到 ~0.9s

## 已知限制

- 非充电状态下电量配色基于锂电电压换算，与 G HUB 的设备专属查找表可能有 ±5% 差异
- G HUB 与本工具并发通信偶发查询超时，软件已做内部容错
- 不支持蓝牙（GPW2 无蓝牙）

## 诊断

如遇识别异常，运行 `dist/LogiDiag.exe`（源码 `tools/LogiDiag.cs`），把输出发给开发者即可；
应用日志位于 `%LOCALAPPDATA%\GPW2BatteryShow\log.txt`。

## 致谢

- [HidSharp](https://github.com/SeekHisKingdom/HIDSharp)（Apache 2.0）— 跨平台 .NET HID 库
- [device-battery-tray](https://github.com/linzifeng07/device-battery-tray) — 托盘 UI 与控制面板形态参考
- [OpenLogi](https://github.com/AprilNEA/OpenLogi) — UnifiedBattery func 语义与 feature 优先级参考
- [Solaar](https://github.com/pwr-Solaar/Solaar)、[LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery) — HID++ 协议实现参考
