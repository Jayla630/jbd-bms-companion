const store = require('../../services/store');
const bms = require('../../services/ble-bms');

Page({
  data: { mine: null, nearby: [], scanning: false },

  onLoad() {
    this.prevLink = store.state.link;
    this.unsub = store.subscribe((s) => this.render(s));
  },
  onShow() {
    if (store.state.link !== 'connected') bms.startScan();
  },
  onUnload() {
    this.unsub && this.unsub();
  },

  render(s) {
    this.setData({
      mine: s.devices.find((d) => d.mine),
      nearby: s.devices.filter((d) => !d.mine),
      scanning: s.scan === 'scanning',
    });
    // 连接成功后短暂停留,自动返回来源页
    if (s.link === 'connected' && this.prevLink !== 'connected') {
      setTimeout(() => wx.navigateBack({ fail: () => {} }), 900);
    }
    this.prevLink = s.link;
  },

  onConnect(e) {
    bms.connect(e.currentTarget.dataset.id);
  },
  onDisconnect() {
    bms.disconnect();
  },
  onRescan() {
    bms.startScan();
  },
});
