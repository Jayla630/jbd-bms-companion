using System.IO;
using System.IO.Ports;
using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 基于 System.IO.Ports 的 BMS 串口客户端。9600 8N1，半双工请求/应答。
/// 接收走 DataReceived 事件：串口线程上喂帧累积器、调 Jbd.Protocol 解析，
/// 按响应帧回显的寄存器字节（帧第 2 字节）路由，不猜发送顺序。
/// </summary>
public sealed class SerialBmsClient : ISerialBmsClient
{
    private readonly object _sync = new();
    private readonly FrameAccumulator _accumulator = new();
    private SerialPort? _port;

    public event Action<BasicInfo>? BasicInfoReceived;

    public event Action<CellVoltages>? CellVoltagesReceived;

    public bool IsOpen
    {
        get
        {
            lock (_sync)
            {
                return _port is { IsOpen: true };
            }
        }
    }

    public void Connect(string portName, int baudRate)
    {
        lock (_sync)
        {
            if (_port is not null)
            {
                throw new InvalidOperationException("串口已连接，先断开再连。");
            }

            var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
            };
            port.DataReceived += OnDataReceived;
            port.Open();
            _accumulator.Clear();
            _port = port;
        }
    }

    public void Disconnect()
    {
        SerialPort? port;
        lock (_sync)
        {
            port = _port;
            _port = null;
        }

        if (port is not null)
        {
            port.DataReceived -= OnDataReceived;
            try
            {
                port.Close();
            }
            catch (IOException)
            {
                // 对端拔线/虚拟串口消失时 Close 可能抛 IO 异常，断开语义已达成
            }

            port.Dispose();
        }
    }

    public void SendRead(byte register)
    {
        byte[] frame = JbdFrame.BuildRead(register);
        lock (_sync)
        {
            if (_port is not { IsOpen: true })
            {
                return;
            }

            _port.Write(frame, 0, frame.Length);
        }
    }

    public void Dispose() => Disconnect();

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        byte[] chunk;
        lock (_sync)
        {
            if (_port is not { IsOpen: true } port)
            {
                return;
            }

            try
            {
                int available = port.BytesToRead;
                if (available <= 0)
                {
                    return;
                }

                chunk = new byte[available];
                _ = port.Read(chunk, 0, available);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return; // 断开竞态：端口刚被关掉
            }
        }

        foreach (byte[] frame in _accumulator.Feed(chunk))
        {
            RouteFrame(frame);
        }
    }

    /// <summary>按回显寄存器路由到对应解析器；解析失败的帧静默丢弃（坏帧由拒收测试兜底）。</summary>
    private void RouteFrame(byte[] frame)
    {
        switch (frame[1])
        {
            case JbdFrame.RegBasicInfo when JbdFrame.TryParseBasicInfo(frame, out var info):
                BasicInfoReceived?.Invoke(info!);
                break;
            case JbdFrame.RegCellVoltages when JbdFrame.TryParseCellVoltages(frame, out var cells):
                CellVoltagesReceived?.Invoke(cells!);
                break;
        }
    }
}
