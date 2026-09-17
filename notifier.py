"""低电量通知判定纯函数。防骚扰策略：跌破阈值只通知一次，
电量回升到 阈值+5 以上后重置，允许下次再次通知。"""


def should_notify(prev: int | None, now: int, already_notified: bool,
                  threshold: int) -> bool:
    """当前电量 ≤ 阈值且尚未通知过 → 通知。prev 参数保留给调用方日志用。

    防重复完全由 already_notified 标志负责；首读即在阈值下方也触发
    （覆盖应用启动时鼠标已低电量的场景）。
    """
    return not already_notified and now <= threshold


def should_reset_notified(now: int, threshold: int,
                          already_notified: bool) -> bool:
    return already_notified and now > threshold + 5
