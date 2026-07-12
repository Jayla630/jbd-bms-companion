Component({
  properties: {
    label: String,
    value: String,
    unit: String,
    tone: { type: String, value: '' }, // '' | normal | info | warning | danger
    badge: { type: String, value: '' }, // 如电流卡的"充电中/放电中/静置"
  },
});
