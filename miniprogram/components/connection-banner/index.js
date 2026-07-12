Component({
  properties: {
    link: { type: String, value: 'disconnected' },
    deviceName: { type: String, value: '' },
  },
  data: { text: '', right: '— · —', tone: 'off', dotOn: false },
  observers: {
    'link, deviceName'(link, name) {
      const map = {
        connected: { text: '已连接 · ' + name, right: '实时 · 刚刚', tone: 'info', dotOn: true },
        connecting: { text: '连接中…', right: '— · —', tone: 'info', dotOn: false },
        dropped: { text: '连接已断开 · 点击重连', right: '— · —', tone: 'danger', dotOn: false },
      };
      this.setData(
        map[link] || { text: '未连接设备 · 点击连接', right: '— · —', tone: 'off', dotOn: false }
      );
    },
  },
  methods: {
    onTap() {
      this.triggerEvent('bannertap');
    },
  },
});
