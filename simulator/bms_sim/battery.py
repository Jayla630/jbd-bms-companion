"""电池仿真模型：OCV-SOC 曲线、单体/整包状态、库仑计数、内阻压降。

真机校准点集中在本文件顶部的常量区，真机到手后按 docs 里的"校准清单"替换。
"""
from __future__ import annotations

from dataclasses import dataclass

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
        """端电压 = OCV + I*R（I 为正表示充电电流流入，端电压略高于 OCV；
        放电时 I 为负，端电压略低于 OCV）。
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
