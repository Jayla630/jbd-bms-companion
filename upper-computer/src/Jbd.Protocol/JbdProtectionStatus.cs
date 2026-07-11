namespace Jbd.Protocol;

/// <summary>
/// 0x03 保护状态字（u16 位图）→ 可读标签。位序以 docs/ 2.3 保护状态位表为准：
/// bit0 单体过压 … bit12 MOS 软件锁定，bit13~15 保留。纯函数，UI 只做展示。
/// </summary>
public static class JbdProtectionStatus
{
    public const int MosSoftwareLockBit = 12;

    private static readonly string[] BitLabels =
    [
        "单体过压",          // bit0
        "单体欠压",          // bit1
        "整组过压",          // bit2
        "整组欠压",          // bit3
        "充电过温",          // bit4
        "充电低温",          // bit5
        "放电过温",          // bit6
        "放电低温",          // bit7
        "充电过流",          // bit8
        "放电过流",          // bit9
        "短路",              // bit10
        "前端采集 IC 错误",  // bit11
        "MOS 软件锁定",      // bit12
    ];

    /// <summary>按位序从低到高返回所有置位保护的可读标签；保留位（bit13~15）忽略。</summary>
    public static IReadOnlyList<string> GetActiveLabels(ushort protectionStatus)
    {
        var labels = new List<string>();
        for (int bit = 0; bit < BitLabels.Length; bit++)
        {
            if ((protectionStatus & (1 << bit)) != 0)
            {
                labels.Add(BitLabels[bit]);
            }
        }

        return labels;
    }

    public static bool IsMosSoftwareLocked(ushort protectionStatus)
        => (protectionStatus & (1 << MosSoftwareLockBit)) != 0;
}
