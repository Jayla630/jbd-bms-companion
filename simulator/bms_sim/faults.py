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
