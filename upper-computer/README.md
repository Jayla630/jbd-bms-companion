# PC 上位机（阶段二 · 切片1+2）

JBD SP04S010 保护板的 Windows 上位机：选串口 → 连接 → 周期轮询 0x03/0x04 实时显示总电压、电流、SOC、4 路单体电压，并支持充/放电 MOS 与均衡的写控制（0xE1/0xE2）、保护状态位展示与 MOS 软件锁定提示。协议编解码与 [`../simulator/`](../simulator/README.md)（Python）互为独立实现，交叉验证 [`../docs/`](../docs/阶段0_JBD-SP04S010_协议参考三表.md) 三表的正确性。

## 工程结构

```
upper-computer/
├── UpperComputer.sln
├── src/
│   ├── Jbd.Protocol/            # 纯协议库（net8.0，零 WPF 依赖）：帧构建/解析/校验/帧累积器/保护位映射
│   └── Jbd.UpperComputer/       # WPF + Prism 9（DryIoc），net8.0-windows：串口 I/O、命令泵、界面
└── tests/
    └── Jbd.Protocol.Tests/      # xUnit，黄金向量与 Python 侧 pytest 互为第二意见
```

分层纪律：**所有字节级解析都在 `Jbd.Protocol`**（含保护位→可读标签的映射），WPF 工程只做串口收发、线程调度和绑定。协议库可脱离界面跨平台测试。

## 构建 / 测试 / 运行

```bash
cd upper-computer
dotnet build
dotnet test          # 黄金向量：0x03 请求/响应、0x04、0xE1 写帧、写 ack、保护位映射、语义相反专项
dotnet run --project src/Jbd.UpperComputer
```

## 与模拟器联调（虚拟串口对）

1. 建一对虚拟串口（两端互通）：
   - Windows：[com0com](https://sourceforge.net/projects/com0com/)（或 ELTIMA VSPD 等同类工具），建出如 `COM1 <-> COM2` 一对；
   - Linux/Mac 参考 `simulator/README.md` 的 socat 用法。
2. 模拟器占一端：

   ```bash
   cd simulator
   python -m bms_sim.server --port COM1 --baudrate 9600
   ```

3. 启动上位机，串口下拉选**另一端**（如 `COM2`），波特率 9600，点"连接"。
4. 界面上总电压/电流/SOC/4 路单体电压每秒刷新，底部显示最后刷新时间戳。在 **server 自己的终端里**直接敲控制台命令（如 `soc 80`、`current -1500`，输入 `help` 看全集），命令作用在串口收发共享的同一个 Device 上，界面数值当场跟着变——详见 simulator README"启动模拟器"一节。
5. 写控制与保护展示的演示：
   - 界面拨"放电 MOS"关 → 写 0xE1 → 下一轮 0x03 回读，开关显示确认为关；拨回开同理；
   - server 控制台 `fault inject discharge_overcurrent` → 保护面板"放电过流"点亮（BMS 同时自动关放电 MOS，界面如实显示）；`fault clear ...` 后熄灭；
   - server 控制台 `fault inject mos_locked` → 顶部打出红色"MOS 软件锁定"横幅；此时再拨 MOS 开关，写入被设备静默拒绝，开关自动弹回真实态——证明界面是回读驱动、非乐观更新。

## 写命令链路（回读为准，非乐观更新）

- **命令泵**：轮询读（0x03→0x04）与用户写（0xE1/0xE2）进同一条串行化队列，半双工一次只在途一条，按在途命令期望的回显寄存器与响应配对推进；超时（800 ms）放弃并发下一条。
- **回读为准**：MOS 开关的显示值只来自 0x03 回读的 FET 状态字节。开关 setter 不改本地值，只入队写命令并立即"弹回"当前回读值；写被受理后，真正的状态变化等下一轮 0x03 带回。写被静默拒绝（典型：软件锁定）时开关自然回到真实态。
- **语义陷阱**：0xE1 写控制字是"关闭语义"（bit0=1 关放电、bit1=1 关充电），0x03 的 FET 状态字节是"开启语义"（bit0=充电开、bit1=放电开）——相反且位序不同，换算收口在 `Jbd.Protocol.JbdMosControl`，有专项测试钉死。
- **均衡开关的例外**：协议的 0x03 响应里没有"均衡使能"回读字段（docs/ 寄存器表偏移 12–15 是逐串均衡动作位图，不是使能开关），所以均衡开关以写 ack 受理为准更新显示，仍非乐观（ack 被拒则弹回）。
- **软件锁定（bit12）**：本切片只识别 + 醒目提示（红色横幅 + 控制组加注），开关保留可拨以演示回读弹回；引导式三步解锁（先关充放→开充电→开放电）留作后续独立切片。

## 线程模型（为什么不会跨线程崩溃）

串口数据到达走 `SerialPort.DataReceived`，它在**串口线程**上回调：

- 串口线程上只做两件事：喂帧累积器（处理分包/粘包/噪声重对齐）、调 `Jbd.Protocol` 解析出强类型模型；
- ViewModel 订阅解析结果事件后，**先通过 `Dispatcher.BeginInvoke` marshal 回 UI 线程，再更新绑定属性**——绝不在串口线程上直接写绑定属性，否则会抛跨线程访问异常或造成难查的界面问题；
- 轮询由后台 `System.Timers.Timer`（1 s）驱动，命令泵负责发送节奏；响应按帧内回显的寄存器字节路由到解析器，不靠发送顺序猜；在途命令超时则界面状态置"超时"。

> 切片1 遗留的"单布尔只盯 0x03"简化已在切片2 由命令泵收掉：每条在途命令（读或写）都按自身回显寄存器配对与超时。

## 范围（本切片之外）

不含：MOS 软件锁定的引导式解锁序列、保护参数（阈值）读写、配置模式（0x00/0x01）、0xAA 错误计数、蓝牙。协议细节一律以 [`../docs/`](../docs/阶段0_JBD-SP04S010_协议参考三表.md) 为准。回到 [根 README](../README.md)。
