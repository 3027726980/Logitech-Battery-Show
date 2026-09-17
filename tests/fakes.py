"""hidapi 测试替身：按「完整请求帧 → 应答 payload」脚本化响应。"""


class FakeDevice:
    def __init__(self, responses=None, fail_open=False):
        # 注意不能用 `responses or {}`：空 dict 是 falsy，会创建新对象、
        # 断开与调用方后续填充的共享引用
        self.responses = {} if responses is None else responses
        self.fail_open = fail_open
        self.opened = False
        self.closed = False
        self.last_write = None

    def open_path(self, path):
        if self.fail_open:
            raise OSError("occupied by another process")
        self.opened = True

    def write(self, data):
        self.last_write = bytes(data)
        return len(data)

    def read(self, n, timeout_ms=None):
        req, self.last_write = self.last_write, None
        if req is None:
            return []
        payload = self.responses.get(req)
        if payload is None:
            return []
        return list(bytes([0x10]) + payload)   # 模拟 Windows hidapi 含 report id 的返回

    def close(self):
        self.closed = True


class FakeHid:
    """interfaces: [(info_dict 含 path/usage_page/vendor_id, FakeDevice), ...]"""

    def __init__(self, interfaces):
        self._by_path = {info["path"]: dev for info, dev in interfaces}
        self._infos = [info for info, _ in interfaces]

    def enumerate(self, vendor_id=0, product_id=0):
        return [dict(i) for i in self._infos
                if not vendor_id or i["vendor_id"] == vendor_id]

    def device(self):
        return _LazyDevice(self._by_path)


class _LazyDevice:
    def __init__(self, by_path):
        self._by_path = by_path
        self._target = None

    def open_path(self, path):
        dev = self._by_path.get(path)
        if dev is None:
            raise OSError("no such path")
        dev.open_path(path)
        self._target = dev

    def write(self, data):
        return self._target.write(data)

    def read(self, n, timeout_ms=None):
        return self._target.read(n, timeout_ms)

    def close(self):
        if self._target:
            self._target.close()


# --- 多 collection / 长报文支持（真机 C547 + GPW2 协议 4.2 实测驱动） ---


class FakeDeviceV2(FakeDevice):
    """支持：write 失败模拟（只读 collection）、完整应答帧（含 report id）。"""

    def __init__(self, responses=None, fail_open=False, fail_write=False):
        super().__init__(responses, fail_open)
        self.fail_write = fail_write

    def write(self, data):
        if self.fail_write:
            raise OSError("collection has no output reports")
        return super().write(data)

    def read(self, n, timeout_ms=None):
        req, self.last_write = self.last_write, None
        if req is None:
            return []
        value = self.responses.get(req)
        if value is None:
            return []
        if value and value[0] in (0x10, 0x11):
            return list(value)                      # 完整应答帧，原样返回
        return list(bytes([0x10]) + value)          # 旧格式：裸 payload
