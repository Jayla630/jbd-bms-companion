Component({
  properties: {
    title: String,
    checked: Boolean,
    pending: Boolean, // 命令已下发,等待回读
    pendingTarget: Boolean, // pending 时滑块停在目标位
    connected: Boolean,
    lockDisabled: Boolean, // bit12 锁定中的充/放开关
  },
  data: { knobOn: false, trackCls: 'off', sub1: '', sub2: '', subCls: '' },
  observers: {
    'checked, pending, pendingTarget, connected, lockDisabled'(checked, pending, target, connected, lockDisabled) {
      const knobOn = pending ? target : checked;
      let trackCls, sub1, sub2, subCls;
      if (pending) {
        trackCls = 'pend';
        sub1 = '命令已下发,等待回读确认…';
        sub2 = '';
        subCls = 'info';
      } else if (!connected) {
        trackCls = 'dis';
        sub1 = '未连接设备';
        sub2 = '';
        subCls = '';
      } else if (lockDisabled) {
        trackCls = 'dis';
        sub1 = '软件锁定中 · ';
        sub2 = '点击前往解锁';
        subCls = 'locked';
      } else {
        trackCls = checked ? 'on' : 'off';
        sub1 = '设备回读:';
        sub2 = checked ? '开' : '关';
        subCls = checked ? 'normal' : '';
      }
      this.setData({ knobOn, trackCls, sub1, sub2, subCls });
    },
  },
  methods: {
    onTap() {
      if (this.data.pending) return;
      this.triggerEvent('toggle');
    },
  },
});
