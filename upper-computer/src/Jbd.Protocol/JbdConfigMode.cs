namespace Jbd.Protocol;

/// <summary>
/// 工厂(配置)模式的机制帧与扫读地址表（docs/ 2.2）。
/// 本切片只做只读抓取：除进模式（0x00 写魔数）与退模式（0x01）两个机制写入外，
/// 不构建任何配置参数写帧。
/// </summary>
public static class JbdConfigMode
{
    public const byte RegEnterConfig = 0x00;
    public const byte RegExitSave = 0x01;
    public const byte RegDeviceName = 0x05;
    public const byte RegErrorCounts = 0xAA;

    /// <summary>扫读区间：0x10~0xA2 逐地址整段扫——哪些地址有效是"查真机"，以真板应答为准。</summary>
    public const byte ScanStart = 0x10;

    public const byte ScanEnd = 0xA2;

    public const ushort EnterMagic = 0x5678;

    /// <summary>进模式帧，docs 2.2 黄金向量：DD 5A 00 02 56 78 FF 30 77。</summary>
    public static byte[] BuildEnterFrame()
        => JbdFrame.BuildWrite(RegEnterConfig, [EnterMagic >> 8, EnterMagic & 0xFF]);

    /// <summary>
    /// 退模式帧（写 0x01）。docs 只记"0x01=退出并保存"，未记载数据载荷；
    /// 载荷 0x0000 是公开 JBD 通用协议里"退出、不固化 EEPROM"的通行值，尚无真机黄金向量，
    /// 真机行为按工单五由 Jayla 核清——因此上位机默认不发此帧，发了也会在抓取档原样记录。
    /// </summary>
    public static byte[] BuildExitFrame()
        => JbdFrame.BuildWrite(RegExitSave, [0x00, 0x00]);

    /// <summary>扫读地址表：0x10~0xA2 逐地址 + 0xAA（错误计数）。</summary>
    public static IReadOnlyList<byte> ScanRegisters { get; } = BuildScanRegisters();

    private static byte[] BuildScanRegisters()
    {
        var registers = new List<byte>();
        for (int r = ScanStart; r <= ScanEnd; r++)
        {
            registers.Add((byte)r);
        }

        registers.Add(RegErrorCounts);
        return [.. registers];
    }
}
