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
    # handle_command 末尾会调用 print_status -> _advance()，经过极短的真实时间会有
    # 微小的散热漂移，所以这里用近似比较而不是严格相等。
    assert abs(device.thermal.core_temp_c - 60.0) < 0.1


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
    assert device.faults.is_set(ProtectionBit.CELL_OVERVOLTAGE)
    # 单体过压故障会关闭充电 MOS，充电电流因此被钳零（贴近真板行为）。
    assert device.mos.charge_enabled is False
    assert device.battery.current_ma == 0.0
