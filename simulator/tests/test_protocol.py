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
