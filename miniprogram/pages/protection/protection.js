const store = require('../../services/store');
const bms = require('../../services/mock-bms');
const { PROTECTION_BITS, isLocked, triggeredBits } = require('../../utils/bms');

const GROUPS = ['电压', '温度', '电流', '系统'];

Page({
  data: { sheetOn: false, unlockStage: 0 },

  onLoad() {
    this.unsub = store.subscribe((s) => this.render(s));
  },
  onUnload() {
    this.unsub && this.unsub();
  },

  render(s) {
    const connected = s.link === 'connected';
    const locked = isLocked(s.protection_status);
    const trig = triggeredBits(s.protection_status);
    const others = trig.length - (locked ? 1 : 0);

    let sum;
    if (others > 0) {
      const names = trig.map((b) => b.label).slice(0, 2).join('、') + (trig.length > 2 ? ' 等' : '');
      sum = { icon: 'err', cls: 'danger', title: trig.length + ' 项保护触发', sub: names + ' · 请检查电池与负载' };
    } else if (locked) {
      sum = { icon: 'lock', cls: 'locked', title: 'MOS 软件锁定', sub: 'bit12 = 1 · 双 MOS 已关断,解锁后恢复输出' };
    } else {
      sum = { icon: 'ok', cls: 'normal', title: '全部正常', sub: '13 项保护 · 0 项触发 · 双 MOS 导通' };
    }

    const groups = GROUPS.map((g) => ({
      name: g,
      items: PROTECTION_BITS.filter((b) => b.group === g).map((b) => ({
        ...b,
        active: !!((s.protection_status >> b.bit) & 1),
      })),
    }));

    this.setData({
      connected,
      locked,
      sum,
      groups,
      trigCount: trig.length,
      mosC: s.mos_charge,
      mosD: s.mos_discharge,
      unlockStage: s.unlock_stage,
    });
  },

  openSheet() {
    this.setData({ sheetOn: true });
    bms.runUnlock();
  },
  onCancel() {
    bms.cancelUnlock();
    this.setData({ sheetOn: false });
  },
  onRetry() {
    bms.runUnlock();
  },
  onFinish() {
    bms.cancelUnlock();
    this.setData({ sheetOn: false });
  },
});
