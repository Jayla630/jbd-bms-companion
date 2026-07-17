// jbd-codec 黄金向量对账测试(node --test,零依赖)
// 事实来源:docs/阶段0_JBD-SP04S010_协议参考三表.md
// 这是继 Python protocol.py、C# Jbd.Protocol 之后的第三套独立实现,交叉验证在此落锚。
const test = require('node:test');
const assert = require('node:assert/strict');

const {
  REG_BASIC, REG_CELLS, REG_MOS, REG_BAL,
  checksumResponse,
  encodeRead, encodeWrite, encodeMosControl, encodeBalanceControl,
  parseBasicInfo, parseCellVoltages, parseWriteAck,
  FrameAccumulator,
} = require('../utils/jbd-codec');

const hex = (bytes) => Array.from(bytes, (b) => b.toString(16).padStart(2, '0').toUpperCase()).join(' ');
const fromHex = (s) => Uint8Array.from(s.replace(/\s+/g, '').match(/../g).map((h) => parseInt(h, 16)));

// 用已被黄金向量验证过的 checksumResponse 构造响应帧
function makeResponse(register, status, data) {
  const cs = checksumResponse(status, data);
  return Uint8Array.from([0xDD, register, status, data.length, ...data, cs >> 8, cs & 0xFF, 0x77]);
}

// 黄金向量②:读基础信息响应(真实抓包,4 串 / 3 温感)
const GOLDEN_BASIC = fromHex(
  'DD 03 00 1D 06 0B 00 00 01 ED 01 F4 00 00 2C 7C 00 00 00 00 10 00 80 63 02 04 03 0B A0 0B 9D 0B 98 FA 55 77');

test('encodeRead(0x03) 编出黄金请求 DD A5 03 00 FF FD 77', () => {
  assert.equal(hex(encodeRead(REG_BASIC)), 'DD A5 03 00 FF FD 77');
});

test('encodeMosControl 三档控制字校验和正确', () => {
  assert.equal(hex(encodeMosControl(0x0003)), 'DD 5A E1 02 00 03 FF 1A 77'); // 全关
  assert.equal(hex(encodeMosControl(0x0001)), 'DD 5A E1 02 00 01 FF 1C 77'); // 开充
  assert.equal(hex(encodeMosControl(0x0000)), 'DD 5A E1 02 00 00 FF 1D 77'); // 全放
});

test('encodeWrite 通用写帧形状(0xE2 数据含义查真机,只验帧结构)', () => {
  assert.equal(hex(encodeWrite(REG_BAL, [0x00, 0x01])), 'DD 5A E2 02 00 01 FF 1B 77');
  assert.equal(hex(encodeBalanceControl(true)), 'DD 5A E2 02 00 01 FF 1B 77');
  assert.equal(hex(encodeBalanceControl(false)), 'DD 5A E2 02 00 00 FF 1C 77');
});

test('parseBasicInfo 黄金响应逐字段对账', () => {
  const info = parseBasicInfo(GOLDEN_BASIC);
  assert.equal(info.total_voltage, 15.47);
  assert.equal(info.current, 0);
  assert.equal(info.soc, 99);
  assert.equal(info.protection_status, 0x1000);
  assert.equal(info.cell_count, 4);
  assert.deepEqual(info.temperature, [24.5, 24.2, 23.7]);
});

test('MOS 陷阱:状态字 0x02 → 充电关、放电开;bit12 → 锁定', () => {
  const info = parseBasicInfo(GOLDEN_BASIC);
  assert.equal(info.mos_charge, false);    // 状态字开启语义 bit0
  assert.equal(info.mos_discharge, true);  // 状态字开启语义 bit1
  assert.equal(info.mos_locked, true);     // 保护状态 bit12
});

test('电流符号:0xFF6A 必须读成 -1.50 A(不是 +653.86)', () => {
  const d = Array.from(GOLDEN_BASIC.slice(4, 4 + GOLDEN_BASIC[3]));
  d[2] = 0xFF; d[3] = 0x6A;
  const frame = makeResponse(REG_BASIC, 0x00, d);
  assert.equal(parseBasicInfo(frame).current, -1.5);
});

test('parseBasicInfo 非法帧返回 null(错寄存器 / 坏校验 / 0x80 出错帧)', () => {
  assert.equal(parseBasicInfo(makeResponse(REG_CELLS, 0x00, [0, 0])), null);
  const bad = Uint8Array.from(GOLDEN_BASIC); bad[10] ^= 0xFF;
  assert.equal(parseBasicInfo(bad), null);
  assert.equal(parseBasicInfo(makeResponse(REG_BASIC, 0x80, [])), null);
});

test('parseCellVoltages:4 串 mV 大端 → 伏特 + delta_mv', () => {
  const frame = makeResponse(REG_CELLS, 0x00, [0x0C, 0xE5, 0x0C, 0xE4, 0x0C, 0xE2, 0x0C, 0xE9]);
  const r = parseCellVoltages(frame);
  assert.deepEqual(r.cell_voltages, [3.301, 3.3, 3.298, 3.305]);
  assert.equal(r.delta_mv, 7);
});

test('parseWriteAck:0x00 设备吃下 / 0x80 拒绝 / 错寄存器无效', () => {
  assert.deepEqual(parseWriteAck(makeResponse(REG_MOS, 0x00, []), REG_MOS), { valid: true, accepted: true });
  assert.deepEqual(parseWriteAck(makeResponse(REG_MOS, 0x80, []), REG_MOS), { valid: true, accepted: false });
  assert.deepEqual(parseWriteAck(makeResponse(REG_BAL, 0x00, []), REG_MOS), { valid: false, accepted: false });
});

test('拆帧器:黄金响应按 20 字节 MTU 分片喂入 → 恰好 1 帧且字段正确', () => {
  const acc = new FrameAccumulator();
  let frames = [];
  for (let i = 0; i < GOLDEN_BASIC.length; i += 20) {
    frames = frames.concat(acc.feed(Array.from(GOLDEN_BASIC.slice(i, i + 20))));
  }
  assert.equal(frames.length, 1);
  assert.equal(parseBasicInfo(frames[0]).total_voltage, 15.47);
});

test('拆帧器:前置噪声(含伪 0xDD)+ 双帧粘包 → 吐出 2 帧,各自校验通过', () => {
  const garbage = [0x01, 0x02, 0xDD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
  const acc = new FrameAccumulator();
  const frames = acc.feed([...garbage, ...GOLDEN_BASIC, ...GOLDEN_BASIC]);
  assert.equal(frames.length, 2);
  for (const f of frames) assert.equal(parseBasicInfo(f).total_voltage, 15.47);
});

test('parseBasicInfo.balance:黄金响应无均衡 → false 且 balance_bits === 0', () => {
  const info = parseBasicInfo(GOLDEN_BASIC);
  assert.equal(info.balance, false);
  assert.equal(info.balance_bits, 0);
});

test('parseBasicInfo.balance:1、3 串均衡中(数据区偏移12 = 0x0005)→ true,位图逐串可查', () => {
  const d = Array.from(GOLDEN_BASIC.slice(4, 4 + GOLDEN_BASIC[3]));
  d[12] = 0x00; d[13] = 0x05;
  const frame = makeResponse(REG_BASIC, 0x00, d);
  const info = parseBasicInfo(frame);
  assert.notEqual(info, null);
  assert.equal(info.balance, true);
  assert.equal(info.balance_bits, 0x0005);
  assert.equal((info.balance_bits >> 0) & 1, 1);
  assert.equal((info.balance_bits >> 1) & 1, 0);
  assert.equal((info.balance_bits >> 2) & 1, 1);
});

test('parseBasicInfo.balance_bits:17~32 串位图(偏移14)进高 16 位', () => {
  const d = Array.from(GOLDEN_BASIC.slice(4, 4 + GOLDEN_BASIC[3]));
  d[14] = 0x80; d[15] = 0x01; // 17 串与 32 串在均衡
  const frame = makeResponse(REG_BASIC, 0x00, d);
  const info = parseBasicInfo(frame);
  assert.equal(info.balance_bits, ((0x8001 << 16) >>> 0));
  assert.equal((info.balance_bits >>> 16) & 1, 1);
  assert.equal((info.balance_bits >>> 31) & 1, 1);
  assert.equal(info.balance, true);
});

test('parseBasicInfo 容量/循环:黄金响应 4930 mAh / 5000 mAh / 0 次(docs 2.4 偏移 4/6/8)', () => {
  const info = parseBasicInfo(GOLDEN_BASIC);
  assert.equal(info.remain_capacity, 4930);   // 01 ED = 493 → ×10 mAh
  assert.equal(info.design_capacity, 5000);   // 01 F4 = 500 → ×10 mAh
  assert.equal(info.cycle_count, 0);          // 00 00
});

test('拆帧器:半帧滞留缓冲,补齐后一次吐出', () => {
  const acc = new FrameAccumulator();
  assert.equal(acc.feed(Array.from(GOLDEN_BASIC.slice(0, 10))).length, 0);
  const frames = acc.feed(Array.from(GOLDEN_BASIC.slice(10)));
  assert.equal(frames.length, 1);
});
