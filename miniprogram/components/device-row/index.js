Component({
  properties: {
    name: String,
    desc: String, // MAC · RSSI
    state: { type: String, value: 'idle' }, // idle | connecting | connected | failed
    mine: Boolean, // "我的设备"行,图标绿色
  },
  data: { sub: '', subCls: 'mono', btnText: '连接', btnCls: 'primary', spinning: false },
  observers: {
    'state, desc'(state, desc) {
      const map = {
        connected: { sub: desc, subCls: 'mono', btnText: '已连接', btnCls: 'done', spinning: false },
        connecting: { sub: '正在建立连接…', subCls: 'info', btnText: '连接中', btnCls: 'busy', spinning: true },
        failed: { sub: '连接失败 · 设备无应答', subCls: 'danger', btnText: '重试', btnCls: 'retry', spinning: false },
      };
      this.setData(map[state] || { sub: desc, subCls: 'mono', btnText: '连接', btnCls: 'primary', spinning: false });
    },
  },
  methods: {
    onBtnTap() {
      if (this.data.state === 'connecting' || this.data.state === 'connected') return;
      this.triggerEvent('connect');
    },
    onDisconnect() {
      this.triggerEvent('disconnect');
    },
  },
});
