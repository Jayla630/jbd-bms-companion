using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 串口 BMS 客户端。注意：两个数据事件都在串口接收线程上触发，
/// 订阅方更新 UI 绑定属性前必须自行 marshal 回 UI 线程（Dispatcher）。
/// </summary>
public interface ISerialBmsClient : IDisposable
{
    /// <summary>收到合法 0x03 基础信息响应（串口线程）。</summary>
    event Action<BasicInfo>? BasicInfoReceived;

    /// <summary>收到合法 0x04 单体电压响应（串口线程）。</summary>
    event Action<CellVoltages>? CellVoltagesReceived;

    bool IsOpen { get; }

    void Connect(string portName, int baudRate);

    void Disconnect();

    /// <summary>发送一条读命令帧。</summary>
    void SendRead(byte register);
}
