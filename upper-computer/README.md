# PC 上位机（阶段二 · 切片1）

JBD SP04S010 保护板的 Windows 上位机：选串口 → 连接 → 周期轮询 0x03/0x04 → 实时显示总电压、电流、SOC 与 4 路单体电压。协议编解码与 [`../simulator/`](../simulator/README.md)（Python）互为独立实现，交叉验证 [`../docs/`](../docs/阶段0_JBD-SP04S010_协议参考三表.md) 三表的正确性。

## 工程结构

```
upper-computer/
├── UpperComputer.sln
├── src/
│   ├── Jbd.Protocol/            # 纯协议库（net8.0，零 WPF 依赖）：帧构建/解析/校验/帧累积器
│   └── Jbd.UpperComputer/       # WPF + Prism 9（DryIoc），net8.0-windows：串口 I/O、轮询、界面
└── tests/
    └── Jbd.Protocol.Tests/      # xUnit，黄金向量与 Python 侧 pytest 互为第二意见
```

分层纪律：**所有字节级解析都在 `Jbd.Protocol`**，WPF 工程只做串口收发、线程调度和绑定。协议库可脱离界面跨平台测试。

## 构建 / 测试 / 运行

```bash
cd upper-computer
dotnet build
dotnet test          # 黄金向量：0x03 请求逐字节、36 字节 0x03 响应、0x04 电压、各类坏帧拒收
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
4. 界面上总电压/电流/SOC/4 路单体电压每秒刷新，底部显示最后刷新时间戳；把模拟器数据改一改（见 simulator README 的控制台命令），界面数值应跟着变。

## 线程模型（为什么不会跨线程崩溃）

串口数据到达走 `SerialPort.DataReceived`，它在**串口线程**上回调：

- 串口线程上只做两件事：喂帧累积器（处理分包/粘包/噪声重对齐）、调 `Jbd.Protocol` 解析出强类型模型；
- ViewModel 订阅解析结果事件后，**先通过 `Dispatcher.BeginInvoke` marshal 回 UI 线程，再更新绑定属性**——绝不在串口线程上直接写绑定属性，否则会抛跨线程访问异常或造成难查的界面问题；
- 轮询由后台 `System.Timers.Timer`（1 s）驱动：每周期发 0x03，收到 0x03 回包后才链式发 0x04（半双工"发一等一"）；响应按帧内回显的寄存器字节路由到解析器，不靠发送顺序猜；整周期无回包则界面状态置"超时"。

## 范围（本切片）

只读不写：不含控制命令（MOS 开关、参数下发）、保护参数读写、故障/告警展示、蓝牙。协议细节一律以 [`../docs/`](../docs/阶段0_JBD-SP04S010_协议参考三表.md) 为准。回到 [根 README](../README.md)。
