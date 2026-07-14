// JBD BMS 帧编解码(纯函数 CommonJS,零 wx.* 依赖,可在 Node 下跑黄金向量)
// 事实来源:docs/阶段0_JBD-SP04S010_协议参考三表.md
// 继 Python protocol.py、C# Jbd.Protocol 之后的第三套独立实现,拆帧行为与 C# FrameAccumulator 对齐。

const START = 0xDD;
const END = 0x77;
const OP_READ = 0xA5;
const OP_WRITE = 0x5A;
const REG_BASIC = 0x03;
const REG_CELLS = 0x04;
const REG_MOS = 0xE1;
const REG_BAL = 0xE2;
const STATUS_OK = 0x00;

// ---- 校验和(请求/响应覆盖范围不对称,勿把响应里的寄存器字节算进去) ----
// 请求 = 寄存器 + 长度 + Σ数据;响应 = 状态 + 长度 + Σ数据;两侧都不含 0xDD/0x77。
function checksum(leadByte, data) {
  let sum = leadByte + data.length;
  for (const b of data) sum += b;
  return (0x10000 - sum) & 0xFFFF; // 高字节在前
}
const checksumRequest = (register, data) => checksum(register, data);
const checksumResponse = (status, data) => checksum(status, data);

// ---- 编码 ----
function encodeRead(register) {
  const cs = checksumRequest(register, []);
  return Uint8Array.from([START, OP_READ, register, 0x00, cs >> 8, cs & 0xFF, END]);
}

function encodeWrite(register, data) {
  const d = Array.from(data);
  const cs = checksumRequest(register, d);
  return Uint8Array.from([START, OP_WRITE, register, d.length, ...d, cs >> 8, cs & 0xFF, END]);
}

// 0xE1 控制字(u16 大端):关闭语义,bit0=1 关放电、bit1=1 关充电。
// 0x0003=全关 / 0x0001=开充 / 0x0000=全放开。与 0x03 状态字的开启语义相反且位对换,勿混用。
const encodeMosControl = (word) => encodeWrite(REG_MOS, [word >> 8, word & 0xFF]);

// 0xE2 均衡开关:数据字节含义 docs 未落锚(查真机),此处按 u16 大端 0x0001=启用/0x0000=关闭编帧。
const encodeBalanceControl = (enabled) => encodeWrite(REG_BAL, [0x00, enabled ? 0x01 : 0x00]);

// ---- 拆帧器(BLE notify 分片=串口碎片到达;只按响应帧语义收帧) ----
const MAX_DATA = 64;
const OVERHEAD = 7; // 起始+寄存器+状态+长度+校验2+结束

class FrameAccumulator {
  constructor() {
    this.buf = [];
  }
  // 喂入任意字节碎片,返回本次凑齐的完整合法帧(可能为空)。
  // 坏帧(结尾不对/长度离谱/校验不符)只丢 1 字节重新找 0xDD,不清空缓冲,避免吞掉后续真帧。
  feed(bytes) {
    for (const b of bytes) this.buf.push(b);
    const frames = [];
    while (true) {
      const start = this.buf.indexOf(START);
      if (start < 0) { this.buf.length = 0; break; }
      if (start > 0) this.buf.splice(0, start);
      if (this.buf.length < 4) break;
      const len = this.buf[3];
      if (len > MAX_DATA) { this.buf.shift(); continue; }
      const total = len + OVERHEAD;
      if (this.buf.length < total) break;
      if (this.buf[total - 1] !== END) { this.buf.shift(); continue; }
      const data = this.buf.slice(4, 4 + len);
      const cs = checksumResponse(this.buf[2], data);
      if (this.buf[total - 3] !== (cs >> 8) || this.buf[total - 2] !== (cs & 0xFF)) {
        this.buf.shift();
        continue;
      }
      frames.push(this.buf.slice(0, total));
      this.buf.splice(0, total);
    }
    return frames;
  }
}

// ---- 解析 ----
const u16 = (d, o) => (d[o] << 8) | d[o + 1];
const s16 = (d, o) => { const v = u16(d, o); return v >= 0x8000 ? v - 0x10000 : v; };

// 结构校验(起始/结束/寄存器回显/长度/校验和),返回 { status, data } 或 null
function splitResponse(frame, expectedReg) {
  const f = Array.from(frame);
  if (f.length < OVERHEAD || f[0] !== START || f[f.length - 1] !== END) return null;
  if (f[1] !== expectedReg) return null;
  const len = f[3];
  if (f.length !== len + OVERHEAD) return null;
  const status = f[2];
  const data = f.slice(4, 4 + len);
  const cs = checksumResponse(status, data);
  if (f[f.length - 3] !== (cs >> 8) || f[f.length - 2] !== (cs & 0xFF)) return null;
  return { status, data };
}

// 结构合法且状态成功时返回数据区,否则 null(0x80=设备报错帧)
function unwrapResponse(frame, expectedReg) {
  const r = splitResponse(frame, expectedReg);
  return r && r.status === STATUS_OK ? r.data : null;
}

// 0x03 基础信息 → store 字段契约(snake_case),非法帧返回 null
function parseBasicInfo(frame) {
  const d = unwrapResponse(frame, REG_BASIC);
  if (!d || d.length < 23) return null;
  const protection_status = u16(d, 16);
  const fet = d[20]; // 状态字开启语义:bit0=充电开、bit1=放电开(与 0xE1 控制字相反)
  const ntc = d[22];
  const temperature = [];
  for (let i = 0; i < ntc && 23 + i * 2 + 1 < d.length; i++) {
    temperature.push((u16(d, 23 + i * 2) - 2731) / 10);
  }
  return {
    total_voltage: u16(d, 0) / 100,    // ×10mV
    current: s16(d, 2) / 100,          // ×10mA,有符号,正充负放
    protection_status,                 // 位图见 docs 2.3
    soc: d[19],
    mos_charge: !!(fet & 0x01),
    mos_discharge: !!(fet & 0x02),
    mos_locked: !!((protection_status >> 12) & 1),
    cell_count: d[21],
    temperature,                       // 0.1K → ℃
    balance: u16(d, 12) !== 0 || u16(d, 14) !== 0, // 均衡位图(1~16串/17~32串)任一非零即在均衡
  };
}

// 0x04 单体电压 → { cell_voltages[], delta_mv },非法帧返回 null
function parseCellVoltages(frame) {
  const d = unwrapResponse(frame, REG_CELLS);
  if (!d || d.length === 0 || d.length % 2 !== 0) return null;
  const cells = [];
  for (let i = 0; i < d.length; i += 2) cells.push(u16(d, i) / 1000); // mV → V
  return {
    cell_voltages: cells,
    delta_mv: Math.round((Math.max(...cells) - Math.min(...cells)) * 1000),
  };
}

// 写命令回执:valid=帧结构与寄存器回显合法,accepted=状态字 0x00 设备吃下
function parseWriteAck(frame, expectedReg) {
  const r = splitResponse(frame, expectedReg);
  if (!r) return { valid: false, accepted: false };
  return { valid: true, accepted: r.status === STATUS_OK };
}

module.exports = {
  START, END, OP_READ, OP_WRITE,
  REG_BASIC, REG_CELLS, REG_MOS, REG_BAL, STATUS_OK,
  checksumRequest, checksumResponse,
  encodeRead, encodeWrite, encodeMosControl, encodeBalanceControl,
  FrameAccumulator,
  parseBasicInfo, parseCellVoltages, parseWriteAck,
};
