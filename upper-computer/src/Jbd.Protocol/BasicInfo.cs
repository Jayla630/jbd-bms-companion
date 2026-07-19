namespace Jbd.Protocol;

/// <summary>
/// 0x03 基础信息（已换算成工程单位）。
/// FetStatus 是"开启语义"（docs/ 2.4：bit0=充电开、bit1=放电开），
/// 与 0xE1 写控制字（关闭语义）同位序、仅极性相反（真机勘误，参照 c1f4ed4），
/// 换算见 <see cref="JbdMosControl"/>。
/// </summary>
public sealed record BasicInfo(
    double TotalVoltageV,
    double CurrentA,
    int SocPercent,
    ushort ProtectionStatus,
    byte FetStatus)
{
    public bool ChargeMosOn => (FetStatus & 0x01) != 0;

    public bool DischargeMosOn => (FetStatus & 0x02) != 0;

    /// <summary>保护状态 bit12 = MOS 软件锁定（docs/ 2.3）。</summary>
    public bool MosSoftwareLocked => (ProtectionStatus & (1 << 12)) != 0;
}
