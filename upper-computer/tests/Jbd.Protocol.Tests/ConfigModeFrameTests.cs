namespace Jbd.Protocol.Tests;

/// <summary>
/// 配置模式机制帧与原始响应解析测试。
/// 进模式帧是 docs/ 2.2 的黄金向量；退出帧载荷 docs 未记载（非黄金向量，仅校验自洽结构，
/// 真机行为按工单五另核）；0xA5 读帧对任意寄存器地址复用按同一校验规则自洽验证。
/// </summary>
public class ConfigModeFrameTests
{
    [Fact]
    public void BuildEnterFrame_MatchesDocsGoldenVector()
    {
        // docs/ 2.2：DD 5A 00 02 56 78 FF 30 77（向 0x00 写魔数 0x5678）
        byte[] expected = [0xDD, 0x5A, 0x00, 0x02, 0x56, 0x78, 0xFF, 0x30, 0x77];

        Assert.Equal(expected, JbdConfigMode.BuildEnterFrame());
    }

    [Fact]
    public void BuildRead_ArbitraryConfigRegister0x10_ChecksumFollowsRequestRule()
    {
        // 0x10000 - (0x10 + 0x00) = 0xFFF0：现有 0xA5 读路径对任意地址直接复用
        byte[] expected = [0xDD, 0xA5, 0x10, 0x00, 0xFF, 0xF0, 0x77];

        Assert.Equal(expected, JbdFrame.BuildRead(0x10));
    }

    [Fact]
    public void BuildRead_ErrorCounts0xAA_ChecksumFollowsRequestRule()
    {
        // 0x10000 - 0xAA = 0xFF56
        byte[] expected = [0xDD, 0xA5, 0xAA, 0x00, 0xFF, 0x56, 0x77];

        Assert.Equal(expected, JbdFrame.BuildRead(JbdConfigMode.RegErrorCounts));
    }

    [Fact]
    public void BuildExitFrame_SelfConsistentStructure_NotAGoldenVector()
    {
        // 载荷 0x0000（docs 未记载退出载荷，取公开通行值，真机待核）：
        // 0x10000 - (0x01 + 0x02 + 0x00 + 0x00) = 0xFFFD
        byte[] expected = [0xDD, 0x5A, 0x01, 0x02, 0x00, 0x00, 0xFF, 0xFD, 0x77];

        Assert.Equal(expected, JbdConfigMode.BuildExitFrame());
    }

    [Fact]
    public void ScanRegisters_CoverFullRangePlusErrorCounts()
    {
        var registers = JbdConfigMode.ScanRegisters;

        // 0x10~0xA2 共 147 个逐地址 + 0xAA
        Assert.Equal(148, registers.Count);
        Assert.Equal(0x10, registers[0]);
        Assert.Equal(0xA2, registers[^2]);
        Assert.Equal(0xAA, registers[^1]);
    }

    [Fact]
    public void TryParseRawResponse_RejectedStatus0x80_ParsesWithStatusPreserved()
    {
        // 未进模式读配置寄存器回 0x80：结构合法帧，状态原样交给调用方入档
        byte[] frame = [0xDD, 0xAA, 0x80, 0x00, 0xFF, 0x80, 0x77];

        Assert.True(JbdFrame.TryParseRawResponse(frame, 0xAA, out byte status, out byte[] data));
        Assert.Equal(0x80, status);
        Assert.Empty(data);
    }

    [Fact]
    public void TryParseRawResponse_OkStatusWithData_ReturnsPayload()
    {
        // 0x10000 - (0x00 + 0x02 + 0x12 + 0x34) = 0xFFB8
        byte[] frame = [0xDD, 0x10, 0x00, 0x02, 0x12, 0x34, 0xFF, 0xB8, 0x77];

        Assert.True(JbdFrame.TryParseRawResponse(frame, 0x10, out byte status, out byte[] data));
        Assert.Equal(0x00, status);
        Assert.Equal([0x12, 0x34], data);
    }

    [Fact]
    public void TryParseRawResponse_CorruptedChecksum_IsRejected()
    {
        byte[] frame = [0xDD, 0x10, 0x00, 0x02, 0x12, 0x34, 0x12, 0x34, 0x77];

        Assert.False(JbdFrame.TryParseRawResponse(frame, 0x10, out _, out _));
    }

    [Fact]
    public void TryParseRawResponse_WrongRegisterEcho_IsRejected()
    {
        byte[] frame = [0xDD, 0xAA, 0x80, 0x00, 0xFF, 0x80, 0x77];

        Assert.False(JbdFrame.TryParseRawResponse(frame, 0x10, out _, out _));
    }
}
