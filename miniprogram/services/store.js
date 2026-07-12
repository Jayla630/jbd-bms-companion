// 极简可订阅状态仓:页面 onLoad/onShow 时 subscribe,onUnload/onHide 时退订。
// 协议字段沿用 data-field 契约的 snake_case,后续换真实 BLE 层时页面不动。
const listeners = new Set();

const state = {
  // 连接
  link: 'disconnected', // disconnected | connecting | connected | dropped
  device_name: '',
  scan: 'idle', // idle | scanning | done
  devices: [
    { id: 'a', name: 'JBD-4S30A', desc: '0C:61:CF:A2:18:4E · -52 dBm', state: 'idle', mine: true },
    { id: 'b', name: 'JBD-SP04S-002', desc: '3A:9F:11:C8:07:D2 · -71 dBm', state: 'idle' },
    { id: 'c', name: 'BLE-Device-8F21', desc: '8D:02:5B:77:F1:9C · -84 dBm', state: 'idle' },
  ],

  // 实时数据(data-field 契约)
  soc: 80,
  cell_voltages: [3.945, 3.938, 3.951, 3.942],
  temperature: [24.5, 25.1],
  current: -1.5,
  cycle_count: 12,
  design_capacity: 2500,
  mos_charge: false,
  mos_discharge: false,
  balance: true,
  protection_status: 0x1000, // 初始 bit12=1:MOS 软件锁定,便于走通解锁流程

  // 开关"回读落定":非 null 表示命令已下发、等待回读的目标值
  pend: { charge: null, discharge: null, balance: null },

  // 解锁流程,值为 utils/bms.js UNLOCK_STAGES 的下标(0=idle … 8=success 9=failed)
  unlock_stage: 0,
};

function setState(patch) {
  Object.assign(state, patch);
  listeners.forEach((fn) => fn(state));
}

function subscribe(fn) {
  listeners.add(fn);
  fn(state);
  return () => listeners.delete(fn);
}

module.exports = { state, setState, subscribe };
