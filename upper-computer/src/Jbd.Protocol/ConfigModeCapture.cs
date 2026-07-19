using System.Text;

namespace Jbd.Protocol;

/// <summary>单条抓取结果的响应形态。</summary>
public enum CaptureOutcome
{
    /// <summary>收到结构合法的响应帧（状态字任意，0x80 拒绝也算 Ok——原样入档）。</summary>
    Ok,

    /// <summary>超时窗口内无应答。</summary>
    NoResponse,
}

/// <summary>
/// 单个寄存器的原始抓取结果：只记事实，不解释。
/// <paramref name="Status"/>/<paramref name="Data"/> 仅在 <see cref="CaptureOutcome.Ok"/> 时有意义。
/// </summary>
public sealed record ConfigCaptureEntry(byte Register, CaptureOutcome Outcome, byte Status, byte[] Data)
{
    public bool IsOk => Outcome == CaptureOutcome.Ok && Status == JbdFrame.StatusOk;

    public string StatusText => Outcome == CaptureOutcome.NoResponse ? "无应答" : $"0x{Status:X2}";

    public string DataHex => Outcome == CaptureOutcome.NoResponse
        ? "–"
        : Data.Length == 0 ? "(空)" : Convert.ToHexString(Data);
}

/// <summary>
/// 一次配置模式只读抓取的完整报告。正文只有原始事实（寄存器/状态/长度/hex），
/// 头部元数据（链路、退出策略结论）由调用方补填。ToMarkdown 输出工单七约定的抓取档格式。
/// </summary>
public sealed class ConfigCaptureReport
{
    public DateTime CapturedAt { get; set; } = DateTime.Now;

    /// <summary>链路描述（如 "COM3 @ 9600 8N1（RS485）"），由传输层填。</summary>
    public string Link { get; set; } = "(未填)";

    /// <summary>退出策略与其真机结论（工单五的交付物之一），由传输层/Jayla 补填。</summary>
    public string ExitStrategyNote { get; set; } = "(未记录)";

    /// <summary>0x05 设备名（不受配置模式限制，进模式前读，给抓取档盖身份戳）。</summary>
    public ConfigCaptureEntry? DeviceName { get; set; }

    // --- 模式门自检：进模式前后各读一次 0x10 与 0xAA。真板上按工单以 0x10 为准；
    // --- 模拟器只对 0xAA 设了 0x80 守卫（0x10 未实现恒回 0x80），故两个都测、翻转其一即证门在起作用。
    public ConfigCaptureEntry? GatePreScanStart { get; set; }

    public ConfigCaptureEntry? GatePreErrorCounts { get; set; }

    public ConfigCaptureEntry? GatePostScanStart { get; set; }

    public ConfigCaptureEntry? GatePostErrorCounts { get; set; }

    /// <summary>进模式（0x00 写 0x5678）的写回执。</summary>
    public ConfigCaptureEntry? EnterAck { get; set; }

    public bool EnterAccepted => EnterAck is { IsOk: true };

    /// <summary>模式门被证实起作用：0x10 或 0xAA 之一从"进模式前非 0x00"翻为"进模式后 0x00"。</summary>
    public bool GateProven =>
        (GatePreScanStart is { IsOk: false } && GatePostScanStart is { IsOk: true }) ||
        (GatePreErrorCounts is { IsOk: false } && GatePostErrorCounts is { IsOk: true });

    /// <summary>扫读正文：0x10~0xA2 + 0xAA 每地址一条。</summary>
    public List<ConfigCaptureEntry> ScanEntries { get; } = [];

    /// <summary>退模式（写 0x01）回执；null = 本次未写退出（走断连退法）。</summary>
    public ConfigCaptureEntry? ExitAck { get; set; }

    /// <summary>退出动作后对 0xAA 的探测：回 0x80 即证设备已退出配置模式。</summary>
    public ConfigCaptureEntry? PostExitProbe { get; set; }

    public int RespondedCount => ScanEntries.Count(e => e.IsOk);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# JBD 配置模式只读抓取档");
        sb.AppendLine();
        sb.AppendLine("> 原始抓取，不解释、不换算。刻度/物理量解释留给下一刀。");
        sb.AppendLine();
        sb.AppendLine($"- 抓取时间：{CapturedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 链路：{Link}");
        sb.AppendLine($"- 设备名(0x05)：{FormatDeviceName()}");
        sb.AppendLine($"- 进模式(0x00 写 0x5678)：{DescribeAck(EnterAck)}");
        sb.AppendLine($"- 模式门自检：{DescribeGate()}");
        sb.AppendLine($"- 退模式(写 0x01)：{(ExitAck is null ? "本次未写（断连退法）" : DescribeAck(ExitAck))}");
        sb.AppendLine($"- 退出后 0xAA 探测：{(PostExitProbe is null ? "未执行" : $"状态 {PostExitProbe.StatusText}（0x80=已退出配置模式）")}");
        sb.AppendLine($"- 退出策略结论：{ExitStrategyNote}");
        sb.AppendLine();
        sb.AppendLine($"## 扫读结果（0x{JbdConfigMode.ScanStart:X2}~0x{JbdConfigMode.ScanEnd:X2} + 0x{JbdConfigMode.RegErrorCounts:X2}）");
        sb.AppendLine();

        if (!EnterAccepted)
        {
            sb.AppendLine("（进模式未被受理，扫读未执行——见上方进模式回执。）");
            return sb.ToString();
        }

        sb.AppendLine("| 寄存器 | 状态 | 长度 | 原始数据 hex |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var entry in ScanEntries)
        {
            string length = entry.Outcome == CaptureOutcome.NoResponse ? "–" : entry.Data.Length.ToString();
            sb.AppendLine($"| 0x{entry.Register:X2} | {entry.StatusText} | {length} | {entry.DataHex} |");
        }

        sb.AppendLine();
        sb.AppendLine($"应答(状态 0x00)地址数：{RespondedCount} / {ScanEntries.Count}");
        return sb.ToString();
    }

    private string FormatDeviceName()
    {
        if (DeviceName is not { IsOk: true } entry)
        {
            return DeviceName is null ? "未读取" : DescribeAck(DeviceName);
        }

        // 设备名按惯例是 ASCII；不可打印时只给 hex，不猜编码
        string hex = Convert.ToHexString(entry.Data);
        return entry.Data.All(b => b is >= 0x20 and < 0x7F)
            ? $"`{Encoding.ASCII.GetString(entry.Data)}`（hex：{hex}）"
            : $"(非 ASCII) hex：{hex}";
    }

    private string DescribeGate()
    {
        if (GatePreScanStart is null || GatePostScanStart is null)
        {
            return "未执行";
        }

        string detail =
            $"0x10 前 {GatePreScanStart.StatusText} → 后 {GatePostScanStart.StatusText}；" +
            $"0xAA 前 {GatePreErrorCounts?.StatusText} → 后 {GatePostErrorCounts?.StatusText}";
        return GateProven ? $"通过（{detail}）" : $"⚠ 未证实（{detail}）——数据可疑，勿作真值使用";
    }

    private static string DescribeAck(ConfigCaptureEntry? ack) => ack switch
    {
        null => "未执行",
        { Outcome: CaptureOutcome.NoResponse } => "无应答",
        { IsOk: true } => "受理（状态 0x00）",
        _ => $"被拒（状态 {ack.StatusText}）",
    };
}

/// <summary>
/// 配置模式只读抓取会话：读 0x05 身份戳 → 模式门前置自检 → 进模式 → 后置自检 →
/// 扫读 0x10~0xA2 + 0xAA →（可选）写 0x01 退模式并复测 0xAA。
/// 传输由调用方注入：exchange(请求帧) 返回结构合法的响应帧字节，超时无应答返回 null——
/// 无应答只记录并继续，绝不中断扫描。本会话逐条同步执行（半双工一次一条在途），
/// 除进/退模式两帧外零写入。
/// </summary>
public static class ConfigModeCapture
{
    public static ConfigCaptureReport Run(
        Func<byte[], byte[]?> exchange,
        bool writeExit,
        Action<int, int>? progress = null)
    {
        var report = new ConfigCaptureReport
        {
            DeviceName = Probe(exchange, JbdConfigMode.RegDeviceName),
            GatePreScanStart = Probe(exchange, JbdConfigMode.ScanStart),
            GatePreErrorCounts = Probe(exchange, JbdConfigMode.RegErrorCounts),
        };

        report.EnterAck = Exchange(exchange, JbdConfigMode.BuildEnterFrame(), JbdConfigMode.RegEnterConfig);
        if (!report.EnterAccepted)
        {
            return report; // 进不了模式就不扫：裸态扫回来的是假数据
        }

        report.GatePostScanStart = Probe(exchange, JbdConfigMode.ScanStart);
        report.GatePostErrorCounts = Probe(exchange, JbdConfigMode.RegErrorCounts);

        var registers = JbdConfigMode.ScanRegisters;
        for (int i = 0; i < registers.Count; i++)
        {
            report.ScanEntries.Add(Probe(exchange, registers[i]));
            progress?.Invoke(i + 1, registers.Count);
        }

        if (writeExit)
        {
            report.ExitAck = Exchange(exchange, JbdConfigMode.BuildExitFrame(), JbdConfigMode.RegExitSave);
            report.PostExitProbe = Probe(exchange, JbdConfigMode.RegErrorCounts);
        }

        return report;
    }

    private static ConfigCaptureEntry Probe(Func<byte[], byte[]?> exchange, byte register)
        => Exchange(exchange, JbdFrame.BuildRead(register), register);

    /// <summary>发一帧、按回显寄存器解析一帧。结构坏帧按无应答记（组帧层已滤，属罕见路径）。</summary>
    private static ConfigCaptureEntry Exchange(Func<byte[], byte[]?> exchange, byte[] request, byte register)
    {
        byte[]? response = exchange(request);
        return response is not null &&
               JbdFrame.TryParseRawResponse(response, register, out byte status, out byte[] data)
            ? new ConfigCaptureEntry(register, CaptureOutcome.Ok, status, data)
            : new ConfigCaptureEntry(register, CaptureOutcome.NoResponse, 0, []);
    }
}
