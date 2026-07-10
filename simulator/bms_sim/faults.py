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
