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
