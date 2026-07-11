"""把协议命令分发到电池/故障模型，唯一知道"寄存器语义"的胶水层。

线程模型：server 的串口线程调 handle_request()，控制台 stdin 线程调命令方法，
两边共享同一个 Device。所有对外入口都在同一把 RLock 内执行，保证 faults 的
evaluate()（handle_request 推进时）与 inject()/_manual_bits 读写不会互相插入。
"""
from __future__ import annotations

import threading
import time
from datetime import date

from . import protocol as proto
from .battery import DEFAULT_RATED_CAPACITY_MAH, BatteryPack, ThermalModel
from .faults import FaultManager, MosController, ProtectionBit

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
        self._lock = threading.RLock()

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

    # --- 控制台命令入口：外部（cli/server 控制台）改状态一律走这些方法，
    # --- 不要直接戳 battery/faults/mos/thermal，线程安全靠这层收口保证。

    def set_current_ma(self, value: float) -> None:
        with self._lock:
            self.battery.set_current_ma(value)

    def set_soc_percent(self, value: float) -> None:
        with self._lock:
            self.battery.set_soc_percent(value)

    def inject_fault(self, bit: ProtectionBit) -> None:
        with self._lock:
            self.faults.inject(bit)

    def clear_fault(self, bit: ProtectionBit) -> None:
        with self._lock:
            self.faults.clear(bit)

    def write_mos_control(self, *, close_discharge: bool, close_charge: bool) -> None:
        with self._lock:
            self.mos.write_control(close_discharge=close_discharge, close_charge=close_charge)
            self.faults.sync_mos_lock()

    def set_core_temp_c(self, value: float) -> None:
        with self._lock:
            self.thermal.core_temp_c = value

    def status_snapshot(self) -> dict:
        """推进模型后取一份状态快照（控制台展示用）。"""
        with self._lock:
            self._advance()
            return {
                "total_voltage_v": self.battery.total_voltage_v,
                "current_ma": self.battery.current_ma,
                "soc_percent": self.battery.average_soc_percent,
                "temperatures_c": list(self.thermal.ntc_temperatures_c),
                "protection_status": self.faults.protection_status,
                "mos_charge_on": self.mos.charge_enabled,
                "mos_discharge_on": self.mos.discharge_enabled,
            }

    def handle_request(self, frame: proto.RequestFrame) -> bytes:
        with self._lock:
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
            self.faults.sync_mos_lock()
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
