namespace Jbd.Protocol;

/// <summary>
/// 0xE1 MOS 控制字与 0xE2 均衡控制数据的构建。
/// ⚠ 语义陷阱（docs/ 2.2）：0xE1 写控制字是"关闭语义"——bit0=1 关放电、bit1=1 关充电
/// （0x0000 全开、0x0003 全关）；而 0x03 里的 FET 状态字节是"开启语义"——bit0=充电开、
/// bit1=放电开。两者语义相反且位序不同，此处统一从"目标开关状态"换算，别在别处手拼位。
/// </summary>
public static class JbdMosControl
{
    /// <summary>按目标状态构建 0xE1 的 2 字节控制字（大端）。</summary>
    public static byte[] BuildControlData(bool chargeOn, bool dischargeOn)
    {
        int word = (dischargeOn ? 0 : 0x01) | (chargeOn ? 0 : 0x02);
        return [0x00, (byte)word];
    }

    /// <summary>0xE2 均衡开关 2 字节数据：非 0 即开（与 simulator device.py 对拍）。</summary>
    public static byte[] BuildBalanceData(bool on) => [0x00, (byte)(on ? 0x01 : 0x00)];
}
