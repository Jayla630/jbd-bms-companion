using System.IO;
using System.IO.Ports;
using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 基于 System.IO.Ports 的配置模式抓取传输层。9600 8N1，独占串口、同步逐条请求/应答
/// （抓取是一次性顺序动作，不复用轮询客户端的命令泵，也不触碰其已封板路径）。
/// 每条命令带 <see cref="CommandTimeoutMs"/> 超时：无应答记录后继续，整个扫描绝不卡死。
/// </summary>
public sealed class ConfigCaptureService : IConfigCaptureService
{
    /// <summary>单条命令超时。9600 波特下最长响应帧远小于此窗口。</summary>
    public const int CommandTimeoutMs = 500;

    /// <summary>断连退法探测前的静置时间。</summary>
    public const int DisconnectProbeDelayMs = 2000;

    public ConfigCaptureReport Capture(
        string portName, int baudRate, bool writeExit, Action<int, int>? progress)
    {
        ConfigCaptureReport report;
        var accumulator = new FrameAccumulator();
        using (var port = OpenPort(portName, baudRate))
        {
            report = ConfigModeCapture.Run(
                frame => ExchangeFrame(port, accumulator, frame), writeExit, progress);
        }

        report.Link = $"{portName} @ {baudRate} 8N1（RS485）";
        report.ExitStrategyNote = writeExit
            ? "写 0x01（载荷 0x0000，docs 未记载载荷、取公开通行值）退出，回执与 0xAA 复测见上。" +
              "0x01 的真机语义（是否固化、固化了什么）仍需按工单五在真板上核清。"
            : RunDisconnectProbe(portName, baudRate, accumulator, report);
        return report;
    }

    /// <summary>
    /// 断连退法探测（工单五首选路径的证据采集）：串口已断开，静置后重连读一次 0xAA。
    /// 回 0x80 = 设备已自行退出配置模式；回 0x00 = 仍在配置模式，退出路径需真机另核。
    /// </summary>
    private static string RunDisconnectProbe(
        string portName, int baudRate, FrameAccumulator accumulator, ConfigCaptureReport report)
    {
        if (!report.EnterAccepted)
        {
            return "未写退出；进模式未受理，无退出问题。";
        }

        Thread.Sleep(DisconnectProbeDelayMs);
        try
        {
            using var port = OpenPort(portName, baudRate);
            report.PostExitProbe = ConfigModeCapture.Probe(
                frame => ExchangeFrame(port, accumulator, frame), JbdConfigMode.RegErrorCounts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"未写退出；断连 {DisconnectProbeDelayMs} ms 后重连探测失败（{ex.Message}），" +
                   "设备是否已退出配置模式未验证，需真机人工核清。";
        }

        return report.PostExitProbe switch
        {
            { Outcome: CaptureOutcome.NoResponse } =>
                $"未写退出；断连 {DisconnectProbeDelayMs} ms 后重连读 0xAA 无应答，退出状态未验证。",
            { IsOk: true } =>
                $"⚠ 未写退出；断连 {DisconnectProbeDelayMs} ms 后重连读 0xAA 仍回 0x00——设备仍在配置模式，" +
                "断连退法在此静置时长内不成立，退出路径需真机另核（勿让设备停在配置态）。",
            _ =>
                $"未写退出；断连 {DisconnectProbeDelayMs} ms 后重连读 0xAA 回 0x80——设备已自行退出配置模式，断连退法成立。",
        };
    }

    private static SerialPort OpenPort(string portName, int baudRate)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 100,
            WriteTimeout = 500,
        };
        port.Open();
        return port;
    }

    /// <summary>发一帧、在超时窗口内组一帧。超时返回 null（上层记"无应答"继续扫）。</summary>
    private static byte[]? ExchangeFrame(SerialPort port, FrameAccumulator accumulator, byte[] request)
    {
        accumulator.Clear();     // 上一条的残余字节不许污染本条的组帧
        port.DiscardInBuffer();
        port.Write(request, 0, request.Length);

        var deadline = DateTime.UtcNow.AddMilliseconds(CommandTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int available = port.BytesToRead;
            if (available <= 0)
            {
                Thread.Sleep(10);
                continue;
            }

            byte[] chunk = new byte[available];
            _ = port.Read(chunk, 0, available);
            var frames = accumulator.Feed(chunk);
            if (frames.Count > 0)
            {
                return frames[0];
            }
        }

        return null;
    }
}
