namespace Jbd.Protocol.Tests;

/// <summary>
/// bit12 软件锁定的引导式解锁序列。原始值 0x0003→0x0001→0x0000 固定不变;步骤含义
/// 按真机勘误后位序(bit0=关充、bit1=关放,参照 c1f4ed4)重标:与 simulator faults.py 的
/// MosController 状态机对拍——LOCKED→(两路全关)→CLOSED_BOTH→(开放电)→DISCHARGE_OPENED
/// →(开充电)→UNLOCKED,错序写入被模拟器静默忽略。这里把三步的顺序、取值、语义、
/// 完整 0xE1 写帧逐字节钉死,防回归把顺序或极性写反。
/// </summary>
public class MosUnlockSequenceTests
{
    [Fact]
    public void BuildUnlockSequence_StepsAreOrderedAndExactValues()
    {
        byte[][] steps = JbdMosControl.BuildUnlockSequence();

        // 0xE1 关闭语义(真机勘误后):0x0003 全关 → 0x0001 关充开放 → 0x0000 全开
        Assert.Equal(3, steps.Length);
        Assert.Equal([0x00, 0x03], steps[0]);
        Assert.Equal([0x00, 0x01], steps[1]);
        Assert.Equal([0x00, 0x00], steps[2]);
    }

    [Fact]
    public void BuildUnlockSequence_StepSemanticsMatchCloseSemantics()
    {
        byte[][] steps = JbdMosControl.BuildUnlockSequence();

        // 步骤1:两路全关(建立幂等起点,任何残留状态重跑都安全)
        Assert.Equal(JbdMosControl.BuildControlData(chargeOn: false, dischargeOn: false), steps[0]);
        // 步骤2:开放电、充电仍关(0x0001,真机勘误后含义;顺序反了会被模拟器静默忽略)
        Assert.Equal(JbdMosControl.BuildControlData(chargeOn: false, dischargeOn: true), steps[1]);
        // 步骤3:两路全开,锁定解除
        Assert.Equal(JbdMosControl.BuildControlData(chargeOn: true, dischargeOn: true), steps[2]);
    }

    [Fact]
    public void BuildUnlockSequence_FullWriteFramesByteExact()
    {
        byte[][] steps = JbdMosControl.BuildUnlockSequence();

        // 步骤1 即 docs/ 2.2 黄金向量(全关);后两帧校验和按请求规则递推
        Assert.Equal(
            [0xDD, 0x5A, 0xE1, 0x02, 0x00, 0x03, 0xFF, 0x1A, 0x77],
            JbdFrame.BuildWrite(JbdFrame.RegMosControl, steps[0]));
        Assert.Equal(
            [0xDD, 0x5A, 0xE1, 0x02, 0x00, 0x01, 0xFF, 0x1C, 0x77],
            JbdFrame.BuildWrite(JbdFrame.RegMosControl, steps[1]));
        Assert.Equal(
            [0xDD, 0x5A, 0xE1, 0x02, 0x00, 0x00, 0xFF, 0x1D, 0x77],
            JbdFrame.BuildWrite(JbdFrame.RegMosControl, steps[2]));
    }
}
