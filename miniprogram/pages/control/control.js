const store = require('../../services/store');
const bms = require('../../services/mock-bms');
const { isLocked } = require('../../utils/bms');

Page({
  data: { connected: false, locked: false },

  onLoad() {
    this.unsub = store.subscribe((s) => this.render(s));
  },
  onUnload() {
    this.unsub && this.unsub();
  },

  render(s) {
    const connected = s.link === 'connected';
    const locked = isLocked(s.protection_status);
    const mkSw = (key) => ({
      checked: key === 'balance' ? s.balance : s['mos_' + key],
      pending: s.pend[key] !== null,
      pendingTarget: !!s.pend[key],
      lockDisabled: locked && key !== 'balance',
    });
    this.setData({
      connected,
      locked,
      link: s.link,
      deviceName: s.device_name,
      sw: { charge: mkSw('charge'), discharge: mkSw('discharge'), balance: mkSw('balance') },
    });
  },

  onToggle(e) {
    const result = bms.toggleSwitch(e.currentTarget.dataset.key);
    if (result === 'locked') this.goProtection();
  },
  goConnect() {
    wx.navigateTo({ url: '/pages/connect/connect' });
  },
  goProtection() {
    wx.switchTab({ url: '/pages/protection/protection' });
  },
  onBannerTap() {
    if (!this.data.connected) this.goConnect();
  },
});
