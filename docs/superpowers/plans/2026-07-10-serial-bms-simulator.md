# 串口 BMS 模拟器 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `simulator/` 下实现一个可通过虚拟串口对与真实上位机通信的 JBD SP04S010 协议从站模拟器，带库仑计数电池模型、故障注入、MOS 软锁定状态机、场景回放。

**Architecture:** `bms_sim/protocol.py`（帧编解码+校验，纯函数/无状态类）→ `bms_sim/battery.py` + `bms_sim/faults.py`（电池/温度/保护状态的仿真模型，不依赖串口）→ `bms_sim/device.py`（把协议命令映射到模型读写，唯一知道"协议语义"的胶水层）→ `bms_sim/server.py`（真实串口 I/O）+ `bms_sim/cli.py`（REPL 与场景回放，操作 `Device` 实例）。每层单向依赖下一层，`device.py` 之下的三层完全不 import `serial`，可以脱离硬件跑 pytest。

**Tech Stack:** Python 3.11+ / pyserial / pyyaml / pytest，纯标准库做仿真数学。

**依据文档：**
- 协议事实来源：`docs/阶段0_JBD-SP04S010_协议参考三表.md`
- 设计文档：`docs/superpowers/specs/2026-07-10-serial-bms-simulator-design.md`

---

## 文件结构总览

```
simulator/
  pyproject.toml            # pytest 配置
  requirements.txt          # pyserial, pyyaml
  requirements-dev.txt      # + pytest
  bms_sim/
    __init__.py
    protocol.py             # 帧结构、校验、编解码、FrameDecoder
    battery.py               # OCV 曲线、Cell、BatteryPack、ThermalModel
    faults.py                # ProtectionBit、保护阈值常量、MosController、FaultManager
    device.py                 # Device：命令分发 + 模型胶水
    server.py                 # 真实串口收发主循环
    cli.py                     # REPL + 场景回放
    scenarios/
      demo.yaml
  tests/
    test_protocol.py
    test_battery.py
    test_faults.py
    test_device.py
    test_server.py
    test_cli.py
  README.md
```

全部命令假定当前目录为 `simulator/`（即本仓库当前工作目录）。测试统一用 `python -m pytest ...`（保证 `bms_sim` 包能被找到，不用装包）。

---

### Task 0: 项目脚手架

**Files:**
- Create: `simulator/bms_sim/__init__.py`
- Create: `simulator/requirements.txt`
- Create: `simulator/requirements-dev.txt`
- Create: `simulator/pyproject.toml`

- [ ] **Step 1: 创建包目录和空 `__init__.py`**

```python
# simulator/bms_sim/__init__.py
```

（空文件即可，只是让 `bms_sim` 成为一个包。）

- [ ] **Step 2: 写依赖文件**

```
# simulator/requirements.txt
pyserial>=3.5
pyyaml>=6.0
```

```
# simulator/requirements-dev.txt
-r requirements.txt
pytest>=7.4
```

- [ ] **Step 3: 写 pytest 配置**

```toml
# simulator/pyproject.toml
[tool.pytest.ini_options]
testpaths = ["tests"]
```

- [ ] **Step 4: 安装依赖**

Run: `pip install -r requirements-dev.txt`
Expected: 安装成功，无报错（若环境已有这些包会直接跳过）。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/__init__.py requirements.txt requirements-dev.txt pyproject.toml
git commit -m "chore: 初始化 simulator 项目脚手架"
```

---

### Task 1: protocol.py — 校验算法 + 帧编解码 + FrameDecoder（黄金向量）

**Files:**
- Create: `simulator/bms_sim/protocol.py`
- Test: `simulator/tests/test_protocol.py`

- [ ] **Step 1: 写失败测试（校验算法 + 请求/响应帧 round-trip，黄金向量）**

```python
# simulator/tests/test_protocol.py
from bms_sim import protocol as proto


def test_checksum_request_golden_vector():
    # DD A5 03 00 FF FD 77 : 寄存器=0x03，长度=0x00，无数据
    assert proto.checksum_request(register=0x03, length=0x00, data=b"") == 0xFFFD


def test_checksum_response_golden_vector():
    data = bytes.fromhex(
        "060B0000" "01ED01F4" "00002C7C" "00000000"
        "1000" "80" "63" "02" "04" "03"
        "0BA0" "0B9D" "0B98"
    )
    assert len(data) == 0x1D
    assert proto.checksum_response(status=0x00, length=0x1D, data=data) == 0xFA55


def test_encode_request_matches_golden_bytes():
    frame = proto.encode_request(proto.OP_READ, proto.REG_BASIC_INFO)
    assert frame == bytes.fromhex("DDA50300FFFD77")


def test_decode_request_golden_vector():
    frame = proto.decode_request(bytes.fromhex("DDA50300FFFD77"))
    assert frame.op == proto.OP_READ
    assert frame.register == proto.REG_BASIC_INFO
    assert frame.data == b""


def test_decode_response_golden_vector():
    raw = bytes.fromhex(
        "DD" "03" "00" "1D"
        "060B0000" "01ED01F4" "00002C7C" "00000000"
        "1000" "80" "63" "02" "04" "03"
        "0BA0" "0B9D" "0B98"
        "FA55" "77"
    )
    frame = proto.decode_response(raw)
    assert frame.register == proto.REG_BASIC_INFO
    assert frame.status == proto.STATUS_OK
    assert len(frame.data) == 0x1D


def test_frame_decoder_handles_split_packets():
    raw = bytes.fromhex("DDA50300FFFD77")
    decoder = proto.FrameDecoder("request")
    frames = decoder.feed(raw[:3])
    assert frames == []
    frames = decoder.feed(raw[3:])
    assert len(frames) == 1
    assert frames[0].register == proto.REG_BASIC_INFO


def test_frame_decoder_handles_concatenated_frames():
    single = bytes.fromhex("DDA50300FFFD77")
    decoder = proto.FrameDecoder("request")
    frames = decoder.feed(single + single)
    assert len(frames) == 2


def test_frame_decoder_resyncs_after_garbage():
    # 前两个字节是噪声；接着一个假的 0xDD 起始帧，长度字段=0x00 使其"凑得出"7字节但
    # 结尾不是 0x77，会被识别为假帧丢弃，而不是死等更多字节；随后能正确找到真帧。
    garbage = bytes([0x01, 0x02]) + bytes([0xDD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
    good = bytes.fromhex("DDA50300FFFD77")
    decoder = proto.FrameDecoder("request")
    frames = decoder.feed(garbage + good)
    assert len(frames) == 1
    assert frames[0].register == proto.REG_BASIC_INFO
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_protocol.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'bms_sim.protocol'` 或 `ImportError`。

- [ ] **Step 3: 实现 protocol.py（校验+帧结构+FrameDecoder 部分）**

```python
# simulator/bms_sim/protocol.py
"""JBD SP04S010 串口协议：帧结构、校验算法、编解码。

事实来源：docs/阶段0_JBD-SP04S010_协议参考三表.md
"""
from __future__ import annotations

from dataclasses import dataclass

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
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_protocol.py -v`
Expected: 全部 PASS（8 个用例）。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/protocol.py tests/test_protocol.py
git commit -m "feat(protocol): 帧结构、校验算法与流式解码器，黄金向量测试通过"
```

---

### Task 2: protocol.py — 基础信息/单体电压/设备名称 字段编解码

**Files:**
- Modify: `simulator/bms_sim/protocol.py`
- Test: `simulator/tests/test_protocol.py`

- [ ] **Step 1: 追加失败测试（0x03 逐字段解读的黄金向量断言 + round-trip）**

```python
# 追加到 simulator/tests/test_protocol.py
from datetime import date


GOLDEN_BASIC_INFO_DATA = bytes.fromhex(
    "060B0000" "01ED01F4" "00002C7C" "00000000"
    "1000" "80" "63" "02" "04" "03"
    "0BA0" "0B9D" "0B98"
)


def test_parse_basic_info_golden_vector():
    info = proto.parse_basic_info(GOLDEN_BASIC_INFO_DATA)
    assert info.total_voltage_v == 15.47
    assert info.current_a == 0.00
    assert info.remaining_capacity_ah == 4.93
    assert info.nominal_capacity_ah == 5.00
    assert info.cycles == 0
    assert info.production_date == date(2022, 3, 28)
    assert info.protection_status == 0x1000
    assert info.software_version == "8.0"
    assert info.soc_percent == 99
    assert info.mos_charge_on is False
    assert info.mos_discharge_on is True
    assert info.cell_count == 4
    assert info.ntc_count == 3
    assert info.temperatures_c == [24.5, 24.2, 23.7]


def test_encode_basic_info_round_trip():
    info = proto.parse_basic_info(GOLDEN_BASIC_INFO_DATA)
    assert proto.encode_basic_info(info) == GOLDEN_BASIC_INFO_DATA


def test_cell_voltages_round_trip():
    voltages_mv = [3450, 3460, 3440, 3455]
    data = proto.encode_cell_voltages(voltages_mv)
    assert data == bytes.fromhex("0D7A0D840D700D7F")
    assert proto.parse_cell_voltages(data) == voltages_mv


def test_device_name_response_is_ascii():
    frame = proto.encode_response(proto.REG_DEVICE_NAME, proto.STATUS_OK, b"JBD-SP04S010-Sim")
    decoded = proto.decode_response(frame)
    assert decoded.data.decode("ascii") == "JBD-SP04S010-Sim"
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_protocol.py -v`
Expected: 新增的 4 个用例 FAIL（`AttributeError: module 'bms_sim.protocol' has no attribute 'parse_basic_info'`），此前 8 个仍 PASS。

- [ ] **Step 3: 实现字段编解码，追加到 protocol.py**

```python
# 追加到 simulator/bms_sim/protocol.py（文件顶部 import 需要加 date）
from datetime import date


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
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_protocol.py -v`
Expected: 全部 12 个用例 PASS。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/protocol.py tests/test_protocol.py
git commit -m "feat(protocol): 基础信息/单体电压/设备名称 字段编解码，黄金字段值验证通过"
```

---

### Task 3: battery.py — OCV 曲线 + Cell + BatteryPack（库仑计数）

**Files:**
- Create: `simulator/bms_sim/battery.py`
- Test: `simulator/tests/test_battery.py`

- [ ] **Step 1: 写失败测试**

```python
# simulator/tests/test_battery.py
from bms_sim import battery


def test_ocv_from_soc_matches_table_anchors():
    assert battery.ocv_from_soc(100) == 4.20
    assert battery.ocv_from_soc(50) == 3.75
    assert battery.ocv_from_soc(0) == 3.00


def test_ocv_from_soc_interpolates_linearly_between_anchors():
    # 90%->4.08, 80%->3.98，85% 应该正好是中点
    assert abs(battery.ocv_from_soc(85) - 4.03) < 1e-9


def test_ocv_from_soc_clamps_out_of_range():
    assert battery.ocv_from_soc(150) == 4.20
    assert battery.ocv_from_soc(-10) == 3.00


def test_cell_charge_by_half_hour_at_1c_raises_soc_by_50_points():
    cell = battery.Cell(soc_percent=20.0)
    cell.advance(current_ma=2500.0, dt_seconds=1800)  # 1C 充 0.5 小时 = 50% 容量
    assert abs(cell.soc_percent - 70.0) < 0.5


def test_cell_discharge_lowers_soc():
    cell = battery.Cell(soc_percent=80.0)
    cell.advance(current_ma=-2500.0, dt_seconds=1800)
    assert abs(cell.soc_percent - 30.0) < 0.5


def test_cell_soc_clamped_to_0_100():
    cell = battery.Cell(soc_percent=95.0)
    cell.advance(current_ma=2500.0, dt_seconds=3600 * 10)
    assert cell.soc_percent == 100.0


def test_cell_terminal_voltage_drops_under_discharge_load():
    cell = battery.Cell(soc_percent=50.0)
    ocv = cell.open_circuit_voltage()
    loaded = cell.terminal_voltage(current_ma=-5000.0)
    assert loaded < ocv


def test_battery_pack_total_voltage_is_sum_of_cells():
    pack = battery.BatteryPack(cell_count=4)
    pack.set_soc_percent(50.0)
    assert abs(pack.total_voltage_v - 4 * 3.75) < 0.05


def test_battery_pack_third_cell_has_capacity_and_resistance_bias():
    pack = battery.BatteryPack(cell_count=4)
    assert pack.cells[2].config.capacity_bias < 1.0
    assert pack.cells[2].config.resistance_bias > 1.0


def test_battery_pack_biased_cell_reaches_low_soc_first_on_discharge():
    pack = battery.BatteryPack(cell_count=4)
    pack.set_soc_percent(50.0)
    pack.set_current_ma(-1250.0)  # 0.5C，放 0.5 小时，避免正常电芯也被打到 0 触底导致无法区分
    pack.advance(dt_seconds=1800)
    socs = [c.soc_percent for c in pack.cells]
    assert socs[2] == min(socs)
    assert socs[2] < socs[0]
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_battery.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'bms_sim.battery'`。

- [ ] **Step 3: 实现 battery.py**

```python
# simulator/bms_sim/battery.py
"""电池仿真模型：OCV-SOC 曲线、单体/整包状态、库仑计数、内阻压降。

真机校准点集中在本文件顶部的常量区，真机到手后按 docs 里的"校准清单"替换。
"""
from __future__ import annotations

from dataclasses import dataclass, field

# --- 真机校准点：以下数值来自 EVE INR18650-25P 数据手册估算/协议三表推荐值，
# --- 真机到手后按 JBDTOOLS 实测读数替换。
DEFAULT_RATED_CAPACITY_MAH = 2500.0
DEFAULT_INTERNAL_RESISTANCE_OHM = 0.025
DEFAULT_CELL_COUNT = 4

# OCV-SOC 曲线锚点（三元电芯近似值），线性插值取中间值。
OCV_SOC_TABLE_V = {
    100: 4.20, 90: 4.08, 80: 3.98, 70: 3.90, 60: 3.83,
    50: 3.75, 40: 3.68, 30: 3.63, 20: 3.55, 10: 3.45,
    5: 3.35, 0: 3.00,
}
_OCV_POINTS = sorted(OCV_SOC_TABLE_V.items())


def ocv_from_soc(soc_percent: float) -> float:
    soc_percent = max(0.0, min(100.0, soc_percent))
    for (soc_lo, v_lo), (soc_hi, v_hi) in zip(_OCV_POINTS, _OCV_POINTS[1:]):
        if soc_lo <= soc_percent <= soc_hi:
            ratio = (soc_percent - soc_lo) / (soc_hi - soc_lo)
            return v_lo + ratio * (v_hi - v_lo)
    return _OCV_POINTS[-1][1]


@dataclass
class CellConfig:
    rated_capacity_mah: float = DEFAULT_RATED_CAPACITY_MAH
    internal_resistance_ohm: float = DEFAULT_INTERNAL_RESISTANCE_OHM
    capacity_bias: float = 1.0
    resistance_bias: float = 1.0


class Cell:
    def __init__(self, config: CellConfig | None = None, soc_percent: float = 50.0):
        self.config = config or CellConfig()
        self.soc_percent = soc_percent

    @property
    def effective_capacity_mah(self) -> float:
        return self.config.rated_capacity_mah * self.config.capacity_bias

    @property
    def effective_resistance_ohm(self) -> float:
        return self.config.internal_resistance_ohm * self.config.resistance_bias

    def advance(self, current_ma: float, dt_seconds: float) -> None:
        """按电流推进 SOC（库仑计数）。正=充电，负=放电。"""
        dt_hours = dt_seconds / 3600.0
        delta_percent = current_ma * dt_hours / self.effective_capacity_mah * 100.0
        self.soc_percent = max(0.0, min(100.0, self.soc_percent + delta_percent))

    def open_circuit_voltage(self) -> float:
        return ocv_from_soc(self.soc_percent)

    def terminal_voltage(self, current_ma: float) -> float:
        """端电压 = OCV - I*R（I 为正表示充电电流流入，端电压应比 OCV 高；
        这里用统一符号：充电时端电压略高于 OCV，放电时略低于 OCV）。
        """
        current_a = current_ma / 1000.0
        return self.open_circuit_voltage() + current_a * self.effective_resistance_ohm


def _default_cell_configs(cell_count: int) -> list:
    configs = [CellConfig() for _ in range(cell_count)]
    if cell_count >= 3:
        # 3 号电芯（下标 2）容量偏小、内阻偏大，让它在充放电时先到极值，
        # 均衡逻辑和保护故障才有戏可演。
        configs[2] = CellConfig(capacity_bias=0.95, resistance_bias=1.2)
    return configs


class BatteryPack:
    def __init__(self, cell_count: int = DEFAULT_CELL_COUNT, cell_configs: list | None = None):
        configs = cell_configs or _default_cell_configs(cell_count)
        self.cells = [Cell(config=cfg, soc_percent=50.0) for cfg in configs]
        self.current_ma = 0.0

    def set_current_ma(self, current_ma: float) -> None:
        self.current_ma = current_ma

    def set_soc_percent(self, soc_percent: float) -> None:
        for cell in self.cells:
            cell.soc_percent = soc_percent

    def advance(self, dt_seconds: float) -> None:
        for cell in self.cells:
            cell.advance(self.current_ma, dt_seconds)

    @property
    def total_voltage_v(self) -> float:
        return sum(cell.terminal_voltage(self.current_ma) for cell in self.cells)

    @property
    def average_soc_percent(self) -> float:
        return sum(cell.soc_percent for cell in self.cells) / len(self.cells)

    @property
    def cell_voltages_mv(self) -> list:
        return [round(cell.terminal_voltage(self.current_ma) * 1000) for cell in self.cells]
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_battery.py -v`
Expected: 全部 PASS（10 个用例）。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/battery.py tests/test_battery.py
git commit -m "feat(battery): OCV-SOC 曲线、单体/整包模型与库仑计数"
```

---

### Task 4: battery.py — ThermalModel（I²R 生热 + 一阶散热）

**Files:**
- Modify: `simulator/bms_sim/battery.py`
- Test: `simulator/tests/test_battery.py`

- [ ] **Step 1: 追加失败测试**

```python
# 追加到 simulator/tests/test_battery.py

def test_thermal_model_heats_up_under_load():
    model = battery.ThermalModel(ntc_count=3, ambient_c=25.0)
    start_temp = model.core_temp_c
    for _ in range(60):
        model.advance(current_ma=20000.0, dt_seconds=1.0)  # 20A 持续 60 秒
    assert model.core_temp_c > start_temp


def test_thermal_model_cools_down_after_stop():
    model = battery.ThermalModel(ntc_count=3, ambient_c=25.0)
    model.core_temp_c = 45.0
    for _ in range(120):
        model.advance(current_ma=0.0, dt_seconds=1.0)
    assert model.core_temp_c < 45.0
    assert model.core_temp_c > 25.0  # 还没完全回落到环境温度


def test_thermal_model_ntc_readings_have_small_distinct_offsets():
    model = battery.ThermalModel(ntc_count=3, ambient_c=25.0)
    temps = model.ntc_temperatures_c
    assert len(temps) == 3
    assert len(set(temps)) == 3  # 三路读数互不相同
    assert all(abs(t - 25.0) < 2.0 for t in temps)
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_battery.py -v`
Expected: 新增 3 个用例 FAIL（`AttributeError: module 'bms_sim.battery' has no attribute 'ThermalModel'`）。

- [ ] **Step 3: 实现 ThermalModel，追加到 battery.py**

```python
# 追加到 simulator/bms_sim/battery.py

# --- 真机校准点：热阻/热容/生热内阻都是估算值，真机满载稳态温升实测后调整。
DEFAULT_AMBIENT_TEMP_C = 25.0
DEFAULT_THERMAL_RESISTANCE_C_PER_W = 8.0
DEFAULT_THERMAL_MASS_J_PER_C = 400.0
DEFAULT_PACK_RESISTANCE_FOR_HEATING_OHM = 0.02


class ThermalModel:
    def __init__(self, ntc_count: int = 3, ambient_c: float = DEFAULT_AMBIENT_TEMP_C):
        self.ambient_c = ambient_c
        self.core_temp_c = ambient_c
        self._ntc_offsets_c = [(i - (ntc_count - 1) / 2) * 0.3 for i in range(ntc_count)]

    def advance(self, current_ma: float, dt_seconds: float) -> None:
        current_a = abs(current_ma) / 1000.0
        heat_power_w = (current_a**2) * DEFAULT_PACK_RESISTANCE_FOR_HEATING_OHM
        cooling_power_w = (self.core_temp_c - self.ambient_c) / DEFAULT_THERMAL_RESISTANCE_C_PER_W
        net_power_w = heat_power_w - cooling_power_w
        self.core_temp_c += net_power_w / DEFAULT_THERMAL_MASS_J_PER_C * dt_seconds

    @property
    def ntc_temperatures_c(self) -> list:
        return [round(self.core_temp_c + offset, 2) for offset in self._ntc_offsets_c]
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_battery.py -v`
Expected: 全部 13 个用例 PASS。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/battery.py tests/test_battery.py
git commit -m "feat(battery): 温度一阶模型，I2R 生热+散热+多路 NTC 偏置"
```

---

### Task 5: faults.py — MosController 与 MOS 软件锁定解锁状态机

**Files:**
- Create: `simulator/bms_sim/faults.py`
- Test: `simulator/tests/test_faults.py`

- [ ] **Step 1: 写失败测试**

```python
# simulator/tests/test_faults.py
from bms_sim import faults


def test_mos_controller_default_state_both_enabled():
    mos = faults.MosController()
    assert mos.charge_enabled is True
    assert mos.discharge_enabled is True
    assert mos.locked is False


def test_mos_controller_write_control_toggles_normally_when_unlocked():
    mos = faults.MosController()
    mos.write_control(close_discharge=True, close_charge=False)
    assert mos.discharge_enabled is False
    assert mos.charge_enabled is True
    mos.write_control(close_discharge=False, close_charge=True)
    assert mos.discharge_enabled is True
    assert mos.charge_enabled is False


def test_mos_lock_blocks_direct_control():
    mos = faults.MosController()
    mos.lock()
    assert mos.locked is True
    assert mos.charge_enabled is False
    assert mos.discharge_enabled is False
    # 锁定后直接尝试"全开"应该被忽略
    mos.write_control(close_discharge=False, close_charge=False)
    assert mos.charge_enabled is False
    assert mos.discharge_enabled is False
    assert mos.locked is True


def test_mos_unlock_wrong_order_stays_locked():
    mos = faults.MosController()
    mos.lock()
    # 顺序错误：没有先关充放，直接尝试开充电
    mos.write_control(close_discharge=True, close_charge=False)
    assert mos.locked is True
    assert mos.charge_enabled is False


def test_mos_unlock_correct_order_succeeds():
    mos = faults.MosController()
    mos.lock()
    mos.write_control(close_discharge=True, close_charge=True)  # 先关充放
    assert mos.locked is True
    mos.write_control(close_discharge=True, close_charge=False)  # 再开充电
    assert mos.charge_enabled is True
    assert mos.discharge_enabled is False
    assert mos.locked is True
    mos.write_control(close_discharge=False, close_charge=False)  # 最后开放电
    assert mos.discharge_enabled is True
    assert mos.locked is False


def test_mos_unlock_discharge_before_charge_is_ignored():
    mos = faults.MosController()
    mos.lock()
    mos.write_control(close_discharge=True, close_charge=True)
    # 顺序错误：先尝试开放电（而不是先开充电）
    mos.write_control(close_discharge=False, close_charge=True)
    assert mos.locked is True
    assert mos.discharge_enabled is False
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_faults.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'bms_sim.faults'`。

- [ ] **Step 3: 实现 MosController**

```python
# simulator/bms_sim/faults.py
"""保护状态位、故障注入、MOS 控制与软件锁定状态机。

保护位定义与推荐阈值来自 docs/阶段0_JBD-SP04S010_协议参考三表.md 第二、三节。
"""
from __future__ import annotations

from enum import IntEnum


class MosUnlockStep(IntEnum):
    LOCKED = 0
    CLOSED_BOTH = 1
    CHARGE_OPENED = 2
    UNLOCKED = 3


class MosController:
    """维护 charge_enabled / discharge_enabled 两个真值。

    写寄存器 0xE1 的语义是"关闭"（bit0=1 关放电、bit1=1 关充电），与状态字节
    （bit0=充电开、bit1=放电开）相反，本类只对外暴露真值，两种位图语义的换算
    交给调用方（device.py）在寄存器 IO 边界上做。
    """

    def __init__(self):
        self.charge_enabled = True
        self.discharge_enabled = True
        self._locked = False
        self._unlock_step = MosUnlockStep.UNLOCKED

    @property
    def locked(self) -> bool:
        return self._locked

    def lock(self) -> None:
        """触发 MOS 软件锁定（对应保护位 bit12）。"""
        self._locked = True
        self._unlock_step = MosUnlockStep.LOCKED
        self.charge_enabled = False
        self.discharge_enabled = False

    def write_control(self, close_discharge: bool, close_charge: bool) -> None:
        if not self._locked:
            self.discharge_enabled = not close_discharge
            self.charge_enabled = not close_charge
            return

        if self._unlock_step == MosUnlockStep.LOCKED:
            if close_discharge and close_charge:
                self._unlock_step = MosUnlockStep.CLOSED_BOTH
            return  # 顺序不对，忽略这次写入

        if self._unlock_step == MosUnlockStep.CLOSED_BOTH:
            if close_discharge and not close_charge:
                self.charge_enabled = True
                self._unlock_step = MosUnlockStep.CHARGE_OPENED
            return  # 顺序不对（比如先开放电），忽略

        if self._unlock_step == MosUnlockStep.CHARGE_OPENED:
            if not close_discharge:
                self.discharge_enabled = True
                self._unlock_step = MosUnlockStep.UNLOCKED
                self._locked = False
            return  # 顺序不对，忽略
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_faults.py -v`
Expected: 全部 6 个用例 PASS。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/faults.py tests/test_faults.py
git commit -m "feat(faults): MOS 控制与软件锁定(bit12)解锁状态机"
```

---

### Task 6: faults.py — ProtectionBit、阈值常量、FaultManager（注入/评估/清除）

**Files:**
- Modify: `simulator/bms_sim/faults.py`
- Test: `simulator/tests/test_faults.py`

- [ ] **Step 1: 追加失败测试**

```python
# 追加到 simulator/tests/test_faults.py

def test_inject_sets_protection_bit_and_clear_resets_it():
    manager = faults.FaultManager(faults.MosController())
    manager.inject(faults.ProtectionBit.CELL_OVERVOLTAGE)
    assert manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)
    assert manager.protection_status & (1 << 0)
    manager.clear(faults.ProtectionBit.CELL_OVERVOLTAGE)
    assert not manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)


def test_inject_charge_fault_disables_charge_mos_only():
    mos = faults.MosController()
    manager = faults.FaultManager(mos)
    manager.inject(faults.ProtectionBit.CHARGE_OVERTEMP)
    assert mos.charge_enabled is False
    assert mos.discharge_enabled is True


def test_inject_discharge_fault_disables_discharge_mos_only():
    mos = faults.MosController()
    manager = faults.FaultManager(mos)
    manager.inject(faults.ProtectionBit.DISCHARGE_OVERCURRENT)
    assert mos.discharge_enabled is False
    assert mos.charge_enabled is True


def test_inject_mos_locked_bit_locks_mos_controller():
    mos = faults.MosController()
    manager = faults.FaultManager(mos)
    manager.inject(faults.ProtectionBit.MOS_LOCKED)
    assert mos.locked is True
    assert manager.is_set(faults.ProtectionBit.MOS_LOCKED)


def test_evaluate_triggers_cell_overvoltage_when_above_threshold():
    manager = faults.FaultManager(faults.MosController())
    manager.evaluate(
        cell_voltages_v=[4.30, 3.80, 3.80, 3.80],
        pack_voltage_v=15.7,
        temperatures_c=[25.0, 25.0, 25.0],
        current_ma=1000.0,
        dt_seconds=1.0,
    )
    assert manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)


def test_evaluate_auto_clears_when_back_within_recover_threshold():
    manager = faults.FaultManager(faults.MosController())
    manager.evaluate(
        cell_voltages_v=[4.30, 3.80, 3.80, 3.80], pack_voltage_v=15.7,
        temperatures_c=[25.0, 25.0, 25.0], current_ma=1000.0, dt_seconds=1.0,
    )
    assert manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)
    manager.evaluate(
        cell_voltages_v=[4.10, 3.80, 3.80, 3.80], pack_voltage_v=15.5,
        temperatures_c=[25.0, 25.0, 25.0], current_ma=1000.0, dt_seconds=1.0,
    )
    assert not manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)


def test_evaluate_delayed_fault_needs_accumulated_time():
    manager = faults.FaultManager(faults.MosController())
    manager.configure(faults.ProtectionBit.DISCHARGE_OVERCURRENT, trigger_mode="delayed", delay_seconds=5.0)
    for _ in range(4):
        manager.evaluate(
            cell_voltages_v=[3.8] * 4, pack_voltage_v=15.2,
            temperatures_c=[25.0] * 3, current_ma=-25000.0, dt_seconds=1.0,
        )
    assert not manager.is_set(faults.ProtectionBit.DISCHARGE_OVERCURRENT)
    manager.evaluate(
        cell_voltages_v=[3.8] * 4, pack_voltage_v=15.2,
        temperatures_c=[25.0] * 3, current_ma=-25000.0, dt_seconds=2.0,
    )
    assert manager.is_set(faults.ProtectionBit.DISCHARGE_OVERCURRENT)


def test_evaluate_manual_clear_mode_does_not_auto_recover():
    manager = faults.FaultManager(faults.MosController())
    manager.configure(faults.ProtectionBit.CELL_OVERVOLTAGE, clear_mode="manual")
    manager.evaluate(
        cell_voltages_v=[4.30, 3.8, 3.8, 3.8], pack_voltage_v=15.7,
        temperatures_c=[25.0] * 3, current_ma=1000.0, dt_seconds=1.0,
    )
    assert manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)
    manager.evaluate(
        cell_voltages_v=[3.8] * 4, pack_voltage_v=15.2,
        temperatures_c=[25.0] * 3, current_ma=1000.0, dt_seconds=1.0,
    )
    assert manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)  # 还得手动 clear
    manager.clear(faults.ProtectionBit.CELL_OVERVOLTAGE)
    assert not manager.is_set(faults.ProtectionBit.CELL_OVERVOLTAGE)
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_faults.py -v`
Expected: 新增用例 FAIL（`AttributeError: module 'bms_sim.faults' has no attribute 'ProtectionBit'`）。

- [ ] **Step 3: 实现 ProtectionBit / PROTECTION_THRESHOLDS / FaultManager，追加到 faults.py**

```python
# 追加到 simulator/bms_sim/faults.py


class ProtectionBit(IntEnum):
    CELL_OVERVOLTAGE = 0
    CELL_UNDERVOLTAGE = 1
    PACK_OVERVOLTAGE = 2
    PACK_UNDERVOLTAGE = 3
    CHARGE_OVERTEMP = 4
    CHARGE_UNDERTEMP = 5
    DISCHARGE_OVERTEMP = 6
    DISCHARGE_UNDERTEMP = 7
    CHARGE_OVERCURRENT = 8
    DISCHARGE_OVERCURRENT = 9
    SHORT_CIRCUIT = 10
    IC_ERROR = 11
    MOS_LOCKED = 12


# --- 真机校准点：以下"推荐触发/恢复值"来自协议三表第三节，真机到手后按
# --- JBDTOOLS 配置界面实测值替换。恢复值为 None 表示该保护不设自动恢复阈值
# --- （文档标注"延时后自恢复"或"查真机"，先按需要手动清除处理）。
PROTECTION_THRESHOLDS = {
    "cell_overvoltage_v": (4.25, 4.15),
    "cell_undervoltage_v": (2.80, 3.00),
    "pack_overvoltage_v": (17.0, 16.6),
    "pack_undervoltage_v": (11.2, 12.0),
    "charge_overtemp_c": (50.0, 45.0),
    "charge_undertemp_c": (0.0, 5.0),
    "discharge_overtemp_c": (60.0, 55.0),
    "discharge_undertemp_c": (-20.0, -10.0),
    "charge_overcurrent_a": (5.0, None),
    "discharge_overcurrent_a": (18.0, None),
}

_CHARGE_MOS_FAULTS = {
    ProtectionBit.CELL_OVERVOLTAGE, ProtectionBit.PACK_OVERVOLTAGE,
    ProtectionBit.CHARGE_OVERTEMP, ProtectionBit.CHARGE_UNDERTEMP,
    ProtectionBit.CHARGE_OVERCURRENT,
}
_DISCHARGE_MOS_FAULTS = {
    ProtectionBit.CELL_UNDERVOLTAGE, ProtectionBit.PACK_UNDERVOLTAGE,
    ProtectionBit.DISCHARGE_OVERTEMP, ProtectionBit.DISCHARGE_UNDERTEMP,
    ProtectionBit.DISCHARGE_OVERCURRENT, ProtectionBit.SHORT_CIRCUIT,
}


class FaultConfig:
    def __init__(self, trigger_mode: str = "instant", delay_seconds: float = 0.0, clear_mode: str = "auto"):
        self.trigger_mode = trigger_mode
        self.delay_seconds = delay_seconds
        self.clear_mode = clear_mode


class FaultManager:
    def __init__(self, mos: MosController):
        self.mos = mos
        self.protection_status = 0
        self._configs = {bit: FaultConfig() for bit in ProtectionBit if bit != ProtectionBit.MOS_LOCKED}
        self._pending_seconds = {}
        self._trigger_counts = {}

    def configure(self, bit: ProtectionBit, trigger_mode: str = "instant", delay_seconds: float = 0.0, clear_mode: str = "auto") -> None:
        self._configs[bit] = FaultConfig(trigger_mode, delay_seconds, clear_mode)

    def is_set(self, bit: ProtectionBit) -> bool:
        return bool(self.protection_status & (1 << int(bit)))

    def error_count_bytes(self) -> bytes:
        """各类保护累计触发次数，每类 1 字节（顺序=保护位号 0~12），封顶 255。
        协议文档未给出 0xAA 的精确数据格式（标注"查真机"），先按此自定格式实现，
        真机到手后按抓包结果调整。
        """
        return bytes(min(self._trigger_counts.get(bit, 0), 255) for bit in range(13))

    def inject(self, bit: ProtectionBit) -> None:
        if bit == ProtectionBit.MOS_LOCKED:
            self.mos.lock()
            self._set_bit(bit)
            return
        self._set_bit(bit)
        self._apply_mos_action(bit)

    def clear(self, bit: ProtectionBit) -> None:
        if bit == ProtectionBit.MOS_LOCKED:
            return  # bit12 只能通过 MosController 的解锁流程清除
        self._clear_bit(bit)

    def evaluate(self, *, cell_voltages_v: list, pack_voltage_v: float, temperatures_c: list, current_ma: float, dt_seconds: float) -> None:
        current_a = current_ma / 1000.0
        ov = PROTECTION_THRESHOLDS
        checks = [
            (ProtectionBit.CELL_OVERVOLTAGE,
             any(v >= ov["cell_overvoltage_v"][0] for v in cell_voltages_v),
             all(v <= ov["cell_overvoltage_v"][1] for v in cell_voltages_v)),
            (ProtectionBit.CELL_UNDERVOLTAGE,
             any(v <= ov["cell_undervoltage_v"][0] for v in cell_voltages_v),
             all(v >= ov["cell_undervoltage_v"][1] for v in cell_voltages_v)),
            (ProtectionBit.PACK_OVERVOLTAGE,
             pack_voltage_v >= ov["pack_overvoltage_v"][0],
             pack_voltage_v <= ov["pack_overvoltage_v"][1]),
            (ProtectionBit.PACK_UNDERVOLTAGE,
             pack_voltage_v <= ov["pack_undervoltage_v"][0],
             pack_voltage_v >= ov["pack_undervoltage_v"][1]),
            (ProtectionBit.CHARGE_OVERTEMP,
             current_ma > 0 and any(t >= ov["charge_overtemp_c"][0] for t in temperatures_c),
             all(t <= ov["charge_overtemp_c"][1] for t in temperatures_c)),
            (ProtectionBit.CHARGE_UNDERTEMP,
             current_ma > 0 and any(t <= ov["charge_undertemp_c"][0] for t in temperatures_c),
             all(t >= ov["charge_undertemp_c"][1] for t in temperatures_c)),
            (ProtectionBit.DISCHARGE_OVERTEMP,
             current_ma < 0 and any(t >= ov["discharge_overtemp_c"][0] for t in temperatures_c),
             all(t <= ov["discharge_overtemp_c"][1] for t in temperatures_c)),
            (ProtectionBit.DISCHARGE_UNDERTEMP,
             current_ma < 0 and any(t <= ov["discharge_undertemp_c"][0] for t in temperatures_c),
             all(t >= ov["discharge_undertemp_c"][1] for t in temperatures_c)),
            (ProtectionBit.CHARGE_OVERCURRENT,
             current_a >= ov["charge_overcurrent_a"][0],
             current_a < ov["charge_overcurrent_a"][0]),
            (ProtectionBit.DISCHARGE_OVERCURRENT,
             -current_a >= ov["discharge_overcurrent_a"][0],
             -current_a < ov["discharge_overcurrent_a"][0]),
        ]
        for bit, triggered, recovered in checks:
            self._evaluate_one(bit, triggered, recovered, dt_seconds)

    def _evaluate_one(self, bit: ProtectionBit, triggered: bool, recovered: bool, dt_seconds: float) -> None:
        config = self._configs[bit]
        if triggered:
            if config.trigger_mode == "instant":
                if not self.is_set(bit):
                    self._set_bit(bit)
                    self._apply_mos_action(bit)
                self._pending_seconds.pop(bit, None)
            else:
                elapsed = self._pending_seconds.get(bit, 0.0) + dt_seconds
                self._pending_seconds[bit] = elapsed
                if elapsed >= config.delay_seconds and not self.is_set(bit):
                    self._set_bit(bit)
                    self._apply_mos_action(bit)
            return
        self._pending_seconds.pop(bit, None)
        if self.is_set(bit) and recovered and config.clear_mode == "auto":
            self._clear_bit(bit)

    def _apply_mos_action(self, bit: ProtectionBit) -> None:
        if bit in _CHARGE_MOS_FAULTS:
            self.mos.charge_enabled = False
        if bit in _DISCHARGE_MOS_FAULTS:
            self.mos.discharge_enabled = False

    def _set_bit(self, bit: ProtectionBit) -> None:
        if not self.is_set(bit):
            self._trigger_counts[int(bit)] = self._trigger_counts.get(int(bit), 0) + 1
        self.protection_status |= 1 << int(bit)

    def _clear_bit(self, bit: ProtectionBit) -> None:
        self.protection_status &= ~(1 << int(bit))
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_faults.py -v`
Expected: 全部 14 个用例 PASS。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/faults.py tests/test_faults.py
git commit -m "feat(faults): 保护位阈值评估、故障注入与自动/手动恢复"
```

---

### Task 7: device.py — 命令分发与模型胶水

**Files:**
- Create: `simulator/bms_sim/device.py`
- Test: `simulator/tests/test_device.py`

- [ ] **Step 1: 写失败测试**

```python
# simulator/tests/test_device.py
from bms_sim import protocol as proto
from bms_sim.device import Device
from bms_sim.faults import ProtectionBit


def test_read_basic_info_returns_ok_status_and_correct_length():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_BASIC_INFO, data=b"")
    response = device.handle_request(frame)
    decoded = proto.decode_response(response)
    assert decoded.status == proto.STATUS_OK
    info = proto.parse_basic_info(decoded.data)
    assert info.cell_count == 4
    assert info.ntc_count == 3
    assert 0.0 <= info.soc_percent <= 100.0


def test_read_cell_voltages_returns_4_values():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_CELL_VOLTAGES, data=b"")
    decoded = proto.decode_response(device.handle_request(frame))
    voltages = proto.parse_cell_voltages(decoded.data)
    assert len(voltages) == 4
    assert all(2500 <= v <= 4300 for v in voltages)


def test_read_device_name():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_DEVICE_NAME, data=b"")
    decoded = proto.decode_response(device.handle_request(frame))
    assert decoded.data.decode("ascii") == "JBD-SP04S010-Sim"


def test_read_config_register_without_entering_mode_returns_error_status():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_ERROR_COUNT, data=b"")
    decoded = proto.decode_response(device.handle_request(frame))
    assert decoded.status == proto.STATUS_ERROR


def test_enter_config_mode_then_read_error_count_succeeds():
    device = Device()
    enter_frame = proto.RequestFrame(
        op=proto.OP_WRITE, register=proto.REG_ENTER_CONFIG,
        data=proto.ENTER_CONFIG_MAGIC.to_bytes(2, "big"),
    )
    decoded = proto.decode_response(device.handle_request(enter_frame))
    assert decoded.status == proto.STATUS_OK

    read_frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_ERROR_COUNT, data=b"")
    decoded = proto.decode_response(device.handle_request(read_frame))
    assert decoded.status == proto.STATUS_OK
    assert len(decoded.data) == 13


def test_write_mos_control_closes_discharge_and_charge():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_WRITE, register=proto.REG_MOS_CONTROL, data=bytes([0x00, 0x03]))
    decoded = proto.decode_response(device.handle_request(frame))
    assert decoded.status == proto.STATUS_OK
    assert device.mos.charge_enabled is False
    assert device.mos.discharge_enabled is False


def test_basic_info_reflects_mos_state_after_control_write():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_WRITE, register=proto.REG_MOS_CONTROL, data=bytes([0x00, 0x03]))
    device.handle_request(frame)

    read_frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_BASIC_INFO, data=b"")
    decoded = proto.decode_response(device.handle_request(read_frame))
    info = proto.parse_basic_info(decoded.data)
    assert info.mos_charge_on is False
    assert info.mos_discharge_on is False


def test_basic_info_shows_injected_fault_protection_bit():
    device = Device()
    device.faults.inject(ProtectionBit.CELL_OVERVOLTAGE)

    read_frame = proto.RequestFrame(op=proto.OP_READ, register=proto.REG_BASIC_INFO, data=b"")
    decoded = proto.decode_response(device.handle_request(read_frame))
    info = proto.parse_basic_info(decoded.data)
    assert info.protection_status & (1 << int(ProtectionBit.CELL_OVERVOLTAGE))


def test_unknown_register_returns_error_status():
    device = Device()
    frame = proto.RequestFrame(op=proto.OP_READ, register=0x99, data=b"")
    decoded = proto.decode_response(device.handle_request(frame))
    assert decoded.status == proto.STATUS_ERROR
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_device.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'bms_sim.device'`。

- [ ] **Step 3: 实现 device.py**

```python
# simulator/bms_sim/device.py
"""把协议命令分发到电池/故障模型，唯一知道"寄存器语义"的胶水层。"""
from __future__ import annotations

import time
from datetime import date

from . import protocol as proto
from .battery import DEFAULT_RATED_CAPACITY_MAH, BatteryPack, ThermalModel
from .faults import FaultManager, MosController

DEVICE_NAME = "JBD-SP04S010-Sim"
DEFAULT_PRODUCTION_DATE = date(2022, 3, 28)


class Device:
    def __init__(self, cell_count: int = 4, ntc_count: int = 3):
        self.battery = BatteryPack(cell_count=cell_count)
        self.thermal = ThermalModel(ntc_count=ntc_count)
        self.mos = MosController()
        self.faults = FaultManager(self.mos)
        self.config_mode = False
        self.cycles = 0
        self.production_date = DEFAULT_PRODUCTION_DATE
        self.balance_enabled = False
        self._last_update = time.monotonic()

    def _advance(self) -> None:
        now = time.monotonic()
        dt = now - self._last_update
        self._last_update = now
        if dt <= 0:
            return

        current_ma = self.battery.current_ma
        if current_ma > 0 and not self.mos.charge_enabled:
            current_ma = 0.0
        if current_ma < 0 and not self.mos.discharge_enabled:
            current_ma = 0.0
        self.battery.current_ma = current_ma

        self.battery.advance(dt)
        self.thermal.advance(current_ma, dt)
        self.faults.evaluate(
            cell_voltages_v=[v / 1000 for v in self.battery.cell_voltages_mv],
            pack_voltage_v=self.battery.total_voltage_v,
            temperatures_c=self.thermal.ntc_temperatures_c,
            current_ma=current_ma,
            dt_seconds=dt,
        )

    def handle_request(self, frame: proto.RequestFrame) -> bytes:
        self._advance()
        if frame.op == proto.OP_READ:
            return self._handle_read(frame.register)
        if frame.op == proto.OP_WRITE:
            return self._handle_write(frame.register, frame.data)
        return proto.encode_response(frame.register, proto.STATUS_ERROR, b"")

    def _handle_read(self, register: int) -> bytes:
        if register == proto.REG_BASIC_INFO:
            return proto.encode_response(register, proto.STATUS_OK, self._build_basic_info())
        if register == proto.REG_CELL_VOLTAGES:
            data = proto.encode_cell_voltages(self.battery.cell_voltages_mv)
            return proto.encode_response(register, proto.STATUS_OK, data)
        if register == proto.REG_DEVICE_NAME:
            return proto.encode_response(register, proto.STATUS_OK, DEVICE_NAME.encode("ascii"))
        if register == proto.REG_ERROR_COUNT:
            if not self.config_mode:
                return proto.encode_response(register, proto.STATUS_ERROR, b"")
            return proto.encode_response(register, proto.STATUS_OK, self.faults.error_count_bytes())
        return proto.encode_response(register, proto.STATUS_ERROR, b"")

    def _handle_write(self, register: int, data: bytes) -> bytes:
        if register == proto.REG_ENTER_CONFIG:
            if len(data) == 2 and int.from_bytes(data, "big") == proto.ENTER_CONFIG_MAGIC:
                self.config_mode = True
                return proto.encode_response(register, proto.STATUS_OK, b"")
            return proto.encode_response(register, proto.STATUS_ERROR, b"")
        if register == proto.REG_EXIT_SAVE:
            self.config_mode = False
            return proto.encode_response(register, proto.STATUS_OK, b"")
        if register == proto.REG_MOS_CONTROL:
            if len(data) != 2:
                return proto.encode_response(register, proto.STATUS_ERROR, b"")
            value = int.from_bytes(data, "big")
            self.mos.write_control(close_discharge=bool(value & 0x01), close_charge=bool(value & 0x02))
            return proto.encode_response(register, proto.STATUS_OK, b"")
        if register == proto.REG_BALANCE_CONTROL:
            if len(data) != 2:
                return proto.encode_response(register, proto.STATUS_ERROR, b"")
            self.balance_enabled = int.from_bytes(data, "big") != 0
            return proto.encode_response(register, proto.STATUS_OK, b"")
        return proto.encode_response(register, proto.STATUS_ERROR, b"")

    def _build_basic_info(self) -> bytes:
        info = proto.BasicInfo(
            total_voltage_v=self.battery.total_voltage_v,
            current_a=self.battery.current_ma / 1000,
            remaining_capacity_ah=(DEFAULT_RATED_CAPACITY_MAH / 1000) * (self.battery.average_soc_percent / 100),
            nominal_capacity_ah=DEFAULT_RATED_CAPACITY_MAH / 1000,
            cycles=self.cycles,
            production_date=self.production_date,
            balance_low=0,
            balance_high=0,
            protection_status=self.faults.protection_status,
            software_version="8.0",
            soc_percent=round(self.battery.average_soc_percent),
            mos_charge_on=self.mos.charge_enabled,
            mos_discharge_on=self.mos.discharge_enabled,
            cell_count=len(self.battery.cells),
            ntc_count=len(self.thermal.ntc_temperatures_c),
            temperatures_c=self.thermal.ntc_temperatures_c,
        )
        return proto.encode_basic_info(info)
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_device.py -v`
Expected: 全部 9 个用例 PASS。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/device.py tests/test_device.py
git commit -m "feat(device): 命令分发，串联协议/电池/故障三层模型"
```

---

### Task 8: server.py — 串口收发主循环

**Files:**
- Create: `simulator/bms_sim/server.py`
- Test: `simulator/tests/test_server.py`

- [ ] **Step 1: 写失败测试（用假串口对象，不需要真实硬件）**

```python
# simulator/tests/test_server.py
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
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_server.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'bms_sim.server'`。

- [ ] **Step 3: 实现 server.py**

```python
# simulator/bms_sim/server.py
"""真实串口收发主循环。process_available()/serve() 只依赖 port 对象提供
read(n)/write(data) 两个方法（serial.Serial 满足这个接口，测试里用假对象替代）。
"""
from __future__ import annotations

import argparse
import time

import serial

from . import protocol as proto
from .device import Device


def process_available(port, device: Device, decoder: proto.FrameDecoder) -> int:
    """读一次串口缓冲区、处理其中的完整请求帧并写回响应，返回处理的帧数。"""
    chunk = port.read(64)
    if not chunk:
        return 0
    frames = decoder.feed(chunk)
    for frame in frames:
        port.write(device.handle_request(frame))
    return len(frames)


def serve(port, device: Device | None = None) -> None:
    device = device or Device()
    decoder = proto.FrameDecoder("request")
    print("BMS 模拟器已启动，按 Ctrl+C 退出")
    try:
        while True:
            if process_available(port, device, decoder) == 0:
                time.sleep(0.01)
    except KeyboardInterrupt:
        print("已停止")


def main() -> None:
    parser = argparse.ArgumentParser(description="JBD SP04S010 串口 BMS 模拟器")
    parser.add_argument("--port", required=True, help="串口号，例如 COM6 或 /dev/pts/3")
    parser.add_argument("--baudrate", type=int, default=9600)
    args = parser.parse_args()
    with serial.Serial(args.port, baudrate=args.baudrate, timeout=0.05) as ser:
        serve(ser)


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_server.py -v`
Expected: 全部 3 个用例 PASS。

- [ ] **Step 5: 提交**

```bash
git add bms_sim/server.py tests/test_server.py
git commit -m "feat(server): 串口收发主循环，process_available 可脱离硬件测试"
```

---

### Task 9: cli.py — REPL 控制台 + 场景回放

**Files:**
- Create: `simulator/bms_sim/cli.py`
- Create: `simulator/bms_sim/scenarios/demo.yaml`
- Test: `simulator/tests/test_cli.py`

- [ ] **Step 1: 写失败测试**

```python
# simulator/tests/test_cli.py
from bms_sim import cli
from bms_sim.device import Device
from bms_sim.faults import ProtectionBit


def test_handle_command_sets_current():
    device = Device()
    cli.handle_command(device, "current 500")
    assert device.battery.current_ma == 500.0


def test_handle_command_sets_soc():
    device = Device()
    cli.handle_command(device, "soc 42")
    assert device.battery.average_soc_percent == 42.0


def test_handle_command_injects_and_clears_fault():
    device = Device()
    cli.handle_command(device, "fault inject cell_overvoltage")
    assert device.faults.is_set(ProtectionBit.CELL_OVERVOLTAGE)
    cli.handle_command(device, "fault clear cell_overvoltage")
    assert not device.faults.is_set(ProtectionBit.CELL_OVERVOLTAGE)


def test_handle_command_writes_mos_control():
    device = Device()
    cli.handle_command(device, "mos 0x03")
    assert device.mos.charge_enabled is False
    assert device.mos.discharge_enabled is False


def test_handle_command_sets_temperature():
    device = Device()
    cli.handle_command(device, "temp 60")
    assert device.thermal.core_temp_c == 60.0


def test_handle_command_quit_returns_false():
    device = Device()
    assert cli.handle_command(device, "quit") is False


def test_handle_command_unknown_returns_true_and_does_not_raise():
    device = Device()
    assert cli.handle_command(device, "not_a_real_command") is True


def test_run_scenario_executes_steps_in_order(tmp_path):
    scenario_path = tmp_path / "scenario.yaml"
    scenario_path.write_text(
        "steps:\n"
        "  - wait_seconds: 0\n"
        "    action: current 500\n"
        "  - wait_seconds: 0\n"
        "    action: fault inject cell_overvoltage\n",
        encoding="utf-8",
    )
    device = Device()
    cli.run_scenario(device, str(scenario_path))
    assert device.battery.current_ma == 500.0
    assert device.faults.is_set(ProtectionBit.CELL_OVERVOLTAGE)
```

- [ ] **Step 2: 运行测试确认失败**

Run: `python -m pytest tests/test_cli.py -v`
Expected: FAIL，`ModuleNotFoundError: No module named 'bms_sim.cli'`。

- [ ] **Step 3: 实现 cli.py**

```python
# simulator/bms_sim/cli.py
"""交互式控制台 + 场景 YAML 回放。"""
from __future__ import annotations

import argparse
import time

import yaml

from .device import Device
from .faults import ProtectionBit


def print_status(device: Device) -> None:
    device._advance()
    temps = ", ".join(f"{t:.1f}" for t in device.thermal.ntc_temperatures_c)
    print(
        f"总压={device.battery.total_voltage_v:.2f}V "
        f"电流={device.battery.current_ma:.0f}mA "
        f"SOC={device.battery.average_soc_percent:.1f}% "
        f"温度=[{temps}]℃ "
        f"保护=0x{device.faults.protection_status:04X} "
        f"MOS(充/放)={device.mos.charge_enabled}/{device.mos.discharge_enabled}"
    )


def handle_command(device: Device, line: str) -> bool:
    """处理一条命令，返回 False 表示应该退出。"""
    parts = line.strip().split()
    if not parts:
        return True
    cmd = parts[0].lower()

    if cmd in ("quit", "exit"):
        return False
    if cmd == "current" and len(parts) == 2:
        device.battery.set_current_ma(float(parts[1]))
    elif cmd == "soc" and len(parts) == 2:
        device.battery.set_soc_percent(float(parts[1]))
    elif cmd == "fault" and len(parts) == 3 and parts[1] in ("inject", "clear"):
        bit = ProtectionBit[parts[2].upper()]
        if parts[1] == "inject":
            device.faults.inject(bit)
        else:
            device.faults.clear(bit)
    elif cmd == "mos" and len(parts) == 2:
        value = int(parts[1], 0)
        device.mos.write_control(close_discharge=bool(value & 0x01), close_charge=bool(value & 0x02))
    elif cmd == "temp" and len(parts) == 2:
        device.thermal.core_temp_c = float(parts[1])
    elif cmd == "scenario" and len(parts) == 2:
        run_scenario(device, parts[1])
    elif cmd == "status":
        pass
    else:
        print(f"未知命令: {line}")
        return True

    print_status(device)
    return True


def run_scenario(device: Device, path: str) -> None:
    with open(path, "r", encoding="utf-8") as f:
        scenario = yaml.safe_load(f)
    for step in scenario.get("steps", []):
        wait = step.get("wait_seconds", 0)
        if wait:
            time.sleep(wait)
        action = step.get("action")
        print(f"[场景] {action}")
        handle_command(device, action)


def repl(device: Device) -> None:
    print("BMS 模拟器控制台，输入 help 查看命令，quit 退出")
    print_status(device)
    while True:
        try:
            line = input("> ")
        except EOFError:
            break
        if line.strip() == "help":
            print(
                "命令: current <mA> | soc <percent> | fault inject/clear <name> | "
                "mos <hex> | temp <celsius> | scenario <path> | status | quit"
            )
            continue
        if not handle_command(device, line):
            break


def main() -> None:
    parser = argparse.ArgumentParser(description="BMS 模拟器控制台")
    parser.add_argument("--scenario", help="启动后直接回放该场景文件并退出（不进入交互模式）")
    args = parser.parse_args()
    device = Device()
    if args.scenario:
        run_scenario(device, args.scenario)
        print_status(device)
        return
    repl(device)


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: 运行测试确认通过**

Run: `python -m pytest tests/test_cli.py -v`
Expected: 全部 8 个用例 PASS。

- [ ] **Step 5: 写示例场景文件**

```yaml
# simulator/bms_sim/scenarios/demo.yaml
name: "演示：充电升压 -> 触发单体过压 -> 保护动作 -> 清故障 -> 放电"
steps:
  - wait_seconds: 0
    action: "current 2000"
  - wait_seconds: 1
    action: "status"
  - wait_seconds: 1
    action: "fault inject cell_overvoltage"
  - wait_seconds: 1
    action: "status"
  - wait_seconds: 1
    action: "fault clear cell_overvoltage"
  - wait_seconds: 1
    action: "current -1500"
  - wait_seconds: 1
    action: "status"
```

- [ ] **Step 6: 提交**

```bash
git add bms_sim/cli.py bms_sim/scenarios/demo.yaml tests/test_cli.py
git commit -m "feat(cli): 交互式控制台与场景 YAML 回放，附示例场景"
```

---

### Task 10: README.md — 虚拟串口两平台建法 + 使用说明

**Files:**
- Create: `simulator/README.md`

- [ ] **Step 1: 写 README**

```markdown
# 串口 BMS 模拟器

模拟 JBD SP04S010（4 串三元 / 30A / 带均衡）保护板的串口从站，供上位机开发在真板子到货前联调、演示、自动化测试。协议实现严格对齐 `../docs/阶段0_JBD-SP04S010_协议参考三表.md`。

## 环境准备

```bash
cd simulator
python -m venv .venv
# Windows: .venv\Scripts\activate
# Linux/Mac: source .venv/bin/activate
pip install -r requirements-dev.txt
```

## 跑测试

```bash
python -m pytest -v
```

黄金测试向量（协议三表 1.3 节）在 `tests/test_protocol.py` 里，是校验实现是否正确的基准，务必全绿。

## 建虚拟串口对

模拟器需要占住虚拟串口对的一端，上位机（或 JBDTOOLS / 任意串口调试工具）接另一端。

### Windows：com0com

1. 从 [com0com 官方仓库](https://sourceforge.net/projects/com0com/) 下载安装（选带签名的 driver 版本，Win10/11 需要开启测试签名或用社区签名版）。
2. 安装后打开 "Setup Command Prompt"，默认会建一对 `CNCA0 <-> CNCB0`；也可以用图形界面 `com0com setup` 重命名成好记的名字，比如：

   ```
   change CNCA0 PortName=COM10
   change CNCB0 PortName=COM11
   ```

3. 模拟器接 `COM10`，上位机/调试工具接 `COM11`（或反过来，两端对称）。

### Linux / Mac：socat

```bash
socat -d -d pty,raw,echo=0 pty,raw,echo=0
```

运行后会打印出两个 `/dev/pts/N` 路径，例如：

```
2026/07/10 10:00:00 socat[12345] N PTY is /dev/pts/3
2026/07/10 10:00:00 socat[12345] N PTY is /dev/pts/4
```

模拟器接 `/dev/pts/3`，上位机/调试工具接 `/dev/pts/4`。这个终端窗口要一直开着（socat 常驻进程维持这对虚拟串口）。

## 启动模拟器

```bash
python -m bms_sim.server --port COM10 --baudrate 9600
```

（Linux/Mac 换成 `--port /dev/pts/3`。）

## 用控制台驱动模拟器

另开一个终端跑交互控制台（与 `server` 共享同一份协议实现，但两者是各自独立的 `Device` 实例——**控制台/场景脚本要驱动的是真正跑在 server 里的那个模拟器**，实际使用时把 `server.py` 和 `cli.py` 接到同一个 `Device` 上，或者用下面"一键回放"方式，让 CLI 自己起一个不经过真实串口的 `Device` 用于快速试验）：

```bash
python -m bms_sim.cli
```

进入交互模式后：

```
> current 2000        # 设置 2000mA 充电电流
> soc 80               # 把所有电芯 SOC 跳到 80%
> fault inject cell_overvoltage   # 注入单体过压故障
> mos 0x03             # 写 MOS 控制寄存器：全关充放（0xE1 语义，bit0=关放电/bit1=关充电）
> fault clear cell_overvoltage
> status
> quit
```

## 一键回放演示场景

```bash
python -m bms_sim.cli --scenario bms_sim/scenarios/demo.yaml
```

会按时间线依次执行：充电升压 → 注入单体过压故障（保护位置位、MOS 动作）→ 清故障 → 切换到放电 → 打印状态快照。场景文件格式见 `bms_sim/scenarios/demo.yaml`，可以照着写新场景（`wait_seconds` + `action`，`action` 就是一条控制台命令）。

## MOS 软件锁定（bit12）已知坑

真板子偶尔会出现"保护状态 bit12=1，充放开关怎么切都切不动"的情况，不是坏了，是软件锁定，需要按固定顺序解锁：

1. 先写 `0xE1 = 0x0003`（同时关闭充电和放电）；
2. 再写只开充电（bit1 清零、bit0 保持 1）；
3. 最后写开放电（bit0 也清零）。

顺序错了（比如先开放电、或者跳过第一步直接开）不会生效，模拟器复现了这个行为（见 `bms_sim/faults.py` 里的 `MosController`），方便上位机提前把这套解锁引导做进 UI。

## 目录说明

| 文件 | 内容 |
|---|---|
| `bms_sim/protocol.py` | 帧结构、校验算法、编解码、流式解码器 |
| `bms_sim/battery.py` | OCV-SOC 曲线、单体/整包模型、库仑计数、温度模型 |
| `bms_sim/faults.py` | 保护状态位、阈值评估、故障注入、MOS 控制与软件锁定状态机 |
| `bms_sim/device.py` | 命令分发，把协议命令映射到电池/故障模型的读写 |
| `bms_sim/server.py` | 真实串口收发主循环 |
| `bms_sim/cli.py` | 交互式控制台 + 场景 YAML 回放 |
| `bms_sim/scenarios/` | 示例场景 YAML |

## 真机校准清单

真机到手后，按 `../docs/阶段0_JBD-SP04S010_协议参考三表.md` 第四节的步骤核对，重点改这几处集中存放的常量：

- `bms_sim/battery.py` 顶部：额定容量、内阻、OCV 曲线锚点、热模型参数。
- `bms_sim/faults.py` 里的 `PROTECTION_THRESHOLDS`：各类保护的触发/恢复阈值。
- `bms_sim/faults.py` 里的 `FaultManager.error_count_bytes()`：0xAA 寄存器的数据格式目前是模拟器自定义的占位格式，真机抓包后按实际格式改。
- 电流符号方向（协议约定正=充电，真机验证后如果相反，只需要翻转 `bms_sim/device.py` 里电流钳零逻辑和 CLI 里的符号说明）。
```

- [ ] **Step 2: 提交**

```bash
git add README.md
git commit -m "docs: simulator README，虚拟串口两平台建法与使用/演示步骤"
```

---

### Task 11: 全量测试收尾

**Files:** 无新文件，仅验证。

- [ ] **Step 1: 跑全量测试套件**

Run: `python -m pytest -v`
Expected: 所有测试文件（`test_protocol.py`/`test_battery.py`/`test_faults.py`/`test_device.py`/`test_server.py`/`test_cli.py`）全部 PASS，无 skip/xfail。

- [ ] **Step 2: 确认黄金向量测试单独可见**

Run: `python -m pytest tests/test_protocol.py -k golden -v`
Expected: `test_checksum_request_golden_vector`、`test_checksum_response_golden_vector`、`test_encode_request_matches_golden_bytes`、`test_decode_request_golden_vector`、`test_decode_response_golden_vector`、`test_parse_basic_info_golden_vector` 全部 PASS。

- [ ] **Step 3: 更新根 README 的开发阶段勾选**

Modify: `../README.md`（仓库根目录，非 `simulator/README.md`）

把：
```markdown
- [ ] 阶段一 · 串口 BMS 模拟器（`simulator/`）
```
改成：
```markdown
- [x] 阶段一 · 串口 BMS 模拟器（`simulator/`）
```

- [ ] **Step 4: 提交**

```bash
git add ../README.md
git commit -m "docs: 阶段一（串口 BMS 模拟器）完成，勾选根 README 进度"
```

---

## Self-Review 备注（写计划时已核对，供执行者参考）

- **黄金向量覆盖**：Task 1/2 的测试直接取自协议三表 1.3 节的两条黄金向量，逐字段断言与文档表格一一对应。
- **两套校验覆盖范围不同**：`_checksum` 明确只吃"第二个头字节+长度+数据"，`checksum_request`/`checksum_response` 只是语义命名的包装，避免调用方把 0xDD/0x77 或错误的头字节算进去。
- **MOS 语义映射**：写寄存器 0xE1 的"关闭语义"和状态字节的"开启语义"只在 `MosController` 内部转换一次，`device.py` 和 `cli.py` 都只经过 `write_control(close_discharge=..., close_charge=...)` 这一个入口，不会有两处各自实现映射导致不一致。
- **MOS 锁定状态机**与文档"先关充放、再依次开充电/放电"的顺序完全对应，测试覆盖了正确顺序、两种错误顺序、直接尝试全开三种场景。
- **配置寄存器范围**按设计文档决定：只做 `0x00/0x01/0xAA/0xE1/0xE2`，`0x10~0xA2` 不实现，阈值集中放在 `faults.py` 的 `PROTECTION_THRESHOLDS`，README 的"真机校准清单"一节指明了这些常量的位置。
- **类型一致性**：`ProtectionBit`（faults.py）在 device.py/cli.py 里全程只用这一个类型引用保护位，没有另起字符串枚举；`BasicInfo`（protocol.py）字段名在 `parse_basic_info`/`encode_basic_info`/`device._build_basic_info` 三处保持一致。
