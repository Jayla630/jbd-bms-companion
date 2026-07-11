using Jbd.Protocol;

namespace Jbd.UpperComputer.Services;

/// <summary>
/// 串口 BMS 客户端：连接后自动按周期轮询 0x03/0x04；写命令与轮询读同走一条
/// 串行化命令队列（半双工一次一条在途）。
/// 注意：所有事件都在后台线程（串口接收线程或定时器线程）上触发，
/// 订阅方更新 UI 绑定属性前必须自行 marshal 回 UI 线程（Dispatcher）。
/// </summary>
public interface ISerialBmsClient : IDisposable
{
    /// <summary>收到合法 0x03 基础信息响应（串口线程）。</summary>
    event Action<BasicInfo>? BasicInfoReceived;

    /// <summary>收到合法 0x04 单体电压响应（串口线程）。</summary>
    event Action<CellVoltages>? CellVoltagesReceived;

    /// <summary>写命令收到 ack：(寄存器, 是否被设备受理)。真正的状态确认以下一轮 0x03 回读为准。</summary>
    event Action<byte, bool>? WriteAcknowledged;

    /// <summary>某条在途命令在超时窗口内没等到回显响应（定时器线程）。</summary>
    event Action? ResponseTimedOut;

    /// <summary>解锁序列推进：第 step 步（1..3）的写 ack 已受理（串口线程）。</summary>
    event Action<int>? MosUnlockProgressed;

    /// <summary>
    /// 解锁序列结束：(是否成功, 结果说明)。三条 ack 收齐 ≠ 成功——成功一律以下一轮
    /// 0x03 回读 bit12=0 且两路 FET 均开为准；任一步被拒/超时立即中止并如实报失败（后台线程）。
    /// </summary>
    event Action<bool, string>? MosUnlockCompleted;

    bool IsOpen { get; }

    /// <summary>打开串口（8N1）并启动轮询。</summary>
    void Connect(string portName, int baudRate);

    void Disconnect();

    /// <summary>写 0xE1：按目标开关状态下发 MOS 控制（入队，命令泵空闲点发送）。</summary>
    void WriteMosControl(bool chargeOn, bool dischargeOn);

    /// <summary>写 0xE2：均衡开关（入队，命令泵空闲点发送）。</summary>
    void WriteBalance(bool on);

    /// <summary>
    /// 启动 bit12 引导式解锁：把 <see cref="Jbd.Protocol.JbdMosControl.BuildUnlockSequence"/>
    /// 的三条 0xE1 写按序入队，收 ack 逐步推进。幂等可重入（首步恒为两路全关），
    /// 序列进行中重复调用被忽略；未连接时调用无效果。
    /// </summary>
    void UnlockMos();
}
