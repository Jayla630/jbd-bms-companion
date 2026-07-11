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
}
