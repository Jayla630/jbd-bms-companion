# 串口 BMS 模拟器设计文档

日期：2026-07-10
范围：`simulator/` 子项目（阶段一）

## 背景与目标

真实 JBD SP04S010（4 串三元、30A、带均衡）保护板还未到货。为了让上位机（`upper-computer/`，规划中）开发不被硬件阻塞，需要一个"假从站"：通过虚拟串口对，按 JBD 协议（详见 `docs/阶段0_JBD-SP04S010_协议参考三表.md`）响应读写请求，背后跑一个有物理意义的电池仿真模型（会随时间/电流变化，能演示故障保护），供上位机联调、演示、自动化测试使用。

协议三表是唯一事实来源，尤其是"校验算法"（请求帧覆盖寄存器字节、响应帧覆盖状态字节，两者都不含 `0xDD`/`0x77`）和"黄金测试向量"（第 61~94 行）必须逐字节对上。

## 架构

```
bms_sim/
  protocol.py     # 帧编解码、校验、寄存器/命令常量
  battery.py      # 电池模型：OCV-SOC 曲线、库仑计数、内阻压降、温度模型
  faults.py       # 保护状态位、故障注入、MOS 软锁定状态机
  device.py       # 把 protocol/battery/faults 粘起来：收到命令 -> 查状态/改状态 -> 生成响应
  server.py       # pyserial 收发主循环，喂字节给 FrameDecoder，转发给 device
  cli.py          # 交互式 REPL + 场景 YAML 回放
  scenarios/       # 示例场景 YAML
tests/
  test_protocol.py
  test_battery.py
  test_faults.py
README.md
```

各模块只通过明确的函数/类接口交互，`device.py` 是唯一知道"协议命令 <-> 电池/故障状态"映射关系的地方，`server.py` 完全不关心业务语义，只做字节搬运。这样 `battery.py`/`faults.py` 可以脱离串口单独用 pytest 测试。

## 关键设计决策

1. **状态推进方式：被动积分。** 电池模型不会自己按内置场景自动跑；充放电电流由 CLI/场景脚本显式设置（`set_current(mA)`），`BatteryPack` 只在被 `device.py` 查询时（收到 0x03 请求，或 CLI 定时 tick）按距上次更新的时间差做一次库仑计数 + 温度积分，然后再读出当前快照。好处：确定性强、单元测试容易写恒流充放电断言；代价：不设置电流就没有变化，这个由 CLI/场景脚本负责驱动。

2. **MOS 控制语义两套独立编码，`device.py` 做映射。**
   - 控制寄存器 `0xE1`（写）：bit0=1 关放电、bit1=1 关放电…（按文档：bit0=1 关放电，bit1=1 关充电）。
   - 状态字节（0x03 偏移 20）：bit0=充电开、bit1=放电开。
   两者语义相反，`faults.py` 内部只维护 `charge_enabled: bool` / `discharge_enabled: bool` 两个真值，写 0xE1 时按"关闭语义"翻译成这两个布尔量，读 0x03 时再按"开启语义"编码回状态字节。避免用一份位图硬套两种含义导致换算出错。

3. **MOS 软件锁定（bit12）解锁状态机。** protection 状态 bit12=1 时进入锁定：
   - 锁定期间收到任意 `0xE1` 写入，先要求写 `0x0003`（先关充放）——记为 `unlock_step = CLOSED_BOTH`；
   - 之后写入清 bit1（开充电，放电仍关）——`unlock_step = CHARGE_OPENED`；
   - 之后写入清 bit0（开放电）——此时解锁完成，`unlock_step = UNLOCKED`，bit12 清零，`charge_enabled`/`discharge_enabled` 按最后写入值生效；
   - 顺序不对（比如锁定时直接写"全开"，或者先开放电再开充电）则该次写入被静默忽略，MOS 状态和 bit12 都不变——贴近真板"切不动"的观感。
   - 解锁流程之外，`faults.py` 也支持手动/脚本直接触发 bit12（模拟"软件锁定"这个故障本身），触发时立即强制 `charge_enabled = discharge_enabled = False` 并重置 `unlock_step = LOCKED`。

4. **配置寄存器范围：只做协议文档明确给出的。** 核心链路阶段只实现 `0x00`（进配置模式，校验魔数 `0x5678`）、`0x01`（退出并保存）、`0xAA`（错误/保护触发计数）、`0xE1`（MOS 控制）、`0xE2`（均衡开关）。未进配置模式时读任何配置类寄存器一律回状态 `0x80`。协议三表第三节"保护参数表"里 `0x10~0xA2` 的具体地址标的是"查真机"，暂不通过串口暴露；对应的保护阈值（单体过压 4.25V、单体欠压 2.80V…）作为 Python 常量集中放在 `faults.py` 顶部一个 `PROTECTION_THRESHOLDS` 结构里，每项都带"真机校准点"注释，等真机到手后按校准清单第 2 条替换。

5. **场景回放用 YAML。** 引入 `pyyaml` 依赖（轻量、纯配置解析，不涉及仿真逻辑）。场景文件描述一个时间线：`at_seconds` + 动作（设置电流、注入/清除故障、开关 MOS、跳 SOC……），`cli.py` 里的回放器按时间顺序 sleep + 执行，保证演示可复现。

## 电池仿真模型细节

- **OCV-SOC 曲线**：需求里给的 12 个锚点（100%→4.20V … 0%→3.00V），线性插值取中间值。
- **单体独立状态**：`Cell` 类持有 `soc`（0~100）、`capacity_bias`（容量倍率，默认 1.0）、`resistance_bias`（内阻倍率，默认 1.0，3 号电芯默认设置成容量小 5%/内阻大 20% 制造离散）。
- **库仑计数**：`soc_delta = current_mA * dt_hours / (rated_capacity_mAh * capacity_bias) * 100`，充电为正、放电为负（电流符号约定：正=充电，来自协议 0.19 行；三表本身也标注"实测再确认符号方向"，模拟器先按文档约定实现，真机校准清单第 4 条核对后如需翻转，只改 `battery.py` 里一个常量）。
- **温度模型**：一阶 RC 热模型，`dT/dt = (I^2 * R_thermal - (T - T_ambient) / tau) `形式的简化实现，多路 NTC 在核心温度基础上加各自固定的小偏置 + 微小噪声。
- **内阻压降**：端电压 = OCV(soc) − I × R_internal(cell)，卸载（I=0）时瞬间回到 OCV，不做 RC 弛豫动画（YAGNI，真板子也只关心稳态读数）。

## 故障注入

`faults.py` 里 `FaultManager`：
- 故障类型 = 协议三表 2.3 节的 11 个保护位（不含 bit11 前端 IC 错误、bit12 MOS 锁定，这两个单独处理：IC 错误暂不建模，MOS 锁定见上）。
- 每个故障可配置：`trigger_mode`（instant/delayed，延时用秒数）、`clear_mode`（auto/manual）。
- 触发条件：故障对应的物理量（单体电压、组电压、温度、电流）超过 `PROTECTION_THRESHOLDS` 里的阈值时置位对应 bit，并按协议语义联动 MOS（如充电过温 → 强制 `charge_enabled=False`，放电过流 → 强制 `discharge_enabled=False`）；`clear_mode=auto` 时物理量回落到恢复阈值内自动清位并恢复 MOS（若无其他故障占用该 MOS）。
- 也支持脚本/CLI 直接 `inject(fault_type)` / `clear(fault_type)` 手动摆位，不依赖物理量，方便演示。

## 协议帧处理

- `FrameDecoder.feed(data: bytes) -> list[Frame]`：内部维护一个 `bytearray` 缓冲区，扫描 `0xDD`，按协议头部的长度字段计算整帧长度，长度不够就继续等下一批字节（半包），凑够了校验 `0x77` 结尾并做校验和验证，验证失败则丢弃这个 `0xDD` 重新从下一个字节找起（不整体清空缓冲区，避免吞掉后续正常帧）。
- 校验函数分别为 `checksum_request(register, length, data) -> int` 和 `checksum_response(status, length, data) -> int`，均返回 `(0x10000 - sum) & 0xFFFF`。
- `Frame` 用 `dataclass` 表示，区分请求/响应两种（字段不同：寄存器+长度+数据 vs 寄存器+状态+长度+数据）。

## 测试计划

- `test_protocol.py`：黄金请求向量 `DD A5 03 00 FF FD 77` 编解码 round-trip；黄金响应向量（29 字节数据区）解码后所有字段与文档比对（15.47V/0.00A/4.93Ah/5.00Ah/0 次/2022-03-28/无均衡/保护0x1000/8.0/99%/MOS 0x02/4 串/3 温感/24.5/24.2/23.7℃）；半包/粘包场景（拆成多次 `feed` 调用、多帧粘在一起）。
- `test_battery.py`：恒流充/放一段模拟时间后 SOC 变化量在容差内；OCV 插值边界值；内阻压降方向正确；3 号电芯偏置生效（同等条件下比其他电芯先到极值）。
- `test_faults.py`：每类故障注入后保护位正确置位、对应 MOS 正确动作；MOS 锁定后乱序写 0xE1 不生效，按"全关→开充→开放"顺序能解锁；delayed/auto-clear 故障的时间行为。

## 分步提交计划

1. `protocol.py` + `test_protocol.py`（黄金向量通过）+ 空的 `battery.py`/`faults.py` 骨架。
2. `battery.py`（电池+温度+内阻模型）+ `test_battery.py`。
3. `faults.py`（故障注入+MOS 锁定状态机）+ `test_faults.py`。
4. `device.py`（命令分发，接协议与模型）+ 补充 device 层测试（可并入现有测试文件或视情况新增）。
5. `server.py`（真实串口收发）+ `cli.py`（REPL）+ `scenarios/` 示例 + README（含 com0com/socat 两套虚拟串口说明）。

每一步完成后单独 `git commit`，提交信息说明本步做了什么。
