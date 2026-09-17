"""notifier.py 低电量通知判定的单元测试。"""
from notifier import should_notify, should_reset_notified


class TestShouldNotify:
    def test_first_reading_below_threshold(self):
        assert should_notify(None, 15, False, 20) is True

    def test_first_reading_above_threshold(self):
        assert should_notify(None, 85, False, 20) is False

    def test_crossing_down_triggers(self):
        assert should_notify(86, 18, False, 20) is True

    def test_already_below_no_repeat(self):
        assert should_notify(15, 12, True, 20) is False

    def test_repeated_below_threshold(self):
        assert should_notify(18, 12, False, 20) is True

    def test_staying_above_no_notify(self):
        assert should_notify(50, 40, False, 20) is False


class TestShouldResetNotified:
    def test_recovered_above_threshold_plus_five(self):
        assert should_reset_notified(26, 20, True) is True

    def test_still_below_threshold(self):
        assert should_reset_notified(15, 20, True) is False

    def test_not_notified_yet(self):
        assert should_reset_notified(26, 20, False) is False
