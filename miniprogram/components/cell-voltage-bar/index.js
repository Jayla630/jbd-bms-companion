Component({
  properties: {
    label: String, // 如 "#1 最高"
    value: String, // 如 "3.945"
    accent: { type: String, value: '' }, // '' | normal(最高) | info(最低) | warning(最低且压差大)
    heightRpx: { type: Number, value: 80 }, // 柱体填充高度,页面按电压归一化计算
    balancing: { type: Boolean, value: false },
  },
});
