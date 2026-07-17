// 真机 BLE 服务:接口形状与 mock-bms.js 严格一致(scan/connect/disconnect/toggle/unlock)。
// 本段范围 = 阶段三·第二步(第一段):连接 + 服务发现 + 只读 0x03/0x04 轮询。
// 不发任何写命令——toggleSwitch/runUnlock/cancelUnlock 留桩,真正实现见第二段。
// 协议帧编解码全部复用 utils/jbd-codec.js,本文件不重复协议逻辑。
const store = require('./store');
const codec = require('../utils/jbd-codec');

// 查看来源:2026-07-16 Jayla 真机(HUAWEI VYG-AL00 + SP04S010)服务发现日志回填。
// 该板仅一个 service FF00;FF01 notify+read(板→手机),FF02 write+writeNoResponse(手机→板);
// 另有 FF03 write 通道,当前未用。
const SERVICE_UUID = '0000FF00-0000-1000-8000-00805F9B34FB';
const NOTIFY_CHAR_UUID = '0000FF01-0000-1000-8000-00805F9B34FB';
const WRITE_CHAR_UUID = '0000FF02-0000-1000-8000-00805F9B34FB';

const DEVICE_NAME_FILTER = 'JBD'; // 扫描结果按名称前缀过滤,避免列表被无关设备淹没
const SCAN_DURATION_MS = 4000;
const POLL_INTERVAL_MS = 1500; // 一问一答之间的间隔
const REQUEST_TIMEOUT_MS = 1000; // 单次读请求的超时

// 占位:与 mock-bms.js 导出形状对齐,第二段(写命令)才会真正用到
const config = {};

// ---- 连接期状态(全部私有,断线时统一清空) ----
let deviceId = null;
let connEpoch = 0; // 每次 connect/断线自增,令旧的 setTimeout/异步回调失效
let activeService = null;
let activeNotifyChar = null;
let activeWriteChar = null;
let accumulator = null;
let pollTimer = null;
let timeoutTimer = null;
let pendingRegister = null; // 当前正等待应答的寄存器(0x03/0x04),空闲为 null
let pollRegister = codec.REG_BASIC;

let deviceFoundRegistered = false;
let valueChangeRegistered = false;
let connectionStateRegistered = false;

function toast(title) {
  wx.showToast({ title, icon: 'none' });
}

// ---- ① ArrayBuffer <-> 字节数组(仅本文件内使用,不污染 jbd-codec.js) ----
function ab2bytes(buffer) {
  return Array.from(new Uint8Array(buffer));
}
function bytes2ab(bytes) {
  return new Uint8Array(bytes).buffer;
}

function patchDeviceState(id, devState) {
  store.setState({
    devices: store.state.devices.map((d) => (d.id === id ? { ...d, state: devState } : d)),
  });
}

// ---- ② 扫描 ----

function ensureDeviceFoundListener() {
  if (deviceFoundRegistered) return;
  deviceFoundRegistered = true;
  wx.onBluetoothDeviceFound((res) => {
    res.devices.forEach((dev) => {
      if (!dev.name || dev.name.indexOf(DEVICE_NAME_FILTER) < 0) return;
      const list = store.state.devices;
      if (list.some((d) => d.id === dev.deviceId)) return;
      store.setState({ devices: [...list, { id: dev.deviceId, name: dev.name, state: 'idle' }] });
    });
  });
}

function scanFail(err) {
  console.warn('[ble-bms] 扫描失败', err);
  toast('蓝牙不可用,请检查系统蓝牙开关与授权');
  store.setState({ scan: 'done' });
}

function startScan() {
  if (store.state.scan === 'scanning') return;
  store.setState({
    scan: 'scanning',
    devices: store.state.devices.map((d) => (d.state === 'connected' ? d : { ...d, state: 'idle' })),
  });
  wx.openBluetoothAdapter({
    success: () => {
      wx.startBluetoothDevicesDiscovery({
        allowDuplicatesKey: false,
        success: () => {
          ensureDeviceFoundListener();
          setTimeout(() => {
            wx.stopBluetoothDevicesDiscovery({ fail: () => {} });
            store.setState({ scan: 'done' });
          }, SCAN_DURATION_MS);
        },
        fail: scanFail,
      });
    },
    fail: scanFail,
  });
}

// ---- ⑦ 防御性清理:断线/连接失败时统一收口 ----

function stopPolling() {
  clearTimeout(pollTimer);
  clearTimeout(timeoutTimer);
  pollTimer = null;
  timeoutTimer = null;
  pendingRegister = null;
}

function teardownConnection() {
  const id = deviceId;
  connEpoch++; // 令所有捕获了旧 epoch 的 setTimeout 回调失效
  stopPolling();
  deviceId = null; // 先置空,再关闭连接:随后到达的 onBLEConnectionStateChange 会因 id 不符被过滤
  accumulator = null;
  activeService = null;
  activeNotifyChar = null;
  activeWriteChar = null;
  if (id) wx.closeBLEConnection({ deviceId: id, fail: () => {} });
}

function handleDropped() {
  if (store.state.link !== 'connected') return; // 用户主动 disconnect() 已经把 link 改掉,这里不重复处理
  teardownConnection();
  store.setState({ link: 'dropped' });
}

function ensureConnectionStateListener() {
  if (connectionStateRegistered) return;
  connectionStateRegistered = true;
  wx.onBLEConnectionStateChange((res) => {
    if (res.deviceId !== deviceId) return; // deviceId 已在 teardown 中置空,意味着这是旧连接的尾声事件
    if (!res.connected) handleDropped();
  });
}

// ---- ③ 连接 + 服务发现(核心产出:把真机 UUID 打印出来) ----

function connectFail(id, err, epoch) {
  if (epoch !== connEpoch) return;
  console.warn('[ble-bms] 连接失败', err);
  teardownConnection();
  patchDeviceState(id, 'failed');
  store.setState({ link: 'disconnected' });
  toast('连接失败,请重试');
}

function guessUuids(found) {
  const byService = {};
  found.forEach((c) => {
    (byService[c.serviceUuid] = byService[c.serviceUuid] || []).push(c);
  });
  const candidates = [];
  Object.keys(byService).forEach((serviceUuid) => {
    const chars = byService[serviceUuid];
    // 真机踩坑:SP04S010 的写通道 FF02 同时上报 notify=true,先挑"纯收不发"的通道再兜底,
    // 否则 find 会把同一个 characteristic 同时当收发两用,订阅错通道后所有读请求超时。
    const notifyChar =
      chars.find((c) => (c.properties.notify || c.properties.indicate) && !(c.properties.write || c.properties.writeNoResponse)) ||
      chars.find((c) => c.properties.notify || c.properties.indicate);
    const writeChar = chars.find((c) => c.properties.write || c.properties.writeNoResponse);
    if (notifyChar && writeChar) {
      candidates.push({ serviceUuid, notifyUuid: notifyChar.uuid, writeUuid: writeChar.uuid });
    }
  });
  if (candidates.length !== 1) {
    console.warn('[ble-bms] 候选 service/characteristic 组合数 = ' + candidates.length + '(需恰好 1 个才能自动判定):', candidates);
    return null;
  }
  return candidates[0];
}

function pickUuidsAndSubscribe(id, dev, epoch, found) {
  let serviceUuid = SERVICE_UUID;
  let notifyUuid = NOTIFY_CHAR_UUID;
  let writeUuid = WRITE_CHAR_UUID;

  if (!serviceUuid || !notifyUuid || !writeUuid) {
    const guess = guessUuids(found);
    if (!guess) {
      console.warn('[ble-bms] UUID 未配置且无法自动判定,请查看以上 service/characteristic 清单,回填文件顶部的 SERVICE_UUID/NOTIFY_CHAR_UUID/WRITE_CHAR_UUID', found);
      connectFail(id, new Error('UUID 未配置:请查看真机 service/characteristic 发现日志并回填'), epoch);
      return;
    }
    console.warn('[ble-bms] 已自动猜测 UUID(仅供参考,请核对真机后回填为固定常量):', guess);
    serviceUuid = serviceUuid || guess.serviceUuid;
    notifyUuid = notifyUuid || guess.notifyUuid;
    writeUuid = writeUuid || guess.writeUuid;
  }

  wx.notifyBLECharacteristicValueChange({
    deviceId: id,
    serviceId: serviceUuid,
    characteristicId: notifyUuid,
    state: true,
    success: () => {
      if (epoch !== connEpoch) return;
      activeService = serviceUuid;
      activeNotifyChar = notifyUuid;
      activeWriteChar = writeUuid;
      accumulator = new codec.FrameAccumulator();
      ensureNotifyListener();
      patchDeviceState(id, 'connected');
      store.setState({
        link: 'connected',
        device_name: dev.name,
        pend: { charge: null, discharge: null, balance: null },
      });
      toast('已连接 · ' + dev.name);
      startPolling(epoch);
    },
    fail: (err) => connectFail(id, err, epoch),
  });
}

function collectCharacteristics(id, dev, epoch, services) {
  if (services.length === 0) {
    connectFail(id, new Error('设备无可用 service'), epoch);
    return;
  }
  const found = [];
  let remaining = services.length;
  services.forEach((svc) => {
    wx.getBLEDeviceCharacteristics({
      deviceId: id,
      serviceId: svc.uuid,
      success: (res) => {
        res.characteristics.forEach((c) => {
          console.log('[ble-bms] characteristic:', svc.uuid, c.uuid, c.properties);
          found.push({ serviceUuid: svc.uuid, uuid: c.uuid, properties: c.properties });
        });
      },
      fail: (err) => console.warn('[ble-bms] getBLEDeviceCharacteristics 失败', svc.uuid, err),
      complete: () => {
        remaining--;
        if (remaining === 0) {
          if (epoch !== connEpoch) return;
          pickUuidsAndSubscribe(id, dev, epoch, found);
        }
      },
    });
  });
}

function discoverServices(id, dev, epoch) {
  wx.getBLEDeviceServices({
    deviceId: id,
    success: (res) => {
      if (epoch !== connEpoch) return;
      console.log('[ble-bms] services:', res.services.map((s) => s.uuid));
      collectCharacteristics(id, dev, epoch, res.services);
    },
    fail: (err) => connectFail(id, err, epoch),
  });
}

function connect(id) {
  const dev = store.state.devices.find((d) => d.id === id);
  if (!dev || dev.state === 'connecting' || dev.state === 'connected') return;
  const epoch = ++connEpoch;
  wx.stopBluetoothDevicesDiscovery({ fail: () => {} });
  patchDeviceState(id, 'connecting');
  store.setState({ scan: 'done', link: 'connecting' });
  wx.createBLEConnection({
    deviceId: id,
    success: () => {
      if (epoch !== connEpoch) {
        wx.closeBLEConnection({ deviceId: id, fail: () => {} });
        return;
      }
      deviceId = id;
      ensureConnectionStateListener();
      discoverServices(id, dev, epoch);
    },
    fail: (err) => connectFail(id, err, epoch),
  });
}

function disconnect() {
  const wasActive = store.state.link === 'connected' || store.state.link === 'connecting';
  wx.stopBluetoothDevicesDiscovery({ fail: () => {} });
  teardownConnection();
  store.setState({
    link: 'disconnected',
    devices: store.state.devices.map((d) => ({ ...d, state: 'idle' })),
    pend: { charge: null, discharge: null, balance: null },
  });
  if (wasActive) toast('已断开连接');
}

// ---- ④ notify 分片 → FrameAccumulator → codec → store ----

function ensureNotifyListener() {
  if (valueChangeRegistered) return;
  valueChangeRegistered = true;
  wx.onBLECharacteristicValueChange((res) => {
    if (res.deviceId !== deviceId || !accumulator) return; // deviceId 已在 teardown 置空则丢弃
    const bytes = ab2bytes(res.value);
    const frames = accumulator.feed(bytes);
    frames.forEach((frame) => handleFrame(frame));
  });
}

function applyFrame(reg, frame) {
  if (reg === codec.REG_BASIC) {
    const info = codec.parseBasicInfo(frame);
    if (!info) return;
    store.setState({
      total_voltage: info.total_voltage,
      current: info.current,
      soc: info.soc,
      temperature: info.temperature,
      protection_status: info.protection_status,
      mos_charge: info.mos_charge,
      mos_discharge: info.mos_discharge,
      mos_locked: info.mos_locked,
      balance: info.balance,
    });
  } else if (reg === codec.REG_CELLS) {
    const info = codec.parseCellVoltages(frame);
    if (!info) return;
    store.setState({ cell_voltages: info.cell_voltages, delta_mv: info.delta_mv });
  }
}

function handleFrame(frame) {
  const reg = frame[1];
  if (reg !== pendingRegister) return; // 不是当前在等的寄存器,防御性丢弃
  clearTimeout(timeoutTimer);
  timeoutTimer = null;
  pendingRegister = null;
  applyFrame(reg, frame);
  scheduleNextPoll(connEpoch);
}

// ---- ⑤ 只读轮询:一问一答 + 超时兜底 ----

function onRequestTimeout(register, epoch) {
  if (epoch !== connEpoch || pendingRegister !== register) return;
  console.warn('[ble-bms] 读请求超时', register);
  pendingRegister = null;
  scheduleNextPoll(epoch);
}

function sendRead(register, epoch) {
  if (epoch !== connEpoch) return;
  pendingRegister = register;
  wx.writeBLECharacteristicValue({
    deviceId,
    serviceId: activeService,
    characteristicId: activeWriteChar,
    value: bytes2ab(Array.from(codec.encodeRead(register))),
    success: () => {
      if (epoch !== connEpoch) return;
      timeoutTimer = setTimeout(() => onRequestTimeout(register, epoch), REQUEST_TIMEOUT_MS);
    },
    fail: (err) => {
      if (epoch !== connEpoch) return;
      console.warn('[ble-bms] 写入读请求失败', register, err);
      pendingRegister = null;
      scheduleNextPoll(epoch);
    },
  });
}

function scheduleNextPoll(epoch) {
  if (epoch !== connEpoch) return;
  pollRegister = pollRegister === codec.REG_BASIC ? codec.REG_CELLS : codec.REG_BASIC;
  pollTimer = setTimeout(() => sendRead(pollRegister, epoch), POLL_INTERVAL_MS);
}

function startPolling(epoch) {
  pollRegister = codec.REG_BASIC;
  sendRead(pollRegister, epoch);
}

// ---- 第二段占位:本段不下发任何写命令 ----

function toggleSwitch() {
  console.warn('第二段实现');
}
function runUnlock() {
  console.warn('第二段实现');
}
function cancelUnlock() {
  console.warn('第二段实现');
}

module.exports = { config, startScan, connect, disconnect, toggleSwitch, runUnlock, cancelUnlock };
