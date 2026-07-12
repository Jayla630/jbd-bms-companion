# miniprogram · JBD 4S BMS 手机管家（微信小程序）

原生微信小程序（无 uniapp/taro）。UI 结构、组件拆分与主题变量来自 Claude Design
移交的《BMS 小程序交互原型》，映射契约见
[`docs/阶段三_小程序设计移交分析.md`](../docs/阶段三_小程序设计移交分析.md)。

当前为**骨架阶段**：全流程用 mock 数据可点通（连接 → 总览实时抖动 → 开关回读落定 →
三步解锁回读裁决），尚未接入真实 BLE。

## 运行

1. 微信开发者工具 → 导入项目 → 选择本目录（`miniprogram/`），AppID 用测试号即可。
2. 编译后从「总览」空态点「扫描并连接设备」→ 连 `JBD-4S30A`。
3. 初始状态故意置 `protection_status bit12=1`（MOS 软件锁定）：
   「控制」页开关置灰 →「保护」页解锁横幅 → 三步解锁弹层 → 回读裁决成功后恢复。
   想演示失败路径，把 `services/mock-bms.js` 里 `config.unlockFails` 置 `true`。

## 结构

```
miniprogram/
├── app.json / app.wxss        # tabBar(总览/控制/保护) + 设计 Token(px×2=rpx)
├── pages/
│   ├── overview/              # 总览:battery-gauge、指标、单体电压、保护概览入口
│   ├── control/               # 控制:三张 mos-switch-card,回读落定无乐观更新
│   ├── protection/            # 保护:13 位 status-chip 网格 + unlock-stepper 弹层
│   └── connect/               # 设备连接:navigateTo 普通页,扫描/连接 mock 流程
├── components/                # 原型 data-component 一比一(tab-bar 由原生承接)
├── services/
│   ├── store.js               # 极简 pub/sub 状态仓,字段名沿 data-field 契约
│   └── mock-bms.js            # mock 服务,接口形状即未来 BLE 层
└── utils/bms.js               # 保护位表 / delta / mode 派生
```

## 下一步（接真机 BLE 时）

用 `wx.openBluetoothAdapter` 系列实现与 `mock-bms.js` 同形的
`scan/connect/disconnect/toggleSwitch/runUnlock`，写入同一个 store；页面层不动。
协议帧参考 `docs/阶段0_JBD-SP04S010_协议参考三表.md` 与 `upper-computer/` 的 C# 实现。
