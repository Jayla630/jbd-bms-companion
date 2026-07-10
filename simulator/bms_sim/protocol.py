"""JBD SP04S010 串口协议：帧结构、校验算法、编解码。

事实来源：docs/阶段0_JBD-SP04S010_协议参考三表.md
"""
from __future__ import annotations

from dataclasses import dataclass
from datetime import date

START_BYTE = 0xDD
END_BYTE = 0x77

OP_READ = 0xA5
OP_WRITE = 0x5A

REG_ENTER_CONFIG = 0x00
REG_EXIT_SAVE = 0x01
REG_BASIC_INFO = 0x03
REG_CELL_VOLTAGES = 0x04
REG_DEVICE_NAME = 0x05
REG_ERROR_COUNT = 0xAA
REG_MOS_CONTROL = 0xE1
REG_BALANCE_CONTROL = 0xE2

STATUS_OK = 0x00
STATUS_ERROR = 0x80

ENTER_CONFIG_MAGIC = 0x5678


@dataclass
class RequestFrame:
    op: int
    register: int
    data: bytes


@dataclass
class ResponseFrame:
    register: int
    status: int
    data: bytes


def _checksum(second_header_byte: int, length: int, data: bytes) -> int:
    """请求帧和响应帧校验的公共部分：校验覆盖"第二个头字节 + 长度 + 数据"，
    不含 0xDD/0x77，也不含第一个头字节（请求的操作码 / 响应里没有对应字段）。
    """
    total = second_header_byte + length + sum(data)
    return (0x10000 - total) & 0xFFFF


def checksum_request(register: int, length: int, data: bytes) -> int:
    return _checksum(register, length, data)


def checksum_response(status: int, length: int, data: bytes) -> int:
    return _checksum(status, length, data)


def encode_request(op: int, register: int, data: bytes = b"") -> bytes:
    length = len(data)
    checksum = checksum_request(register, length, data)
    body = bytes([op, register, length]) + data + checksum.to_bytes(2, "big")
    return bytes([START_BYTE]) + body + bytes([END_BYTE])


def encode_response(register: int, status: int, data: bytes = b"") -> bytes:
    length = len(data)
    checksum = checksum_response(status, length, data)
    body = bytes([register, status, length]) + data + checksum.to_bytes(2, "big")
    return bytes([START_BYTE]) + body + bytes([END_BYTE])


class FrameDecoder:
    """流式帧解码器。喂任意大小的字节块，吐出完整帧列表。

    处理粘包（一次喂了多帧）/半包（一帧被拆成多次喂）；遇到校验失败或结尾不是
    0x77 的"假帧"，跳过 1 个字节重新找下一个 0xDD，不会卡死或整体丢弃缓冲区。
    """

    def __init__(self, kind: str):
        if kind not in ("request", "response"):
            raise ValueError("kind 必须是 'request' 或 'response'")
        self._kind = kind
        self._buf = bytearray()

    def feed(self, data: bytes) -> list:
        self._buf.extend(data)
        frames = []
        while True:
            frame = self._try_extract_one()
            if frame is None:
                break
            frames.append(frame)
        return frames

    def _try_extract_one(self):
        buf = self._buf
        while True:
            while buf and buf[0] != START_BYTE:
                del buf[0]
            if len(buf) < 4:
                return None
            length = buf[3]
            total = 7 + length  # 起始(1)+头(3)+数据(N)+校验(2)+结束(1)
            if len(buf) < total:
                return None
            candidate = bytes(buf[:total])
            if candidate[-1] != END_BYTE:
                del buf[0]
                continue
            b0, b1 = candidate[1], candidate[2]
            data = candidate[4 : 4 + length]
            checksum = (candidate[4 + length] << 8) | candidate[4 + length + 1]
            expected = _checksum(b1, length, data)
            if checksum != expected:
                del buf[0]
                continue
            del buf[:total]
            if self._kind == "request":
                return RequestFrame(op=b0, register=b1, data=bytes(data))
            return ResponseFrame(register=b0, status=b1, data=bytes(data))


def decode_request(frame_bytes: bytes) -> RequestFrame:
    frames = FrameDecoder("request").feed(frame_bytes)
    if not frames:
        raise ValueError("无法解析为合法请求帧")
    return frames[0]


def decode_response(frame_bytes: bytes) -> ResponseFrame:
    frames = FrameDecoder("response").feed(frame_bytes)
    if not frames:
        raise ValueError("无法解析为合法响应帧")
    return frames[0]


@dataclass
class BasicInfo:
    total_voltage_v: float
    current_a: float
    remaining_capacity_ah: float
    nominal_capacity_ah: float
    cycles: int
    production_date: date
    balance_low: int
    balance_high: int
    protection_status: int
    software_version: str
    soc_percent: int
    mos_charge_on: bool
    mos_discharge_on: bool
    cell_count: int
    ntc_count: int
    temperatures_c: list


def parse_basic_info(data: bytes) -> BasicInfo:
    if len(data) < 23:
        raise ValueError("基础信息数据长度不足 23 字节")

    total_voltage_raw = int.from_bytes(data[0:2], "big")
    current_raw = int.from_bytes(data[2:4], "big", signed=True)
    remaining_capacity_raw = int.from_bytes(data[4:6], "big")
    nominal_capacity_raw = int.from_bytes(data[6:8], "big")
    cycles = int.from_bytes(data[8:10], "big")
    date_raw = int.from_bytes(data[10:12], "big")
    balance_low = int.from_bytes(data[12:14], "big")
    balance_high = int.from_bytes(data[14:16], "big")
    protection_status = int.from_bytes(data[16:18], "big")
    sw_version_raw = data[18]
    soc_percent = data[19]
    mos_raw = data[20]
    cell_count = data[21]
    ntc_count = data[22]

    temperatures_c = []
    offset = 23
    for _ in range(ntc_count):
        raw = int.from_bytes(data[offset : offset + 2], "big")
        temperatures_c.append(round((raw - 2731) / 10, 1))
        offset += 2

    return BasicInfo(
        total_voltage_v=total_voltage_raw / 100,
        current_a=current_raw / 100,
        remaining_capacity_ah=remaining_capacity_raw / 100,
        nominal_capacity_ah=nominal_capacity_raw / 100,
        cycles=cycles,
        production_date=date(2000 + (date_raw >> 9), (date_raw >> 5) & 0xF, date_raw & 0x1F),
        balance_low=balance_low,
        balance_high=balance_high,
        protection_status=protection_status,
        software_version=f"{sw_version_raw >> 4}.{sw_version_raw & 0xF}",
        soc_percent=soc_percent,
        mos_charge_on=bool(mos_raw & 0x01),
        mos_discharge_on=bool(mos_raw & 0x02),
        cell_count=cell_count,
        ntc_count=ntc_count,
        temperatures_c=temperatures_c,
    )


def encode_basic_info(info: BasicInfo) -> bytes:
    date_raw = (
        ((info.production_date.year - 2000) << 9)
        | (info.production_date.month << 5)
        | info.production_date.day
    )
    sw_major, sw_minor = (int(x) for x in info.software_version.split("."))
    sw_raw = (sw_major << 4) | sw_minor
    mos_raw = (0x01 if info.mos_charge_on else 0) | (0x02 if info.mos_discharge_on else 0)

    parts = [
        round(info.total_voltage_v * 100).to_bytes(2, "big"),
        round(info.current_a * 100).to_bytes(2, "big", signed=True),
        round(info.remaining_capacity_ah * 100).to_bytes(2, "big"),
        round(info.nominal_capacity_ah * 100).to_bytes(2, "big"),
        info.cycles.to_bytes(2, "big"),
        date_raw.to_bytes(2, "big"),
        info.balance_low.to_bytes(2, "big"),
        info.balance_high.to_bytes(2, "big"),
        info.protection_status.to_bytes(2, "big"),
        bytes([sw_raw]),
        bytes([info.soc_percent]),
        bytes([mos_raw]),
        bytes([info.cell_count]),
        bytes([info.ntc_count]),
    ]
    for temp_c in info.temperatures_c:
        raw = round(temp_c * 10) + 2731
        parts.append(raw.to_bytes(2, "big"))
    return b"".join(parts)


def parse_cell_voltages(data: bytes) -> list:
    return [int.from_bytes(data[i : i + 2], "big") for i in range(0, len(data), 2)]


def encode_cell_voltages(voltages_mv: list) -> bytes:
    return b"".join(v.to_bytes(2, "big") for v in voltages_mv)
