namespace Jbd.Protocol;

/// <summary>
/// JBD 帧常量与请求帧构建。帧结构见 docs/ 一章。
/// </summary>
public static class JbdFrame
{
    public const byte Start = 0xDD;
    public const byte End = 0x77;
    public const byte OpRead = 0xA5;
    public const byte StatusOk = 0x00;

    public const byte RegBasicInfo = 0x03;
    public const byte RegCellVoltages = 0x04;

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

    /// <summary>解析 0x03 基础信息响应帧。校验不过或字段不完整返回 false。</summary>
    public static bool TryParseBasicInfo(ReadOnlySpan<byte> frame, out BasicInfo? info)
    {
        info = null;
        if (!TryGetResponseData(frame, RegBasicInfo, out var data) || data.Length < 20)
        {
            return false;
        }

        // 偏移与刻度见 docs/ 2.4：总电压 ×10mV，电流 s16 ×10mA，SOC 在偏移 19。
        double totalVoltageV = ReadUInt16(data, 0) / 100.0;
        double currentA = ReadInt16(data, 2) / 100.0;
        int socPercent = data[19];

        info = new BasicInfo(totalVoltageV, currentA, socPercent);
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
    /// 校验响应帧的公共骨架：起始/结束码、寄存器回显、状态 0x00、长度字段与实际一致、校验和匹配。
    /// 全部通过时给出数据区切片。
    /// </summary>
    private static bool TryGetResponseData(
        ReadOnlySpan<byte> frame, byte expectedRegister, out ReadOnlySpan<byte> data)
    {
        data = default;
        if (frame.Length < ResponseOverhead ||
            frame[0] != Start || frame[^1] != End ||
            frame[1] != expectedRegister || frame[2] != StatusOk ||
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

        data = payload;
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
        => (short)ReadUInt16(data, offset);
}
