namespace Jbd.Protocol;

/// <summary>
/// JBD 帧常量与请求帧构建。帧结构见 docs/ 一章。
/// </summary>
public static class JbdFrame
{
    public const byte Start = 0xDD;
    public const byte End = 0x77;
    public const byte OpRead = 0xA5;
    public const byte OpWrite = 0x5A;
    public const byte StatusOk = 0x00;

    public const byte RegBasicInfo = 0x03;
    public const byte RegCellVoltages = 0x04;
    public const byte RegMosControl = 0xE1;
    public const byte RegBalanceControl = 0xE2;

    /// <summary>响应帧总长 = 长度字段 + 7（DD reg status len [data] csum_hi csum_lo 77）。</summary>
    public const int ResponseOverhead = 7;

    /// <summary>构建纯读命令帧（长度 0x00，无数据）。</summary>
    public static byte[] BuildRead(byte register)
    {
        ushort checksum = JbdChecksum.ComputeRequest(register, ReadOnlySpan<byte>.Empty);
        return
        [
            Start, OpRead, register, 0x00,
            (byte)(checksum >> 8), (byte)(checksum & 0xFF),
            End,
        ];
    }

    /// <summary>构建写命令帧（DD 5A reg len data csum 77）。0xE1/0xE2 无需先进配置模式，可直接写。</summary>
    public static byte[] BuildWrite(byte register, ReadOnlySpan<byte> data)
    {
        ushort checksum = JbdChecksum.ComputeRequest(register, data);
        byte[] frame = new byte[data.Length + 7];
        frame[0] = Start;
        frame[1] = OpWrite;
        frame[2] = register;
        frame[3] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(4));
        frame[^3] = (byte)(checksum >> 8);
        frame[^2] = (byte)(checksum & 0xFF);
        frame[^1] = End;
        return frame;
    }

    /// <summary>
    /// 解析写响应 ack。返回 true = 帧结构合法（含校验和）；accepted = 状态字 0x00（设备受理）。
    /// 状态 0x80 等非 0 值是"设备拒绝"，帧合法但 accepted=false，不抛异常。
    /// </summary>
    public static bool TryParseWriteAck(ReadOnlySpan<byte> frame, byte expectedRegister, out bool accepted)
    {
        bool valid = TryGetResponse(frame, expectedRegister, out byte status, out _);
        accepted = valid && status == StatusOk;
        return valid;
    }

    /// <summary>
    /// 状态无关地解析响应帧：结构与校验合法即返回 true，状态字（0x00/0x80/其它）与原始
    /// 数据交给调用方记录——配置模式抓取要求 0x80 拒绝帧同样原样入档，不在这里筛掉。
    /// </summary>
    public static bool TryParseRawResponse(
        ReadOnlySpan<byte> frame, byte expectedRegister, out byte status, out byte[] data)
    {
        bool valid = TryGetResponse(frame, expectedRegister, out status, out var payload);
        data = valid ? payload.ToArray() : [];
        return valid;
    }

    /// <summary>解析 0x03 基础信息响应帧。校验不过或字段不完整返回 false。</summary>
    public static bool TryParseBasicInfo(ReadOnlySpan<byte> frame, out BasicInfo? info)
    {
        info = null;
        if (!TryGetResponseData(frame, RegBasicInfo, out var data) || data.Length < 21)
        {
            return false;
        }

        // 偏移与刻度见 docs/ 2.4：总电压 ×10mV，电流 s16 ×10mA，
        // 保护状态 u16 在偏移 16，SOC 在偏移 19，MOS(FET) 状态在偏移 20。
        info = new BasicInfo(
            TotalVoltageV: ReadUInt16(data, 0) / 100.0,
            CurrentA: ReadInt16(data, 2) / 100.0,
            SocPercent: data[19],
            ProtectionStatus: ReadUInt16(data, 16),
            FetStatus: data[20]);
        return true;
    }

    /// <summary>解析 0x04 单体电压响应帧（N × u16 大端，单位 mV）。校验不过返回 false。</summary>
    public static bool TryParseCellVoltages(ReadOnlySpan<byte> frame, out CellVoltages? cells)
    {
        cells = null;
        if (!TryGetResponseData(frame, RegCellVoltages, out var data) ||
            data.Length == 0 || data.Length % 2 != 0)
        {
            return false;
        }

        var voltages = new double[data.Length / 2];
        for (int i = 0; i < voltages.Length; i++)
        {
            voltages[i] = ReadUInt16(data, i * 2) / 1000.0;
        }

        cells = new CellVoltages(voltages);
        return true;
    }

    /// <summary>
    /// 校验响应帧的公共骨架（状态无关）：起始/结束码、寄存器回显、长度字段与实际一致、
    /// 校验和匹配（校验和覆盖含实际状态字节，0x80 拒绝帧同样能过结构校验）。
    /// </summary>
    private static bool TryGetResponse(
        ReadOnlySpan<byte> frame, byte expectedRegister, out byte status, out ReadOnlySpan<byte> data)
    {
        status = 0;
        data = default;
        if (frame.Length < ResponseOverhead ||
            frame[0] != Start || frame[^1] != End ||
            frame[1] != expectedRegister ||
            frame[3] != frame.Length - ResponseOverhead)
        {
            return false;
        }

        var payload = frame.Slice(4, frame[3]);
        ushort expectedChecksum = JbdChecksum.ComputeResponse(frame[2], payload);
        if (frame[^3] != (byte)(expectedChecksum >> 8) || frame[^2] != (byte)(expectedChecksum & 0xFF))
        {
            return false;
        }

        status = frame[2];
        data = payload;
        return true;
    }

    /// <summary>读响应骨架：结构合法且状态字必须为 0x00（非 0 视为设备报错帧）。</summary>
    private static bool TryGetResponseData(
        ReadOnlySpan<byte> frame, byte expectedRegister, out ReadOnlySpan<byte> data)
        => TryGetResponse(frame, expectedRegister, out byte status, out data) && status == StatusOk;

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
        => (short)ReadUInt16(data, offset);
}
