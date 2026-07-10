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
