namespace Jbd.Protocol.Tests;

/// <summary>
/// 请求帧构建的黄金向量测试，断言值与 docs/ 三表及 simulator/ 的 pytest 保持一致，互为第二意见。
/// </summary>
public class JbdFrameBuildTests
{
    [Fact]
    public void BuildRead_BasicInfo_MatchesGoldenVector()
    {
        // docs/ 1.3 黄金向量：DD A5 03 00 FF FD 77
        byte[] expected = [0xDD, 0xA5, 0x03, 0x00, 0xFF, 0xFD, 0x77];

        Assert.Equal(expected, JbdFrame.BuildRead(0x03));
    }

    [Fact]
    public void BuildRead_CellVoltages_ChecksumFollowsSameRule()
    {
        // 0x10000 - (0x04 + 0x00) = 0xFFFC
        byte[] expected = [0xDD, 0xA5, 0x04, 0x00, 0xFF, 0xFC, 0x77];

        Assert.Equal(expected, JbdFrame.BuildRead(0x04));
    }

    [Fact]
    public void ChecksumRequest_GoldenVector()
    {
        Assert.Equal(0xFFFD, JbdChecksum.ComputeRequest(0x03, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ChecksumResponse_GoldenVector()
    {
        // docs/ 1.3：0x03 响应黄金向量，长度 0x1D + Σ数据 = 0x05AB，0x10000 - 0x05AB = 0xFA55
        byte[] data = Convert.FromHexString(
            "060B0000" + "01ED01F4" + "00002C7C" + "00000000" +
            "1000" + "80" + "63" + "02" + "04" + "03" +
            "0BA0" + "0B9D" + "0B98");

        Assert.Equal(0x1D, data.Length);
        Assert.Equal(0xFA55, JbdChecksum.ComputeResponse(0x00, data));
    }
}
