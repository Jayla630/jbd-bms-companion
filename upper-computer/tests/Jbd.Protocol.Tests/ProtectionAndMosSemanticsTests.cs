namespace Jbd.Protocol.Tests;

/// <summary>
/// 保护状态位图 → 可读标签（位序以 docs/ 2.3 为准），
/// 以及 0xE1 控制字（关闭语义）与 FET 状态字节（开启语义）"相反且位序不同"的专项钉死。
/// </summary>
public class ProtectionAndMosSemanticsTests
{
    [Fact]
    public void GetActiveLabels_NoBits_ReturnsEmpty()
    {
        Assert.Empty(JbdProtectionStatus.GetActiveLabels(0x0000));
    }

    [Fact]
    public void GetActiveLabels_KnownBits_MapPerDocsBitTable()
    {
        // bit0 单体过压 + bit9 放电过流 + bit10 短路
        var labels = JbdProtectionStatus.GetActiveLabels((ushort)((1 << 0) | (1 << 9) | (1 << 10)));

        Assert.Equal(["单体过压", "放电过流", "短路"], labels);
    }

    [Fact]
    public void GetActiveLabels_Bit12_IsMosSoftwareLock()
    {
        var labels = JbdProtectionStatus.GetActiveLabels(1 << 12);

        Assert.Equal(["MOS 软件锁定"], labels);
        Assert.True(JbdProtectionStatus.IsMosSoftwareLocked(1 << 12));
        Assert.False(JbdProtectionStatus.IsMosSoftwareLocked(0x0FFF));
    }

    [Fact]
    public void GetActiveLabels_ReservedBits_AreIgnored()
    {
        // bit13~15 保留位不产生标签
        Assert.Empty(JbdProtectionStatus.GetActiveLabels(0xE000));
    }

    [Fact]
    public void MosControlData_SemanticsAreOppositeOfFetStatus()
    {
        // 逻辑意图"只关放电、充电保持开"：
        // 0xE1 控制字（关闭语义）：bit0(关放电)=1、bit1(关充电)=0 → 0x0001
        byte[] data = JbdMosControl.BuildControlData(chargeOn: true, dischargeOn: false);
        Assert.Equal([0x00, 0x01], data);

        // 同一意图在 FET 状态字节（开启语义）里：bit0(充电开)=1、bit1(放电开)=0 → 0x01。
        // "关放电"在控制字是 bit0=1，在状态字节是 bit1=0——语义相反且位序不同，钉死防混用。
        var info = new BasicInfo(0, 0, 0, ProtectionStatus: 0, FetStatus: 0x01);
        Assert.True(info.ChargeMosOn);
        Assert.False(info.DischargeMosOn);
    }

    [Fact]
    public void MosControlData_AllCombinations()
    {
        // 全开 = 0x0000（docs/ 2.2），全关 = 0x0003
        Assert.Equal([0x00, 0x00], JbdMosControl.BuildControlData(chargeOn: true, dischargeOn: true));
        Assert.Equal([0x00, 0x03], JbdMosControl.BuildControlData(chargeOn: false, dischargeOn: false));
        // 只关充电：bit1=1 → 0x0002
        Assert.Equal([0x00, 0x02], JbdMosControl.BuildControlData(chargeOn: false, dischargeOn: true));
    }

    [Fact]
    public void BalanceControlData_NonZeroMeansOn()
    {
        // 对拍 simulator/bms_sim/device.py：REG_BALANCE_CONTROL 2 字节，非 0 即开
        Assert.Equal([0x00, 0x01], JbdMosControl.BuildBalanceData(on: true));
        Assert.Equal([0x00, 0x00], JbdMosControl.BuildBalanceData(on: false));
    }
}
