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
