import io

from bms_sim import cli
from bms_sim import protocol as proto
from bms_sim import server
from bms_sim.device import Device
from bms_sim.faults import ProtectionBit


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


def _read_basic_info(device: Device) -> proto.BasicInfo:
    request = proto.decode_request(proto.encode_request(proto.OP_READ, proto.REG_BASIC_INFO))
    response = proto.decode_response(device.handle_request(request))
    return proto.parse_basic_info(response.data)


def test_console_command_reflects_in_serial_response():
    # server 与 console 共享同一 Device：控制台改 SOC 后，串口读 0x03 立刻读到新值
    device = Device()
    assert _read_basic_info(device).soc_percent != 80
    cli.handle_command(device, "soc 80")
    assert _read_basic_info(device).soc_percent == 80


def test_console_loop_shares_device_and_exits_on_eof():
    device = Device()
    stream = io.StringIO("fault inject cell_overvoltage\n\nhelp\n")
    server.console_loop(device, stream)  # 流耗尽（EOF）应自然返回，不抛异常
    assert device.faults.is_set(ProtectionBit.CELL_OVERVOLTAGE)
    info = _read_basic_info(device)
    assert info.protection_status & (1 << ProtectionBit.CELL_OVERVOLTAGE)


def test_console_loop_quits_on_quit_and_survives_bad_command():
    device = Device()
    stream = io.StringIO("fault inject not_a_bit\nquit\nsoc 5\n")
    server.console_loop(device, stream)  # 坏命令不炸；quit 后停止处理后续行
    assert _read_basic_info(device).soc_percent != 5
