const store = require('../../services/store');
const { triggeredBits, isLocked, deltaMv, modeOf } = require('../../utils/bms');

Page({
  data: { connected: false },

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

    const cells = s.cell_voltages;
    const vmax = Math.max(...cells);
    const vmin = Math.min(...cells);
    const maxI = cells.indexOf(vmax);
    const minI = cells.indexOf(vmin);
    const delta = deltaMv(cells);
    const wide = delta > 40;
    const range = Math.max(vmax - vmin, 0.001);
    const cellItems = cells.map((v, i) => ({
      value: v.toFixed(3),
      label: '#' + (i + 1) + (i === maxI ? ' 最高' : i === minI ? ' 最低' : ''),
      accent: i === maxI ? 'normal' : i === minI ? (wide ? 'warning' : 'info') : '',
      heightRpx: Math.round(80 + ((v - vmin) / range) * 42),
      balancing: connected && s.balance && i === maxI,
    }));

    const total = cells.reduce((a, b) => a + b, 0);
    const mode = modeOf(s.current);
    const mosChip = (on, label) => ({
      text: label + ' ' + (on ? '开' : '关'),
      cls: on ? 'normal' : locked ? 'locked' : '',
    });

    const others = trig.length - (locked ? 1 : 0);
    let ps;
    if (others > 0) ps = { cls: 'danger', text: trig.length + ' 项保护触发 · 查看详情' };
    else if (locked) ps = { cls: 'locked', text: 'MOS 软件锁定中 · 前往解锁' };
    else ps = { cls: '', text: '保护状态 · 全部正常' };

    this.setData({
      connected,
      link: s.link,
      deviceName: s.device_name,
      soc: s.soc,
      remainMah: Math.round((s.design_capacity * s.soc) / 100),
      designCapacity: s.design_capacity,
      cycleCount: s.cycle_count,
      tv: total.toFixed(2),
      cur: (s.current > 0 ? '+' : '') + s.current.toFixed(2),
      modeTone: mode === 'charge' ? 'normal' : mode === 'discharge' ? 'info' : '',
      modeText: mode === 'charge' ? '充电中' : mode === 'discharge' ? '放电中' : '静置',
      power: Math.abs(total * s.current).toFixed(1),
      // 真机 NTC 探头数量由 0x03 帧决定,不一定是 2 路,缺位显示 --
      t1: s.temperature[0] != null ? s.temperature[0].toFixed(1) : '--',
      t2: s.temperature[1] != null ? s.temperature[1].toFixed(1) : '--',
      cellItems,
      deltaText: '压差 ' + delta + ' mV',
      deltaWide: wide,
      mos: { c: mosChip(s.mos_charge, '充MOS'), d: mosChip(s.mos_discharge, '放MOS'), b: mosChip(s.balance, '均衡') },
      ps,
      emptyTitle: s.link === 'dropped' ? '连接已断开' : '尚未连接保护板',
      emptySub:
        s.link === 'dropped'
          ? '与 ' + s.device_name + ' 的连接已断开,可尝试重新连接'
          : '连接 JBD 4S 蓝牙模块后,即可实时查看电池状态并下发控制命令',
      emptyBtn: s.link === 'dropped' ? '重新连接' : '扫描并连接设备',
    });
  },

  goConnect() {
    wx.navigateTo({ url: '/pages/connect/connect' });
  },
  onBannerTap() {
    // 已连接时也进设备页:那里有「断开」按钮,是断开连接的唯一入口
    this.goConnect();
  },
  goProtection() {
    wx.switchTab({ url: '/pages/protection/protection' });
  },
});
