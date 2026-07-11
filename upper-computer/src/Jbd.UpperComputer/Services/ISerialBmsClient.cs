using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 串口 BMS 客户端：连接后自动按周期轮询 0x03/0x04。
/// 注意：所有事件都在后台线程（串口接收线程或定时器线程）上触发，
/// 订阅方更新 UI 绑定属性前必须自行 marshal 回 UI 线程（Dispatcher）。
/// </summary>
public interface ISerialBmsClient : IDisposable
{
    /// <summary>收到合法 0x03 基础信息响应（串口线程）。</summary>
    event Action<BasicInfo>? BasicInfoReceived;

    /// <summary>收到合法 0x04 单体电压响应（串口线程）。</summary>
    event Action<CellVoltages>? CellVoltagesReceived;

    /// <summary>上一个轮询周期没等到 0x03 响应（定时器线程）。</summary>
    event Action? ResponseTimedOut;

    bool IsOpen { get; }

    /// <summary>打开串口（8N1）并启动轮询。</summary>
    void Connect(string portName, int baudRate);

    void Disconnect();
}
