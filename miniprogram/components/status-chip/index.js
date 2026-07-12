const { BIT_MOS_LOCKED } = require('../../utils/bms');

Component({
  properties: {
    label: String,
    bit: Number,
    active: Boolean,
  },
  data: { cls: 'idle', mark: '●' },
  observers: {
    'bit, active'(bit, active) {
      if (!active) this.setData({ cls: 'idle', mark: '●' });
      else if (bit === BIT_MOS_LOCKED) this.setData({ cls: 'locked', mark: '锁定' });
      else this.setData({ cls: 'danger', mark: '触发' });
    },
  },
});
