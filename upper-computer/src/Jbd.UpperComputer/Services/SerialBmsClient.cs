using System.IO;
using System.IO.Ports;
using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 基于 System.IO.Ports 的 BMS 串口客户端。9600 8N1，半双工请求/应答。
/// 接收走 DataReceived 事件：串口线程上喂帧累积器、调 Jbd.Protocol 解析，
/// 按响应帧回显的寄存器字节（帧第 2 字节）路由，不猜发送顺序。
/// 轮询：定时器每周期发 0x03；收到 0x03 响应后在同周期链式发 0x04，
/// 保持"发一条、等回包、再发下一条"的半双工节奏。
/// </summary>
public sealed class SerialBmsClient : ISerialBmsClient
{
    public const double PollIntervalMs = 1000;

    private readonly object _sync = new();
    private readonly FrameAccumulator _accumulator = new();
    private readonly System.Timers.Timer _pollTimer = new(PollIntervalMs) { AutoReset = true };
    private SerialPort? _port;

    /// <summary>1 = 已发 0x03 还没等到回包。用 Interlocked 读写：定时器线程置位，串口线程清零。</summary>
    private int _awaitingBasicInfo;

    public SerialBmsClient()
    {
        _pollTimer.Elapsed += (_, _) => PollOnce();
    }

    public event Action<BasicInfo>? BasicInfoReceived;

    public event Action<CellVoltages>? CellVoltagesReceived;

    public event Action? ResponseTimedOut;

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

        PollOnce(); // 不等第一个定时周期，立刻发起首轮读
        _pollTimer.Start();
    }

    public void Disconnect()
    {
        _pollTimer.Stop();
        Interlocked.Exchange(ref _awaitingBasicInfo, 0);

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

    public void Dispose()
    {
        Disconnect();
        _pollTimer.Dispose();
    }

    /// <summary>一个轮询周期的起点：检查上周期是否超时，然后发 0x03。</summary>
    private void PollOnce()
    {
        if (!IsOpen)
        {
            return;
        }

        bool previousCycleUnanswered = Interlocked.Exchange(ref _awaitingBasicInfo, 1) == 1;
        if (previousCycleUnanswered)
        {
            ResponseTimedOut?.Invoke();
        }

        SendRead(JbdFrame.RegBasicInfo);
    }

    private void SendRead(byte register)
    {
        byte[] frame = JbdFrame.BuildRead(register);
        lock (_sync)
        {
            if (_port is not { IsOpen: true })
            {
                return;
            }

            try
            {
                _port.Write(frame, 0, frame.Length);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                // 写失败按超时处理：下个周期检测不到回包会抛 ResponseTimedOut
            }
        }
    }

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
                Interlocked.Exchange(ref _awaitingBasicInfo, 0);
                BasicInfoReceived?.Invoke(info!);
                SendRead(JbdFrame.RegCellVoltages); // 半双工：等到 0x03 回包才发 0x04
                break;
            case JbdFrame.RegCellVoltages when JbdFrame.TryParseCellVoltages(frame, out var cells):
                CellVoltagesReceived?.Invoke(cells!);
                break;
        }
    }
}
