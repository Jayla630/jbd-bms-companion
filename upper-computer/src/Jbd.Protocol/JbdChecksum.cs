namespace Jbd.Protocol;

/// <summary>
/// JBD 协议校验和。请求帧与响应帧的覆盖范围不对称（docs/ 〇章）：
/// 请求帧 = 寄存器 + 长度 + 数据；响应帧 = 状态 + 长度 + 数据（不含被回显的寄存器字节）。
/// 两边都不计入起始 0xDD 与结束 0x77。结果 = 0x10000 − Σ，取低 16 位，高字节在前。
/// </summary>
public static class JbdChecksum
{
    public static ushort ComputeRequest(byte register, ReadOnlySpan<byte> data)
        => Compute(register, data);

    public static ushort ComputeResponse(byte status, ReadOnlySpan<byte> data)
        => Compute(status, data);

    private static ushort Compute(byte leadByte, ReadOnlySpan<byte> data)
    {
        int sum = leadByte + data.Length;
        foreach (byte b in data)
        {
            sum += b;
        }

        return (ushort)(0x10000 - sum);
    }
}
