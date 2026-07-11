namespace Jbd.Protocol.Tests;

/// <summary>
/// 帧累积器测试：串口数据分包、粘包、夹杂噪声时都要能切出完整帧，
/// 用例与 simulator/tests/test_protocol.py 的 FrameDecoder 三个场景对齐。
/// </summary>
public class FrameAccumulatorTests
{
    private static byte[] GoldenBasicInfoFrame() =>
        Convert.FromHexString(BasicInfoParseTests.GoldenBasicInfoFrameHex);

    [Fact]
    public void Feed_SplitAcrossTwoChunks_YieldsOneFrame()
    {
        byte[] raw = GoldenBasicInfoFrame();
        var accumulator = new FrameAccumulator();

        Assert.Empty(accumulator.Feed(raw.AsSpan(0, 10)));

        var frames = accumulator.Feed(raw.AsSpan(10));
        Assert.Single(frames);
        Assert.Equal(raw, frames[0]);
    }

    [Fact]
    public void Feed_TwoConcatenatedFrames_YieldsBoth()
    {
        byte[] basicInfo = GoldenBasicInfoFrame();
        byte[] cells = Convert.FromHexString(CellVoltagesParseTests.GoldenCellVoltagesFrameHex);
        var accumulator = new FrameAccumulator();

        var frames = accumulator.Feed([.. basicInfo, .. cells]);

        Assert.Equal(2, frames.Count);
        Assert.Equal(basicInfo, frames[0]);
        Assert.Equal(cells, frames[1]);
    }

    [Fact]
    public void Feed_GarbageThenFakeStartThenGoodFrame_ResyncsAndYieldsGoodFrame()
    {
        // 两个噪声字节 + 一个"凑得出长度但结尾不是 0x77"的假帧 + 真帧：
        // 累积器必须丢掉假帧重新对齐，而不是把缓冲卡死。
        byte[] garbage = [0x01, 0x02, 0xDD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] good = GoldenBasicInfoFrame();
        var accumulator = new FrameAccumulator();

        var frames = accumulator.Feed([.. garbage, .. good]);

        Assert.Single(frames);
        Assert.Equal(good, frames[0]);
    }

    [Fact]
    public void Feed_AbsurdLengthField_IsDroppedAndResyncs()
    {
        // 长度字段离谱（超过上限）时不能傻等后续字节，要丢弃并继续找下一个起始码。
        byte[] absurd = [0xDD, 0x03, 0x00, 0xFF];
        byte[] good = GoldenBasicInfoFrame();
        var accumulator = new FrameAccumulator();

        Assert.Empty(accumulator.Feed(absurd));

        var frames = accumulator.Feed(good);
        Assert.Single(frames);
        Assert.Equal(good, frames[0]);
    }
}
