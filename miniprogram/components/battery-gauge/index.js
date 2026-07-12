Component({
  properties: {
    soc: { type: Number, value: 0 },
    remainMah: { type: Number, value: 0 },
    cycleCount: { type: Number, value: 0 },
    designCapacity: { type: Number, value: 2500 },
  },
  data: { tone: 'normal' },
  observers: {
    soc(v) {
      this.setData({ tone: v >= 50 ? 'normal' : v >= 20 ? 'warning' : 'danger' });
    },
  },
});
