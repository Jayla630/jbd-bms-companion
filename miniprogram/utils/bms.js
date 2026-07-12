// 保护位表与派生计算,与 docs/阶段三_小程序设计移交分析.md 的字段契约一致
const PROTECTION_BITS = [
  { bit: 0, key: 'cell_ov', label: '单体过压', group: '电压' },
  { bit: 1, key: 'cell_uv', label: '单体欠压', group: '电压' },
  { bit: 2, key: 'pack_ov', label: '整组过压', group: '电压' },
  { bit: 3, key: 'pack_uv', label: '整组欠压', group: '电压' },
  { bit: 4, key: 'chg_ot', label: '充电过温', group: '温度' },
  { bit: 5, key: 'chg_ut', label: '充电低温', group: '温度' },
  { bit: 6, key: 'dsg_ot', label: '放电过温', group: '温度' },
  { bit: 7, key: 'dsg_ut', label: '放电低温', group: '温度' },
  { bit: 8, key: 'chg_oc', label: '充电过流', group: '电流' },
  { bit: 9, key: 'dsg_oc', label: '放电过流', group: '电流' },
  { bit: 10, key: 'short', label: '短路保护', group: '电流' },
  { bit: 11, key: 'afe_err', label: '前端 IC 错误', group: '系统' },
  { bit: 12, key: 'mos_locked', label: 'MOS 软件锁定', group: '系统' },
];

const BIT_MOS_LOCKED = 12;

// 解锁流程阶段,下标即 store.unlock_stage
const UNLOCK_STAGES = [
  'idle',
  'step1_send', 'step1_ack',
  'step2_send', 'step2_ack',
  'step3_send', 'step3_ack',
  'adjudicating', 'success', 'failed',
];

function isLocked(protectionStatus) {
  return !!((protectionStatus >> BIT_MOS_LOCKED) & 1);
}

function triggeredBits(protectionStatus) {
  return PROTECTION_BITS.filter((b) => (protectionStatus >> b.bit) & 1);
}

function deltaMv(cells) {
  return Math.round((Math.max(...cells) - Math.min(...cells)) * 1000);
}

// 正=充电、负=放电、0=静置
function modeOf(current) {
  return current > 0 ? 'charge' : current < 0 ? 'discharge' : 'idle';
}

module.exports = { PROTECTION_BITS, BIT_MOS_LOCKED, UNLOCK_STAGES, isLocked, triggeredBits, deltaMv, modeOf };
