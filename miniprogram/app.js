const store = require('./services/store');
const { triggeredBits } = require('./utils/bms');

App({
  onLaunch() {
    // 保护 tab 红点跟随保护位图,全局只订阅一处
    store.subscribe((s) => {
      const n = triggeredBits(s.protection_status).length;
      if (n > 0) wx.showTabBarRedDot({ index: 2, fail: () => {} });
      else wx.hideTabBarRedDot({ index: 2, fail: () => {} });
    });
  },
});
