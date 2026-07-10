from bms_sim import protocol as proto
from bms_sim import server
from bms_sim.device import Device


class FakePort:
    def __init__(self, to_read: bytes = b""):
        self._queue = [to_read] if to_read else []
        self.written = b""

    def read(self, size: int) -> bytes:
        if self._queue:
            return self._queue.pop(0)
        return b""

    def write(self, data: bytes) -> None:
        self.written += data


def test_process_available_returns_zero_when_nothing_to_read():
    port = FakePort()
    decoder = proto.FrameDecoder("request")
    count = server.process_available(port, Device(), decoder)
    assert count == 0
    assert port.written == b""


def test_process_available_responds_to_basic_info_request():
    port = FakePort(bytes.fromhex("DDA50300FFFD77"))
    decoder = proto.FrameDecoder("request")
    device = Device()
    count = server.process_available(port, device, decoder)
    assert count == 1
    decoded = proto.decode_response(port.written)
    assert decoded.register == proto.REG_BASIC_INFO
    assert decoded.status == proto.STATUS_OK


def test_process_available_handles_split_frame_across_two_reads():
    raw = bytes.fromhex("DDA50300FFFD77")
    port = FakePort()
    port._queue = [raw[:3], raw[3:]]
    decoder = proto.FrameDecoder("request")
    device = Device()
    assert server.process_available(port, device, decoder) == 0
    assert server.process_available(port, device, decoder) == 1
    assert port.written[:1] == bytes([proto.START_BYTE])
