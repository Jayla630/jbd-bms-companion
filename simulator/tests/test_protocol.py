from datetime import date

from bms_sim import protocol as proto


def test_checksum_request_golden_vector():
    # DD A5 03 00 FF FD 77 : 寄存器=0x03，长度=0x00，无数据
    assert proto.checksum_request(register=0x03, length=0x00, data=b"") == 0xFFFD


def test_checksum_response_golden_vector():
    data = bytes.fromhex(
        "060B0000" "01ED01F4" "00002C7C" "00000000"
        "1000" "80" "63" "02" "04" "03"
        "0BA0" "0B9D" "0B98"
    )
    assert len(data) == 0x1D
    assert proto.checksum_response(status=0x00, length=0x1D, data=data) == 0xFA55


def test_encode_request_matches_golden_bytes():
    frame = proto.encode_request(proto.OP_READ, proto.REG_BASIC_INFO)
    assert frame == bytes.fromhex("DDA50300FFFD77")


def test_decode_request_golden_vector():
    frame = proto.decode_request(bytes.fromhex("DDA50300FFFD77"))
    assert frame.op == proto.OP_READ
    assert frame.register == proto.REG_BASIC_INFO
    assert frame.data == b""


def test_decode_response_golden_vector():
    raw = bytes.fromhex(
        "DD" "03" "00" "1D"
        "060B0000" "01ED01F4" "00002C7C" "00000000"
        "1000" "80" "63" "02" "04" "03"
        "0BA0" "0B9D" "0B98"
        "FA55" "77"
    )
    frame = proto.decode_response(raw)
    assert frame.register == proto.REG_BASIC_INFO
    assert frame.status == proto.STATUS_OK
    assert len(frame.data) == 0x1D


def test_frame_decoder_handles_split_packets():
    raw = bytes.fromhex("DDA50300FFFD77")
    decoder = proto.FrameDecoder("request")
    frames = decoder.feed(raw[:3])
    assert frames == []
    frames = decoder.feed(raw[3:])
    assert len(frames) == 1
    assert frames[0].register == proto.REG_BASIC_INFO


def test_frame_decoder_handles_concatenated_frames():
    single = bytes.fromhex("DDA50300FFFD77")
    decoder = proto.FrameDecoder("request")
    frames = decoder.feed(single + single)
    assert len(frames) == 2


def test_frame_decoder_resyncs_after_garbage():
    # 前两个字节是噪声；接着一个假的 0xDD 起始帧，长度字段=0x00 使其"凑得出"7字节但
    # 结尾不是 0x77，会被识别为假帧丢弃，而不是死等更多字节；随后能正确找到真帧。
    garbage = bytes([0x01, 0x02]) + bytes([0xDD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
    good = bytes.fromhex("DDA50300FFFD77")
    decoder = proto.FrameDecoder("request")
    frames = decoder.feed(garbage + good)
    assert len(frames) == 1
    assert frames[0].register == proto.REG_BASIC_INFO


GOLDEN_BASIC_INFO_DATA = bytes.fromhex(
    "060B0000" "01ED01F4" "00002C7C" "00000000"
    "1000" "80" "63" "02" "04" "03"
    "0BA0" "0B9D" "0B98"
)


def test_parse_basic_info_golden_vector():
    info = proto.parse_basic_info(GOLDEN_BASIC_INFO_DATA)
    assert info.total_voltage_v == 15.47
    assert info.current_a == 0.00
    assert info.remaining_capacity_ah == 4.93
    assert info.nominal_capacity_ah == 5.00
    assert info.cycles == 0
    assert info.production_date == date(2022, 3, 28)
    assert info.protection_status == 0x1000
    assert info.software_version == "8.0"
    assert info.soc_percent == 99
    assert info.mos_charge_on is False
    assert info.mos_discharge_on is True
    assert info.cell_count == 4
    assert info.ntc_count == 3
    assert info.temperatures_c == [24.5, 24.2, 23.7]


def test_encode_basic_info_round_trip():
    info = proto.parse_basic_info(GOLDEN_BASIC_INFO_DATA)
    assert proto.encode_basic_info(info) == GOLDEN_BASIC_INFO_DATA


def test_cell_voltages_round_trip():
    voltages_mv = [3450, 3460, 3440, 3455]
    data = proto.encode_cell_voltages(voltages_mv)
    assert data == bytes.fromhex("0D7A0D840D700D7F")
    assert proto.parse_cell_voltages(data) == voltages_mv


def test_device_name_response_is_ascii():
    frame = proto.encode_response(proto.REG_DEVICE_NAME, proto.STATUS_OK, b"JBD-SP04S010-Sim")
    decoded = proto.decode_response(frame)
    assert decoded.data.decode("ascii") == "JBD-SP04S010-Sim"
