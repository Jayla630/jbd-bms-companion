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
