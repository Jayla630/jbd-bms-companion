# jbd-bms-companion

一块真实 JBD SP04S010 智能 BMS 保护板（4 串三元 / 30A / 带均衡）的配套软件套件，包含 PC 上位机、微信小程序，以及一个用于解耦硬件的串口 BMS 模拟器。

## 项目结构

| 目录 | 内容 | 技术栈 |
|---|---|---|
| `simulator/` | 串口 BMS 模拟器：模拟 SP04S010 从站，让上位机开发不依赖真板子 | Python |
| `upper-computer/` | PC 上位机：连接串口轮询 0x03/0x04，实时显示电压/电流/SOC/单体电压 | C# / WPF + Prism |
| `miniprogram/` | 微信小程序：总览/控制/保护 + 设备连接，骨架已可点通（mock 数据，[README](miniprogram/README.md)） | 原生小程序 |
| `docs/` | 协议参考、寄存器表、保护参数、抓包记录 | — |

## 硬件

- 保护板：JBD-SP04S010 V1.1（4 串三元、30A、充放同口、带均衡、蓝牙 + 485）
- 电芯：EVE INR18650-25P ×4（2500mAh / 3.7V）
- 通信：USB 转 485（CH340）走 JBDTOOLS；蓝牙走官方"小象"App

## 开发阶段

- [x] 阶段〇 · 软件地基：协议三表、模拟器切片提示词
- [x] 阶段一 · 串口 BMS 模拟器（`simulator/`）
- [x] 阶段二 · 切片1 · PC 上位机读数据链路（[`upper-computer/`](upper-computer/README.md)）
- [x] 阶段二 · 切片2 · 写命令（0xE1 MOS / 0xE2 均衡）与保护状态展示（[`upper-computer/`](upper-computer/README.md)）
- [x] 阶段二 · 切片3 · MOS 软件锁定（bit12）的引导式三步解锁（[`upper-computer/`](upper-computer/README.md)）
- [ ] 阶段三 · 微信小程序
  - [x] 切片0 · 设计移交与骨架：Claude Design 原型解析、原生工程 + tabBar、组件拆分、mock 数据全流程可点通（[`miniprogram/`](miniprogram/README.md)）
  - [ ] 切片1 · 真机 BLE 数据链路
- [ ] 阶段四 · 真机联调与校准

## 快速开始

各子项目的运行方式见对应目录下的 README。模拟器从 [`simulator/README.md`](simulator/README.md) 开始；上位机从 [`upper-computer/README.md`](upper-computer/README.md) 开始，两者用虚拟串口对联调。

## 说明

模拟器与上位机之间的契约是 JBD 串口协议（字节流），两端语言无关。模拟器刻意用 Python 独立实现协议编解码，作为上位机 C# 实现的"第二意见"，用于交叉验证协议正确性。
