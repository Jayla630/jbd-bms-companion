// mock BMS 服务:接口形状即未来 BLE 层的形状(scan/connect/disconnect/toggle/unlock)。
// 行为与 Claude Design 原型脚本一致:扫描 1.5s、连接 1.2s(设备 c 必失败)、
// 开关回读落定 1.3s、解锁三步 send/ack 后回读 bit12 裁决。
const store = require('./store');
const { BIT_MOS_LOCKED, isLocked } = require('../utils/bms');

const timers = {};
let unlockRunId = 0;
let jitterTimer = null;

// 联调开关:置 true 可演示"三步 ack 全过但回读 bit12 仍为 1"的失败路径
const config = { unlockFails: false };

function later(key, ms, fn) {
  clearTimeout(timers[key]);
  timers[key] = setTimeout(fn, ms);
}

function toast(title) {
  wx.showToast({ title, icon: 'none' });
}

function patchDevice(id, devState) {
  store.setState({
    devices: store.state.devices.map((d) => (d.id === id ? { ...d, state: devState } : d)),
  });
}

// ---- 扫描 / 连接 ----

function startScan() {
  if (store.state.scan === 'scanning') return;
  store.setState({
    scan: 'scanning',
    devices: store.state.devices.map((d) => (d.state === 'connected' ? d : { ...d, state: 'idle' })),
  });
  later('scan', 1500, () => store.setState({ scan: 'done' }));
}

function connect(id) {
  const dev = store.state.devices.find((d) => d.id === id);
  if (!dev || dev.state === 'connecting' || dev.state === 'connected') return;
  if (id === 'c') {
    patchDevice(id, 'connecting');
    later('conn', 1300, () => patchDevice(id, 'failed'));
    return;
  }
  store.setState({
    link: 'connecting',
    devices: store.state.devices.map((d) =>
      d.id === id ? { ...d, state: 'connecting' } : { ...d, state: d.id === 'c' ? d.state : 'idle' }
    ),
  });
  later('conn', 1200, () => {
    patchDevice(id, 'connected');
    store.setState({
      link: 'connected',
      device_name: dev.name,
      pend: { charge: null, discharge: null, balance: null },
    });
    toast('已连接 · ' + dev.name);
    startJitter();
  });
}

function disconnect() {
  stopJitter();
  // 断线:叫停所有"等回读"的在途操作——三个开关落定定时器 + 解锁链条,
  // 否则它们会在已断开的设备上落定假回读 / 报假解锁成功,违背"回读为准"。
  ['sw_charge', 'sw_discharge', 'sw_balance', 'unlock'].forEach((k) => clearTimeout(timers[k]));
  unlockRunId++; // 令 runUnlock 里尚未触发的 later 回调失效(其内部已有 unlockRunId 守卫)
  store.setState({
    link: 'disconnected',
    devices: store.state.devices.map((d) => ({ ...d, state: 'idle' })),
    pend: { charge: null, discharge: null, balance: null },
    unlock_stage: 0,
  });
  toast('已断开连接');
}

// ---- 实时数据抖动(模拟 2s 轮询) ----

const BASE_CELLS = [3.945, 3.938, 3.951, 3.942];
const BASE_TEMPS = [24.5, 25.1];

function startJitter() {
  stopJitter();
  jitterTimer = setInterval(() => {
    const s = store.state;
    if (s.link !== 'connected') return;
    const cell_voltages = s.cell_voltages.map((v, i) =>
      Math.min(BASE_CELLS[i] + 0.003, Math.max(BASE_CELLS[i] - 0.003, v + (Math.random() - 0.5) * 0.002))
    );
    const base = s.current;
    const current = base === 0 ? 0 : Math.round((base + (Math.random() - 0.5) * 0.06) * 100) / 100;
    const temperature = s.temperature.map((t, i) =>
      Math.min(BASE_TEMPS[i] + 0.4, Math.max(BASE_TEMPS[i] - 0.4, Math.round((t + (Math.random() - 0.5) * 0.2) * 10) / 10))
    );
    store.setState({ cell_voltages, current, temperature });
  }, 2000);
}

function stopJitter() {
  clearInterval(jitterTimer);
  jitterTimer = null;
}

// ---- 开关(回读落定,无乐观更新) ----
// 返回值:'ok' 已受理 | 'locked' 软件锁定中(充/放不可操作) | 'offline' 未连接

function toggleSwitch(key) {
  const s = store.state;
  if (s.link !== 'connected') {
    toast('未连接设备,无法下发命令');
    return 'offline';
  }
  if (isLocked(s.protection_status) && key !== 'balance') return 'locked';
  if (s.pend[key] !== null) return 'ok';
  const cur = key === 'balance' ? s.balance : s['mos_' + key];
  const target = !cur;
  store.setState({ pend: { ...s.pend, [key]: target } });
  later('sw_' + key, 1300, () => {
    const st = store.state;
    store.setState({
      pend: { ...st.pend, [key]: null },
      ...(key === 'balance' ? { balance: target } : { ['mos_' + key]: target }),
    });
    const name = { charge: '充电 MOS', discharge: '放电 MOS', balance: '均衡' }[key];
    toast('回读确认:' + name + (target ? '已开启' : '已关闭'));
  });
  return 'ok';
}

// ---- 三步解锁(0xE1: 0x0003→0x0001→0x0000,回读 bit12 裁决) ----

function runUnlock() {
  const id = ++unlockRunId;
  store.setState({ unlock_stage: 1 });
  // [目标阶段, 距上一阶段的延时ms]:send 800ms 等 ack,ack 350ms 后发下一步,裁决 1600ms
  const seq = [[2, 800], [3, 350], [4, 800], [5, 350], [6, 800], [7, 500], [8, 1600]];
  let i = 0;
  const next = () => {
    if (i >= seq.length) return;
    const [stage, ms] = seq[i++];
    later('unlock', ms, () => {
      if (unlockRunId !== id) return;
      if (stage === 8) {
        if (config.unlockFails) {
          store.setState({ unlock_stage: 9 });
        } else {
          store.setState({
            unlock_stage: 8,
            protection_status: store.state.protection_status & ~(1 << BIT_MOS_LOCKED),
            mos_charge: true,
            mos_discharge: true,
          });
          toast('解锁成功 · 双 MOS 恢复导通');
        }
      } else {
        store.setState({ unlock_stage: stage });
      }
      next();
    });
  };
  next();
}

function cancelUnlock() {
  unlockRunId++;
  store.setState({ unlock_stage: 0 });
}

module.exports = { config, startScan, connect, disconnect, toggleSwitch, runUnlock, cancelUnlock };
