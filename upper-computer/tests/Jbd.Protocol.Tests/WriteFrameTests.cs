namespace Jbd.Protocol.Tests;

/// <summary>
/// 写命令帧（0x5A）构建与写响应 ack 解析测试。
/// 0xE1 全关帧是 docs/ 2.2 的黄金向量；0xE2 帧与 simulator/bms_sim/device.py 的
/// REG_BALANCE_CONTROL 处理（2 字节、非 0 即开）对拍。
/// </summary>
public class WriteFrameTests
{
    [Fact]
    public void BuildWrite_MosControlAllOff_MatchesDocsGoldenVector()
    {
        // docs/ 2.2：DD 5A E1 02 00 03 FF 1A 77（0x0003 = 关放电 + 关充电）
        byte[] expected = [0xDD, 0x5A, 0xE1, 0x02, 0x00, 0x03, 0xFF, 0x1A, 0x77];

        Assert.Equal(expected, JbdFrame.BuildWrite(0xE1, [0x00, 0x03]));
    }

    [Fact]
    public void BuildWrite_BalanceOn_ChecksumFollowsRequestRule()
    {
        // 0x10000 - (0xE2 + 0x02 + 0x00 + 0x01) = 0xFF1B
        byte[] expected = [0xDD, 0x5A, 0xE2, 0x02, 0x00, 0x01, 0xFF, 0x1B, 0x77];

        Assert.Equal(expected, JbdFrame.BuildWrite(0xE2, [0x00, 0x01]));
    }

    [Fact]
    public void TryParseWriteAck_AcceptedAck_ReturnsTrueAndAccepted()
    {
        // 状态 0x00、空数据：校验和 = 0x10000 - 0 → 低 16 位 0x0000
        byte[] frame = [0xDD, 0xE1, 0x00, 0x00, 0x00, 0x00, 0x77];

        Assert.True(JbdFrame.TryParseWriteAck(frame, 0xE1, out bool accepted));
        Assert.True(accepted);
    }

    [Fact]
    public void TryParseWriteAck_RejectedStatus0x80_ReturnsTrueButNotAccepted()
    {
        // 设备拒绝（0x80）也是结构合法的响应帧，解析成功但 accepted=false，不抛异常
        byte[] frame = [0xDD, 0xE1, 0x80, 0x00, 0xFF, 0x80, 0x77];

        Assert.True(JbdFrame.TryParseWriteAck(frame, 0xE1, out bool accepted));
        Assert.False(accepted);
    }

    [Fact]
    public void TryParseWriteAck_CorruptedChecksum_IsRejected()
    {
        byte[] frame = [0xDD, 0xE1, 0x00, 0x00, 0x12, 0x34, 0x77];

        Assert.False(JbdFrame.TryParseWriteAck(frame, 0xE1, out _));
    }

    [Fact]
    public void TryParseWriteAck_WrongRegisterEcho_IsRejected()
    {
        byte[] frame = [0xDD, 0xE1, 0x00, 0x00, 0x00, 0x00, 0x77];

        Assert.False(JbdFrame.TryParseWriteAck(frame, 0xE2, out _));
    }
}
