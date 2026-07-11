namespace Jbd.Protocol.Tests;

/// <summary>
/// 坏帧拒收测试：解析入口对任何一处校验失败都必须返回 false，而不是抛异常或给出半截数据。
/// 每个用例都从合法黄金帧出发只破坏一处，保证拒收原因明确。
/// </summary>
public class FrameRejectionTests
{
    private static byte[] GoldenFrame() =>
        Convert.FromHexString(BasicInfoParseTests.GoldenBasicInfoFrameHex);

    [Fact]
    public void CorruptedChecksum_IsRejected()
    {
        byte[] frame = GoldenFrame();
        frame[^2] ^= 0xFF;

        Assert.False(JbdFrame.TryParseBasicInfo(frame, out _));
    }

    [Fact]
    public void NonZeroStatus_IsRejected()
    {
        // 状态 0x80 = 设备报错帧（如未进配置模式读配置寄存器），即便校验和配套改对也要拒收。
        byte[] frame = GoldenFrame();
        frame[2] = 0x80;
        BasicInfoParseTests.FixupResponseChecksum(frame);

        Assert.False(JbdFrame.TryParseBasicInfo(frame, out _));
    }

    [Fact]
    public void LengthFieldMismatch_IsRejected()
    {
        byte[] frame = GoldenFrame();
        frame[3] = 0x1C; // 实际数据 29 字节，长度字段谎报 28

        Assert.False(JbdFrame.TryParseBasicInfo(frame, out _));
    }

    [Fact]
    public void MissingEndByte_IsRejected()
    {
        byte[] frame = GoldenFrame();
        frame[^1] = 0x00;

        Assert.False(JbdFrame.TryParseBasicInfo(frame, out _));
    }

    [Fact]
    public void MissingStartByte_IsRejected()
    {
        byte[] frame = GoldenFrame();
        frame[0] = 0x00;

        Assert.False(JbdFrame.TryParseBasicInfo(frame, out _));
    }

    [Fact]
    public void WrongRegisterEcho_IsRejected()
    {
        // 0x03 的帧喂给 0x04 解析器：寄存器回显对不上，必须拒收（轮询路由靠这个）。
        Assert.False(JbdFrame.TryParseCellVoltages(GoldenFrame(), out _));
    }

    [Fact]
    public void TruncatedFrame_IsRejected()
    {
        byte[] frame = GoldenFrame();

        Assert.False(JbdFrame.TryParseBasicInfo(frame.AsSpan(0, 6), out _));
        Assert.False(JbdFrame.TryParseBasicInfo(ReadOnlySpan<byte>.Empty, out _));
    }
}
