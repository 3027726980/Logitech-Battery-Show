# GPW2 电池托盘显示应用 · 设计文档

- 日期：2026-09-17
- 状态：待用户审阅
- 目标平台：Windows 11（兼容 Windows 10 1809+）
- 参考调研：LGSTrayBattery（C#/.NET 8，GitHub）、Solaar（Python，HID++ 协议权威实现，Gitee 有镜像）、cvuchener/hidpp（C++ 协议库）

## 1. 目标与非目标

### 目标
- 在 Windows 11 系统托盘常驻图标，实时显示罗技 G Pro X Superlight 2（GPW2）鼠标电量百分比与充电状态
- 不依赖 G HUB / Logi Options+，直接通过 USB HID 使用罗技 HID++ 协议读取
- 单 exe 双击即用，资源占用低，界面极简
- 支持 GPW2 的两种连接形态：有线 USB-C 直连、LIGHTSPEED USB 接收器

### 非目标（第一版明确不做）
- 不做 GUI 设置窗口（用配置文件 + 托盘右键菜单覆盖高频选项）
- 不做多设备管理面板（但实现上天然兼容其他有电量功能的罗技无线鼠标）
- 不做蓝牙支持（GPW2 无蓝牙）
- 不做历史曲线、数据导出等附加功能（YAGNI）

## 2. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 语言 | Python 3.12 | 生态可移植 Solaar 协议逻辑，开发最快 |
| HID 通信 | `hidapi`（pip 包名 `hid`） | 封装 Windows hid.dll，免驱动，支持热插拔枚举 |
| 托盘 | `pystray` | 纯 Python（ctypes 实现），支持动态图标/菜单/气泡通知 |
| 图标绘制 | `Pillow` | 动态生成电量位图 |
| 打包 | PyInstaller（`--onefile --noconsole`） | 产出单 exe |

运行时依赖仅 3 个第三方包；标准库使用 `threading`、`json`、`logging`、`winreg`、`ctypes`。

## 3. 协议层设计（核心）

### 3.1 HID++ 基础
- 罗技 HID++ 跑在标准 HID Output/Input Report 上：
  - 短报文：Report ID `0x10`，7 字节负载
  - 长报文：Report ID `0x11`，20 字节负载
- 请求结构：`[ReportID, DeviceIndex, FeatureIndex, FuncSwId, Params...]`
- **Feature Index 是设备内动态分配的**：feature ID（如电量 `0x1000`）≠ feature index。需先经 Root feature（ID `0x0000`，func 0）查询 `0x1000` 在该设备上被分配到的 index，再向该 index 发请求
- 设备在线确认：Root 的 Ping（func 1），返回 payload 首字节含随机数回显

### 3.2 电量查询
1. 优先 Feature `0x1000` Battery Status（func 0）：返回电量百分比 0-100、status（0=放电、1=充电、2=接近充满等）
2. 若设备不支持 `0x1000`，回退 Feature `0x1004` ADC Measurement：返回电压 mV，按 3.7V 锂电曲线换算百分比（与 G HUB 设备专属查找表可能有几个百分点偏差，tooltip 中不作区分）
3. 两者都无 → 视为"无电量设备"，不显示

### 3.3 设备发现与形态区分
流程（不硬编码 PID，天然兼容多设备）：
1. `hid.enumerate()` 过滤 `VID = 0x046D`（Logitech），取 usage page 为厂商自定义页的接口（候选 `0xFF00` / `0xFF43`，以真机实测响应为准）
2. 对每个候选句柄，用设备索引 `0xFF` 尝试 Root Ping：
   - 响应者 → 直连设备（GPW2 有线模式即此形态）
   - 无响应/行为符合接收器特征 → 判定为 LIGHTSPEED 接收器：对 slot 1-6 逐个 Ping（HID++ 1.0 兼容方式，不在线的 slot 会静默失败，无副作用），在线 slot 继续走 2.0 feature 探测与电量查询
3. 汇总所有"能查到电量"的设备为设备列表

### 3.4 轮询与状态机
- 常规轮询间隔：默认 60 秒（可配置）。**不能太频繁，否则干扰鼠标省电休眠**（LGSTrayBattery 已知问题）
- 设备离线/休眠时：以 10 秒间隔快速重试 3 次，仍失败则回落常规间隔；图标灰化，tooltip 标注「休眠/离线」，保留最后一次已知电量
- 第一版纯轮询，不实现设备主动上报（HID++ 电量状态变化通知），保持简单

## 4. UI 层设计

### 4.1 托盘图标（Pillow 动态绘制）
- 画布 32×32（交给系统缩放至托盘尺寸），样式二选一：
  - 数值模式（默认）：电池轮廓 + 大号百分比数字，绿色=正常、黄色=低电量、红色=极低、闪烁闪电=充电中、灰色=离线
  - 简约模式：电池填充条
- 主题自适应：读注册表 `HKCU\...\Themes\Personalize\AppsUseLightTheme`，浅色主题深色前景、深色主题浅色前景；每次轮询顺带检查主题变化
- tooltip：「GPW2 · 电量 86%（充电中）」/「未检测到设备」

### 4.2 托盘右键菜单
- 立即刷新
- 数值图标 / 简约图标（勾选切换）
- 开机自启（勾选切换）
- 退出

### 4.3 低电量通知
- 阈值默认 20%（可配置）
- 触发逻辑：电量从 >阈值 跌至 ≤阈值 时弹一次气泡通知（用 `pystray.Icon.notify()` 的 balloon 实现，零额外依赖）；电量回升超过 阈值+5 后重置触发标志，避免反复骚扰

## 5. 架构与模块

```
main.py       入口：加载配置 → 起后台轮询线程 → 主线程 icon.run()
hidpp.py      纯协议层：帧构造/解析、feature 探测、电量查询（可完全单测）
devices.py    设备层：hidapi 枚举、直连/接收器形态区分、句柄生命周期
tray.py       UI 层：图标绘制、右键菜单、tooltip、气泡通知
config.py     配置加载/保存（损坏时备份重建默认值）
```

- 数据流：轮询线程 → `devices.get_battery()` → `(percent, charging, state)` → 绘制图标 → `icon.icon` 属性更新（pystray 线程安全）
- 模块边界：`hidpp.py` 不 import 任何 UI/IO 库（仅 bytes 逻辑）；`devices.py` 只依赖 `hid`；`tray.py` 只依赖 `pystray/Pillow`

## 6. 配置与存储

`config.json` 位置：打包环境 `%LOCALAPPDATA%\GPW2BatteryShow\config.json`，开发环境用项目目录。

```json
{
  "poll_interval_sec": 60,
  "low_battery_threshold": 20,
  "icon_style": "numeric",
  "auto_start": false,
  "log_level": "INFO"
}
```

- 日志：同目录 `log.txt`，滚动保留 1 份；全局异常钩子写入日志并气泡提示一次
- 开机自启：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写 exe 绝对路径（PyInstaller frozen 时取 `sys.executable`）

## 7. 错误处理

| 场景 | 行为 |
|---|---|
| 无任何罗技设备 | 灰色图标 + tooltip「未检测到设备」，按常规间隔静默重试 |
| 设备休眠查询超时 | 保留已知电量、图标灰化，快速重试 3 次后回落 |
| 配置损坏 | 备份原文件为 `config.json.bak`，重建默认配置 |
| 协议异常帧/CRC | 丢弃该次结果，计入连续失败，连续失败后按离线处理 |
| 程序重复启动 | 不加锁（托盘顶多多一个图标，影响极小；留作后续可选） |

## 8. 测试策略

- **单元测试**（`hidpp.py` 全覆盖）：帧构造、Root/Ping/Feature 探测、电量应答解析、异常帧，参考 Solaar 的真实协议数据做固定用例
- **手动验收清单**：有线直连读数 vs G HUB 读数对照；接收器模式同；充电中状态；拔接收器；设备休眠后唤醒；低电量通知只弹一次；自启勾选后重启生效
- 图标绘制为纯函数，可用固定状态断言输出像素特征（低优先级）

## 9. 里程碑

1. **M1 协议打通**：`hidpp.py` + 命令行脚本，有线模式读出电量（TDD：协议层先测后码）
2. **M2 接收器模式**：接收器枚举 + 电量查询
3. **M3 托盘 UI**：动态图标 + 菜单 + tooltip
4. **M4 完整功能**：低电量通知、开机自启、配置
5. **M5 发布**：PyInstaller 单 exe + README 使用说明

## 10. 风险与对策

| 风险 | 对策 |
|---|---|
| 接收器 slot 枚举行为与预期不符 | 采用 Solaar 已验证的 slot ping 方案；M2 阶段优先真机验证 |
| GPW2 休眠时查询无响应 | 已设计为预期行为（离线状态机），非缺陷 |
| `hid` 包 Windows wheel 兼容性 | hidapi wheel 自带 DLL 无外部依赖；打包时 `--collect-all hid` 兜底 |
| 主题检测注册表缺失（策略机） | 读取失败时默认深色前景，不影响主功能 |
