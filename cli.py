"""真机验证脚本（M1/M2 验收）：python cli.py"""
from devices import DeviceManager

state = DeviceManager().read_battery()
if not state.online:
    print("未检测到设备（可能未连接、休眠或接口被占用）")
elif state.percent is None:
    print("设备在线，但尚未读取到电量")
else:
    tag = "（充电中）" if state.charging else ""
    print(f"电量 {state.percent}%{tag}")
