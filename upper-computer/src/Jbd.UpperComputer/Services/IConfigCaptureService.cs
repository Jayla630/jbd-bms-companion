using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 配置模式只读抓取服务：独占打开串口跑一次
/// <see cref="Jbd.Protocol.ConfigModeCapture"/> 会话。
/// 与轮询客户端 <see cref="ISerialBmsClient"/> 互斥使用（同一串口不能两开），
/// UI 只在未连接轮询时才允许触发抓取。
/// </summary>
public interface IConfigCaptureService
{
    /// <summary>
    /// 阻塞执行一次抓取（调用方放后台线程）。
    /// writeExit=false 时走"断连退法"：会话不写 0x01，收尾断开串口、静置后重连读一次
    /// 0xAA 探测设备是否已自行退出配置模式，结论记入抓取档（工单五首选路径的证据）。
    /// progress 在调用线程上回调 (已扫地址数, 总数)。
    /// </summary>
    ConfigCaptureReport Capture(string portName, int baudRate, bool writeExit, Action<int, int>? progress);
}
