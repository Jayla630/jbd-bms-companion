namespace Jbd.Protocol;

/// <summary>
/// 0xE1 MOS 控制字与 0xE2 均衡控制数据的构建。
/// ⚠ 2026-07-18 真机（SP04S010）实测勘误（参照小程序 c1f4ed4）：0xE1 写控制字是
/// "关闭语义"——bit0=1 关充电、bit1=1 关放电（0x0000 全开、0x0003 全关），与 0x03 里的
/// FET 状态字节（开启语义：bit0=充电开、bit1=放电开）**同位序**、仅使能/关断极性相反。
/// docs 三表原先记的"bit0=关放电、读写位对换"是错的（真机发 0x0002 断的是放电负载）。
/// 此处统一从"目标开关状态"换算，别在别处手拼位。
/// </summary>
public static class JbdMosControl
{
    /// <summary>按目标状态构建 0xE1 的 2 字节控制字（大端）。</summary>
    public static byte[] BuildControlData(bool chargeOn, bool dischargeOn)
    {
        int word = (chargeOn ? 0 : 0x01) | (dischargeOn ? 0 : 0x02);
        return [0x00, (byte)word];
    }

    /// <summary>0xE2 均衡开关 2 字节数据：非 0 即开（与 simulator device.py 对拍）。</summary>
    public static byte[] BuildBalanceData(bool on) => [0x00, (byte)(on ? 0x01 : 0x00)];

    /// <summary>解锁序列固定三步（<see cref="BuildUnlockSequence"/>）。</summary>
    public const int UnlockStepCount = 3;

    /// <summary>
    /// bit12 软件锁定的引导式解锁序列：按序返回三个 0xE1 控制字（各 2 字节大端）。
    /// 原始值 0x0003 → 0x0001 → 0x0000 固定不变；按真机勘误后的位序，步骤含义为
    /// ① 全关（幂等起点，任何残留状态重跑都安全）→ ② 0x0001 开放电、充电仍关 →
    /// ③ 0x0000 两路全开、锁定解除。与 simulator faults.py 的 MosController 状态机
    /// 对拍——模拟器对错序写入静默忽略。真机另证（c1f4ed4）：单写 0x0000 即可清
    /// bit12、锁定态写不被拒；三步序列保留是为与小程序/模拟器的引导式演示一条线。
    /// </summary>
    public static byte[][] BuildUnlockSequence() =>
    [
        BuildControlData(chargeOn: false, dischargeOn: false), // ① 0x0003 全关
        BuildControlData(chargeOn: false, dischargeOn: true),  // ② 0x0001 先开放电
        BuildControlData(chargeOn: true, dischargeOn: true),   // ③ 0x0000 再开充电
    ];
}
