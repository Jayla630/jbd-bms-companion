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
