namespace Jbd.Protocol.Tests;

/// <summary>
/// 0x04 响应解析测试。电压值与 simulator/tests/test_protocol.py 的
/// test_cell_voltages_round_trip（3450/3460/3440/3455 mV）保持一致。
/// </summary>
public class CellVoltagesParseTests
{
    // DD 04 00 08 [4 × u16 mV] FD D7 77
    // 校验：0x10000 - (0x00 + 0x08 + Σ数据) = 0x10000 - 0x0229 = 0xFDD7
    internal const string GoldenCellVoltagesFrameHex =
        "DD" + "04" + "00" + "08" + "0D7A0D840D700D7F" + "FDD7" + "77";

    [Fact]
    public void TryParseCellVoltages_GoldenVector_YieldsFourCells()
    {
        byte[] frame = Convert.FromHexString(GoldenCellVoltagesFrameHex);

        Assert.True(JbdFrame.TryParseCellVoltages(frame, out var cells));
        Assert.Equal([3.450, 3.460, 3.440, 3.455], cells!.CellVoltagesV);
    }

    [Fact]
    public void TryParseCellVoltages_OddDataLength_IsRejected()
    {
        // 单体电压必须是 2 字节一组，奇数长度是坏帧。校验和本身是对的（0xFF69），
        // 确保拒收原因是长度而不是校验。
        byte[] frame = Convert.FromHexString("DD" + "04" + "00" + "03" + "0D7A0D" + "FF69" + "77");

        Assert.False(JbdFrame.TryParseCellVoltages(frame, out _));
    }
}
