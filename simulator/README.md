# 串口 BMS 模拟器

模拟 JBD SP04S010（4 串三元 / 30A / 带均衡）保护板的串口从站，供上位机开发在真板子到货前联调、演示、自动化测试。协议实现严格对齐 `../docs/阶段0_JBD-SP04S010_协议参考三表.md`。

## 环境准备

```bash
cd simulator
python -m venv .venv
# Windows: .venv\Scripts\activate
# Linux/Mac: source .venv/bin/activate
pip install -r requirements-dev.txt
```

## 跑测试

```bash
python -m pytest -v
```

黄金测试向量（协议三表 1.3 节）在 `tests/test_protocol.py` 里，是校验实现是否正确的基准，务必全绿。

## 建虚拟串口对

模拟器需要占住虚拟串口对的一端，上位机（或 JBDTOOLS / 任意串口调试工具）接另一端。

### Windows：com0com

1. 从 [com0com 官方仓库](https://sourceforge.net/projects/com0com/) 下载安装（选带签名的 driver 版本，Win10/11 需要开启测试签名或用社区签名版）。
2. 安装后打开 "Setup Command Prompt"，默认会建一对 `CNCA0 <-> CNCB0`；也可以用图形界面 `com0com setup` 重命名成好记的名字，比如：

   ```
   change CNCA0 PortName=COM10
   change CNCB0 PortName=COM11
   ```

3. 模拟器接 `COM10`，上位机/调试工具接 `COM11`（或反过来，两端对称）。

### Linux / Mac：socat

```bash
socat -d -d pty,raw,echo=0 pty,raw,echo=0
```

运行后会打印出两个 `/dev/pts/N` 路径，例如：

```
2026/07/10 10:00:00 socat[12345] N PTY is /dev/pts/3
2026/07/10 10:00:00 socat[12345] N PTY is /dev/pts/4
```

模拟器接 `/dev/pts/3`，上位机/调试工具接 `/dev/pts/4`。这个终端窗口要一直开着（socat 常驻进程维持这对虚拟串口）。

## 启动模拟器

```bash
python -m bms_sim.server --port COM10 --baudrate 9600
```

（Linux/Mac 换成 `--port /dev/pts/3`。）

## 用控制台驱动模拟器

`server.py`（真实串口）和 `cli.py`（控制台）目前各自持有独立的 `Device` 实例，`cli.py` 默认是一个不接串口、方便本地快速试验/写自动化场景的"空跑"模式。真正联调上位机时，控制台命令要驱动的是挂在真实串口上的那个模拟器——最简单的方式是把 `cli.repl()`/`run_scenario()` 需要执行的命令直接在 `server.py` 侧也接一份（后续可以扩展 `server.py` 支持从标准输入接控制台命令，与串口收发跑在同一个 `Device` 上；当前版本先满足"协议 + 模型"核心链路，这条留在下一阶段接线）。

单独跑控制台（脱离真实串口，用于本地快速试验命令和场景文件是否符合预期）：

```bash
python -m bms_sim.cli
```

进入交互模式后：

```
> current 2000        # 设置 2000mA 充电电流
> soc 80               # 把所有电芯 SOC 跳到 80%
> fault inject cell_overvoltage   # 注入单体过压故障
> mos 0x03             # 写 MOS 控制寄存器：全关充放（0xE1 语义，bit0=关放电/bit1=关充电）
> fault clear cell_overvoltage
> status
> quit
```

## 一键回放演示场景

```bash
python -m bms_sim.cli --scenario bms_sim/scenarios/demo.yaml
```

会按时间线依次执行：充电升压 → 注入单体过压故障（保护位置位、充电 MOS 被关闭）→ 清故障（充电 MOS 需要另外用 `mos` 命令重新打开，清故障本身不会自动开 MOS，贴近真板行为）→ 切换到放电 → 打印状态快照。场景文件格式见 `bms_sim/scenarios/demo.yaml`，可以照着写新场景（`wait_seconds` + `action`，`action` 就是一条控制台命令）。

## MOS 软件锁定（bit12）已知坑

真板子偶尔会出现"保护状态 bit12=1，充放开关怎么切都切不动"的情况，不是坏了，是软件锁定，需要按固定顺序解锁：

1. 先写 `0xE1 = 0x0003`（同时关闭充电和放电）；
2. 再写只开充电（bit1 清零、bit0 保持 1）；
3. 最后写开放电（bit0 也清零）。

顺序错了（比如先开放电、或者跳过第一步直接开）不会生效，模拟器复现了这个行为（见 `bms_sim/faults.py` 里的 `MosController`），方便上位机提前把这套解锁引导做进 UI。

## 目录说明

| 文件 | 内容 |
|---|---|
| `bms_sim/protocol.py` | 帧结构、校验算法、编解码、流式解码器 |
| `bms_sim/battery.py` | OCV-SOC 曲线、单体/整包模型、库仑计数、温度模型 |
| `bms_sim/faults.py` | 保护状态位、阈值评估、故障注入、MOS 控制与软件锁定状态机 |
| `bms_sim/device.py` | 命令分发，把协议命令映射到电池/故障模型的读写 |
| `bms_sim/server.py` | 真实串口收发主循环 |
| `bms_sim/cli.py` | 交互式控制台 + 场景 YAML 回放 |
| `bms_sim/scenarios/` | 示例场景 YAML |

## 真机校准清单

真机到手后，按 `../docs/阶段0_JBD-SP04S010_协议参考三表.md` 第四节的步骤核对，重点改这几处集中存放的常量：

- `bms_sim/battery.py` 顶部：额定容量、内阻、OCV 曲线锚点、热模型参数。
- `bms_sim/faults.py` 里的 `PROTECTION_THRESHOLDS`：各类保护的触发/恢复阈值。
- `bms_sim/faults.py` 里的 `FaultManager.error_count_bytes()`：0xAA 寄存器的数据格式目前是模拟器自定义的占位格式，真机抓包后按实际格式改。
- 电流符号方向（协议约定正=充电，真机验证后如果相反，只需要翻转 `bms_sim/device.py` 里电流钳零逻辑和 CLI 里的符号说明）。
