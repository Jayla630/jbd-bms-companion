// 解锁弹层:三步 0xE1(0x0003 全关 → 0x0001 开充 → 0x0000 开放)+ 回读 bit12 裁决。
// stage 含义见 utils/bms.js UNLOCK_STAGES:0 idle,1-6 三步 send/ack,7 裁决中,8 成功,9 失败。
const CMDS = ['0x0003', '0x0001', '0x0000'];
const DESCS = ['充放电全关', '开启充电', '开启放电'];
const TITLES = ['步骤 1 · 全部关断', '步骤 2 · 开启充电', '步骤 3 · 开启放电'];

Component({
  properties: {
    show: Boolean,
    stage: { type: Number, value: 0 },
  },
  data: {
    head: '—',
    headCls: 'info',
    barW: '0%',
    barCls: 'info',
    steps: [],
    aj: {},
    foot: 'run', // run | success | fail
  },
  observers: {
    stage(st) {
      const heads = ['—', '1 / 3', '1 / 3', '2 / 3', '2 / 3', '3 / 3', '3 / 3', '裁决中', '成功', '失败'];
      const barWs = ['0%', '14%', '30%', '46%', '62%', '76%', '90%', '100%', '100%', '100%'];
      const tone = st === 8 ? 'normal' : st === 9 ? 'danger' : 'info';

      const steps = [1, 2, 3].map((n) => {
        const send = 2 * n - 1;
        const ack = 2 * n;
        const fin = st > ack || st === ack;
        const sending = st === send;
        return {
          title: TITLES[n - 1],
          cls: fin ? 'done' : sending ? 'sending' : 'todo',
          glyph: fin ? '✓' : String(n),
          sub: fin
            ? CMDS[n - 1] + ' 已确认 · ack OK'
            : sending
              ? '下发 ' + CMDS[n - 1] + ',等待设备 ack…'
              : CMDS[n - 1] + ' · ' + DESCS[n - 1],
        };
      });

      let aj;
      if (st < 7) aj = { cls: 'todo', glyph: '↺', sub: '三步 ack 完成 ≠ 成功,由回读结果裁决' };
      else if (st === 7) aj = { cls: 'sending', glyph: '', sub: '读取 protection_status,校验 bit12 是否清零…' };
      else if (st === 8) aj = { cls: 'done', glyph: '✓', sub: 'bit12 = 0 · 解锁生效,双 MOS 恢复导通' };
      else aj = { cls: 'fail', glyph: '✕', sub: 'bit12 仍为 1 · 解锁未生效,可从头重跑' };

      this.setData({
        head: heads[st],
        headCls: tone,
        barW: barWs[st],
        barCls: tone,
        steps,
        aj,
        foot: st === 8 ? 'success' : st === 9 ? 'fail' : 'run',
      });
    },
  },
  methods: {
    onCancel() {
      this.triggerEvent('cancel');
    },
    onRetry() {
      this.triggerEvent('retry');
    },
    onFinish() {
      this.triggerEvent('finish');
    },
    noop() {},
  },
});
