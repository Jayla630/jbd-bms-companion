using System.IO;
using System.IO.Ports;
using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 基于 System.IO.Ports 的 BMS 串口客户端。9600 8N1，半双工请求/应答。
/// 接收走 DataReceived 事件：串口线程上喂帧累积器、调 Jbd.Protocol 解析，
/// 按响应帧回显的寄存器字节（帧第 2 字节）路由，不猜发送顺序。
///
/// 命令泵：轮询读（0x03→0x04）与用户写（0xE1/0xE2）都进同一条串行化队列，
/// 一次只在途一条，收到其回显寄存器的响应（或超时）才发下一条——
/// 每条在途命令记住期望回显寄存器按回显配对，取代切片1 的单布尔 _awaitingBasicInfo。
/// </summary>
public sealed class SerialBmsClient : ISerialBmsClient
{
    public const double PollIntervalMs = 1000;

    /// <summary>在途命令超时：下一个定时器周期检查，超过即放弃并发下一条。</summary>
    public const double CommandTimeoutMs = 800;

    private readonly object _sync = new();
    private readonly FrameAccumulator _accumulator = new();
    private readonly System.Timers.Timer _pollTimer = new(PollIntervalMs) { AutoReset = true };
    private readonly Queue<byte[]> _queue = new();
    private SerialPort? _port;
    private byte[]? _inFlightFrame;
    private DateTime _inFlightSentUtc;

    // --- bit12 引导式解锁状态（都在 _sync 内读写）。三条写帧按引用识别，
    // --- 与用户手拨开关产生的普通 0xE1 写区分开，互不干扰步数推进。
    private byte[][]? _unlockFrames;
    private int _unlockAckedSteps;
    private bool _unlockAwaitingReadback;

    public SerialBmsClient()
    {
        _pollTimer.Elapsed += (_, _) => OnPollTick();
    }

    public event Action<BasicInfo>? BasicInfoReceived;

    public event Action<CellVoltages>? CellVoltagesReceived;

    /// <summary>写命令收到 ack（寄存器, 是否被设备受理）。后台线程触发。</summary>
    public event Action<byte, bool>? WriteAcknowledged;

    public event Action? ResponseTimedOut;

    /// <summary>解锁序列推进：第 step 步（1..3）的写 ack 已受理。串口线程触发。</summary>
    public event Action<int>? MosUnlockProgressed;

    /// <summary>解锁序列结束：(成功与否, 说明)。成败以 0x03 回读为准，不以 ack 收齐为准。后台线程触发。</summary>
    public event Action<bool, string>? MosUnlockCompleted;

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
            _queue.Clear();
            _inFlightFrame = null;
            ResetUnlockLocked();
            _port = port;

            EnqueueReadsLocked(); // 不等第一个定时周期，立刻发起首轮读
            PumpLocked();
        }

        _pollTimer.Start();
    }

    public void Disconnect()
    {
        _pollTimer.Stop();

        SerialPort? port;
        lock (_sync)
        {
            _queue.Clear();
            _inFlightFrame = null;
            ResetUnlockLocked(); // 断开即丢弃进行中的解锁，重连后可从第一步重跑（幂等）
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

    /// <summary>按目标状态写 0xE1（关闭语义换算交给协议层）。只入队，命令泵在空闲点发送。</summary>
    public void WriteMosControl(bool chargeOn, bool dischargeOn)
        => EnqueueAndPump(JbdFrame.BuildWrite(
            JbdFrame.RegMosControl, JbdMosControl.BuildControlData(chargeOn, dischargeOn)));

    /// <summary>写 0xE2 均衡开关。只入队，命令泵在空闲点发送。</summary>
    public void WriteBalance(bool on)
        => EnqueueAndPump(JbdFrame.BuildWrite(
            JbdFrame.RegBalanceControl, JbdMosControl.BuildBalanceData(on)));

    /// <summary>
    /// 启动 bit12 引导式解锁：三条 0xE1 写按序入队走命令泵，一次一条、收 ack 再发下一条。
    /// 常规轮询读穿插其间不影响——设备端解锁状态机只由 0xE1 写值推进（simulator device.py：
    /// 读寄存器不触碰 MosController），无需暂停轮询。序列进行中重复调用被忽略。
    /// </summary>
    public void UnlockMos()
    {
        lock (_sync)
        {
            if (_port is not { IsOpen: true } || _unlockFrames is not null || _unlockAwaitingReadback)
            {
                return;
            }

            _unlockAckedSteps = 0;
            _unlockFrames = [.. JbdMosControl.BuildUnlockSequence()
                .Select(data => JbdFrame.BuildWrite(JbdFrame.RegMosControl, data))];
            foreach (byte[] frame in _unlockFrames)
            {
                _queue.Enqueue(frame);
            }

            PumpLocked();
        }
    }

    public void Dispose()
    {
        Disconnect();
        _pollTimer.Dispose();
    }

    private void EnqueueAndPump(byte[] frame)
    {
        lock (_sync)
        {
            if (_port is not { IsOpen: true })
            {
                return;
            }

            _queue.Enqueue(frame);
            PumpLocked();
        }
    }

    /// <summary>轮询周期：先判在途命令超时，再补一轮读命令（队列空时才补，防止设备失联时堆积）。</summary>
    private void OnPollTick()
    {
        bool timedOut = false;
        int unlockTimedOutStep = 0;
        lock (_sync)
        {
            if (_port is not { IsOpen: true })
            {
                return;
            }

            if (_inFlightFrame is not null &&
                (DateTime.UtcNow - _inFlightSentUtc).TotalMilliseconds > CommandTimeoutMs)
            {
                if (IsUnlockFrameLocked(_inFlightFrame))
                {
                    unlockTimedOutStep = _unlockAckedSteps + 1;
                    AbortUnlockLocked();
                }

                _inFlightFrame = null;
                timedOut = true;
            }

            if (_queue.Count == 0)
            {
                EnqueueReadsLocked();
            }

            PumpLocked();
        }

        if (timedOut)
        {
            ResponseTimedOut?.Invoke();
        }

        if (unlockTimedOutStep > 0)
        {
            MosUnlockCompleted?.Invoke(false, $"第 {unlockTimedOutStep}/3 步超时未应答，解锁中止");
        }
    }

    private void EnqueueReadsLocked()
    {
        _queue.Enqueue(JbdFrame.BuildRead(JbdFrame.RegBasicInfo));
        _queue.Enqueue(JbdFrame.BuildRead(JbdFrame.RegCellVoltages));
    }

    /// <summary>空闲（无在途命令）且队列非空时发出下一条。请求帧的寄存器都在偏移 2。</summary>
    private void PumpLocked()
    {
        if (_inFlightFrame is not null || _queue.Count == 0 || _port is not { IsOpen: true })
        {
            return;
        }

        byte[] frame = _queue.Dequeue();
        try
        {
            _port.Write(frame, 0, frame.Length);
            _inFlightFrame = frame;
            _inFlightSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            // 写失败：不置在途，留给下个周期重试/超时路径
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

    /// <summary>
    /// 按回显寄存器路由到对应解析器并推进命令泵；解析失败的帧静默丢弃（坏帧由拒收测试兜底）。
    /// </summary>
    private void RouteFrame(byte[] frame)
    {
        byte echoRegister = frame[1];
        bool ackIsUnlockStep = false;
        lock (_sync)
        {
            // 请求帧寄存器在偏移 2；回显匹配在途命令即视为其应答，泵推进下一条
            if (_inFlightFrame is not null && _inFlightFrame[2] == echoRegister)
            {
                ackIsUnlockStep = IsUnlockFrameLocked(_inFlightFrame);
                _inFlightFrame = null;
            }
        }

        if (ackIsUnlockStep)
        {
            // 解锁步骤的 ack 只推进解锁状态机，不进 WriteAcknowledged（避免和普通写提示混流）。
            // 帧结构坏/被拒都算该步失败——绝不带伤继续下一步。
            bool stepOk = JbdFrame.TryParseWriteAck(frame, JbdFrame.RegMosControl, out bool accepted)
                && accepted;
            HandleUnlockStepAck(stepOk);
        }
        else
        {
            switch (echoRegister)
            {
                case JbdFrame.RegBasicInfo when JbdFrame.TryParseBasicInfo(frame, out var info):
                    HandleUnlockReadback(info!);
                    BasicInfoReceived?.Invoke(info!);
                    break;
                case JbdFrame.RegCellVoltages when JbdFrame.TryParseCellVoltages(frame, out var cells):
                    CellVoltagesReceived?.Invoke(cells!);
                    break;
                case JbdFrame.RegMosControl or JbdFrame.RegBalanceControl
                    when JbdFrame.TryParseWriteAck(frame, echoRegister, out bool accepted):
                    WriteAcknowledged?.Invoke(echoRegister, accepted);
                    break;
            }
        }

        lock (_sync)
        {
            PumpLocked();
        }
    }

    /// <summary>解锁帧按引用识别（三条帧在 UnlockMos 里一次性构建）。_sync 内调用。</summary>
    private bool IsUnlockFrameLocked(byte[] frame)
        => _unlockFrames is not null && Array.IndexOf(_unlockFrames, frame) >= 0;

    /// <summary>中止解锁：丢弃队列里还没发出的解锁帧，清空序列状态。_sync 内调用。</summary>
    private void AbortUnlockLocked()
    {
        if (_unlockFrames is not null)
        {
            byte[][] rest = [.. _queue.Where(f => Array.IndexOf(_unlockFrames, f) < 0)];
            _queue.Clear();
            foreach (byte[] frame in rest)
            {
                _queue.Enqueue(frame);
            }
        }

        ResetUnlockLocked();
    }

    private void ResetUnlockLocked()
    {
        _unlockFrames = null;
        _unlockAckedSteps = 0;
        _unlockAwaitingReadback = false;
    }

    private void HandleUnlockStepAck(bool accepted)
    {
        int step;
        lock (_sync)
        {
            if (_unlockFrames is null)
            {
                return; // 断开/中止竞态：序列已被清理
            }

            if (!accepted)
            {
                step = _unlockAckedSteps + 1;
                AbortUnlockLocked();
            }
            else
            {
                step = ++_unlockAckedSteps;
                if (step == JbdMosControl.UnlockStepCount)
                {
                    // 三条 ack 收齐 ≠ 成功：转入等待下一帧 0x03 回读判成败
                    _unlockFrames = null;
                    _unlockAwaitingReadback = true;
                }
            }
        }

        if (!accepted)
        {
            MosUnlockCompleted?.Invoke(false, $"第 {step}/3 步被设备拒绝，解锁中止");
            return;
        }

        MosUnlockProgressed?.Invoke(step);
    }

    /// <summary>三条 ack 收齐后的第一帧 0x03 回读给出最终裁决：bit12=0 且两路 FET 均开才算成功。</summary>
    private void HandleUnlockReadback(BasicInfo info)
    {
        lock (_sync)
        {
            if (!_unlockAwaitingReadback)
            {
                return;
            }

            _unlockAwaitingReadback = false;
        }

        bool unlocked = !info.MosSoftwareLocked && info.ChargeMosOn && info.DischargeMosOn;
        MosUnlockCompleted?.Invoke(
            unlocked,
            unlocked
                ? "解锁成功：回读确认 bit12 已清零，两路 MOS 均开"
                : "回读未确认解锁（bit12 仍置位或 FET 未全开），请重试");
    }
}
