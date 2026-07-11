namespace Jbd.Protocol.Tests;

/// <summary>
/// 0x03 响应解析测试。基准帧是 docs/ 1.3 那条真实抓包黄金向量（36 字节，4 串 / 3 温感），
/// 期望值与 simulator/tests/test_protocol.py 保持一致。
/// </summary>
public class BasicInfoParseTests
{
    // DD 03 00 1D [29 字节数据] FA 55 77
    internal const string GoldenBasicInfoFrameHex =
        "DD" + "03" + "00" + "1D" +
        "060B0000" + "01ED01F4" + "00002C7C" + "00000000" +
        "1000" + "80" + "63" + "02" + "04" + "03" +
        "0BA0" + "0B9D" + "0B98" +
        "FA55" + "77";

    [Fact]
    public void TryParseBasicInfo_GoldenVector_YieldsKnownValues()
    {
        byte[] frame = Convert.FromHexString(GoldenBasicInfoFrameHex);

        Assert.True(JbdFrame.TryParseBasicInfo(frame, out var info));
        Assert.Equal(15.47, info!.TotalVoltageV);
        Assert.Equal(0.00, info.CurrentA);
        Assert.Equal(99, info.SocPercent);
    }

    [Fact]
    public void TryParseBasicInfo_DischargeCurrent_IsNegative()
    {
        // 电流是有符号 s16：-1.50 A = -150 × 10 mA = 0xFF6A。
        // 用无符号解读会得到 +653.86 A，这个用例专防这个坑。
        byte[] frame = Convert.FromHexString(GoldenBasicInfoFrameHex);
        frame[6] = 0xFF;
        frame[7] = 0x6A;
        FixupResponseChecksum(frame);

        Assert.True(JbdFrame.TryParseBasicInfo(frame, out var info));
        Assert.Equal(-1.50, info!.CurrentA);
    }

    /// <summary>按响应帧覆盖范围（状态+长度+数据）重算并回填校验和。</summary>
    internal static void FixupResponseChecksum(byte[] frame)
    {
        int dataLength = frame[3];
        ushort checksum = JbdChecksum.ComputeResponse(frame[2], frame.AsSpan(4, dataLength));
        frame[^3] = (byte)(checksum >> 8);
        frame[^2] = (byte)(checksum & 0xFF);
    }
}
