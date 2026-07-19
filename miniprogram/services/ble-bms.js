// 真机 BLE 服务:接口形状与 mock-bms.js 一致(scan/connect/disconnect/toggle/unlock)。
// 阶段三·第二步(第二段):半双工命令队列 + 0xE1/0xE2 写命令 + 三步解锁链条。
// 核心纪律:回读为准(显示值只来自 0x03 回读,0xE2 因协议无回读字段例外走 ack 受理)、
// 严格串行半双工、断线清场(队列/定时器/在途链条全作废,不许假落定)。
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
const POLL_INTERVAL_MS = 1500; // 相邻两轮轮询读之间的间隔
const COMMAND_TIMEOUT_MS = 800; // 单条在途命令的应答超时,与 C# 命令泵同量级

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
let pollRegister = codec.REG_BASIC;

// ---- 半双工命令队列(与 C# 切片2 命令泵同构) ----
// 轮询读(0x03/0x04)与用户写(0xE1/0xE2)进同一条串行化队列,一次只在途一条;
// 响应按在途命令期望的回显寄存器配对推进,不靠发送顺序猜;超时放弃当前、继续下一条。
let queue = []; // 待发命令:{ epoch, priority, label, bytes, expectReg, onFrame, onTimeout? }
let inflight = null; // 在途命令,空闲为 null
let cmdTimer = null; // 在途命令的应答超时定时器

// 充/放写命令 ack 已受理、等待 0x03 回读落定的目标值(null=无在途);pend 的清除由回读驱动
let mosAwait = { charge: null, discharge: null };
let unlockRunId = 0; // 解锁链条运行号,cancel/断线自增令旧回调失效
let adjudicating = false; // 三步 ack 收齐后,等 0x03 回读裁决中

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
      const desc = dev.deviceId + (typeof dev.RSSI === 'number' ? ' · ' + dev.RSSI + ' dBm' : '');
      store.setState({ devices: [...list, { id: dev.deviceId, name: dev.name, desc, state: 'idle' }] });
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
  clearTimeout(cmdTimer);
  pollTimer = null;
  cmdTimer = null;
  inflight = null;
  queue = []; // 断线必须清空队列:残留命令在下一次连接上发出去就是串台
}

function teardownConnection() {
  const id = deviceId;
  connEpoch++; // 令所有捕获了旧 epoch 的 setTimeout 回调失效
  stopPolling();
  // 作废在途写命令与解锁链条:断线后绝不允许任何回调把界面写成"落定成功"
  mosAwait = { charge: null, discharge: null };
  unlockRunId++;
  adjudicating = false;
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
  // 设备列表状态必须一并复位:残留 'connected' 会让设备页挂"已连接"假象,
  // 且 connect() 的防重守卫会拒绝重连,掉线后就永远连不上了。
  store.setState({
    link: 'dropped',
    devices: store.state.devices.map((d) => ({ ...d, state: 'idle' })),
    pend: { charge: null, discharge: null, balance: null },
    unlock_stage: 0,
  });
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
    unlock_stage: 0,
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
      remain_capacity: info.remain_capacity,
      design_capacity: info.design_capacity,
      cycle_count: info.cycle_count,
      temperature: info.temperature,
      protection_status: info.protection_status,
      mos_charge: info.mos_charge,
      mos_discharge: info.mos_discharge,
      mos_locked: info.mos_locked,
      // 注意:不把 info.balance 写回 store.balance——偏移 12/14 是逐串均衡"动作"位图,
      // 不是均衡使能开关的回读(协议无该字段);store.balance 表示开关状态,由 0xE2 ack 落定。
      // 单体未到均衡开启电压(docs:4.10 V)时位图恒 0,若拿它当开关回读,开关永远亮不起来。
      balance_bits: info.balance_bits,
    });
    settleMosPend(info); // 充/放开关"回读为准"落定
    adjudicateUnlock(info); // 解锁链条的回读裁决
  } else if (reg === codec.REG_CELLS) {
    const info = codec.parseCellVoltages(frame);
    if (!info) return;
    store.setState({ cell_voltages: info.cell_voltages, delta_mv: info.delta_mv });
  }
}

function handleFrame(frame) {
  const reg = frame[1];
  if (!inflight || reg !== inflight.expectReg) return; // 与在途命令对不上号,防御性丢弃
  const cmd = finishInflight();
  cmd.onFrame(frame);
  pumpQueue();
}

// ---- ⑤ 命令队列泵:严格串行,一次只在途一条 ----

function enqueueCommand(cmd) {
  if (cmd.priority) {
    // 用户写命令插到轮询读之前(否则点了开关要等一轮才发),但排在已有优先命令之后,
    // 写与写之间保持先后;插队不打断在途命令,半双工仍严格串行。
    let i = 0;
    while (i < queue.length && queue[i].priority) i++;
    queue.splice(i, 0, cmd);
  } else {
    queue.push(cmd);
  }
  pumpQueue();
}

function finishInflight() {
  clearTimeout(cmdTimer);
  cmdTimer = null;
  const cmd = inflight;
  inflight = null;
  return cmd;
}

function pumpQueue() {
  if (inflight) return;
  while (queue.length > 0 && queue[0].epoch !== connEpoch) queue.shift(); // 甩掉旧连接残留
  const cmd = queue.shift();
  if (!cmd) return;
  inflight = cmd;
  wx.writeBLECharacteristicValue({
    deviceId,
    serviceId: activeService,
    characteristicId: activeWriteChar,
    value: bytes2ab(cmd.bytes),
    success: () => {
      // 应答可能抢在 success 回调之前到达并结掉本条,此时不再起超时定时器
      if (cmd.epoch !== connEpoch || inflight !== cmd) return;
      cmdTimer = setTimeout(() => {
        if (inflight !== cmd) return;
        finishInflight();
        console.warn('[ble-bms] 命令超时,放弃并继续下一条:', cmd.label);
        if (cmd.onTimeout) cmd.onTimeout();
        pumpQueue();
      }, COMMAND_TIMEOUT_MS);
    },
    fail: (err) => {
      if (cmd.epoch !== connEpoch || inflight !== cmd) return;
      finishInflight();
      console.warn('[ble-bms] 命令写入失败:', cmd.label, err);
      if (cmd.onTimeout) cmd.onTimeout();
      pumpQueue();
    },
  });
}

// ---- ⑥ 轮询读:0x03/0x04 交替入队,完成(应答或超时)后间隔 POLL_INTERVAL_MS 再入下一条 ----

function enqueuePollRead(epoch) {
  if (epoch !== connEpoch) return;
  pollRegister = pollRegister === codec.REG_BASIC ? codec.REG_CELLS : codec.REG_BASIC;
  const register = pollRegister;
  enqueueCommand({
    epoch,
    label: '轮询读 0x' + register.toString(16).toUpperCase(),
    bytes: Array.from(codec.encodeRead(register)),
    expectReg: register,
    onFrame: (frame) => {
      applyFrame(register, frame);
      scheduleNextPoll(epoch);
    },
    onTimeout: () => scheduleNextPoll(epoch),
  });
}

function scheduleNextPoll(epoch) {
  if (epoch !== connEpoch) return;
  pollTimer = setTimeout(() => enqueuePollRead(epoch), POLL_INTERVAL_MS);
}

function startPolling(epoch) {
  pollRegister = codec.REG_CELLS; // 入队前会先翻转,故首轮读 0x03
  enqueuePollRead(epoch);
}

// ---- ⑦ 写命令:0xE1 充/放 MOS(回读为准)+ 0xE2 均衡(ack 受理为准) ----

// 写落定/解锁裁决都要等 0x03 回读;轮询下一次轮到 0x03 最迟隔一轮 0x04,
// 这里插一条即时 0x03 优先读加速确认。它不参与轮询节奏(不排下一轮)。
function enqueueBasicRefresh(epoch) {
  enqueueCommand({
    epoch,
    priority: true,
    label: '回读确认 0x03',
    bytes: Array.from(codec.encodeRead(codec.REG_BASIC)),
    expectReg: codec.REG_BASIC,
    onFrame: (frame) => applyFrame(codec.REG_BASIC, frame),
  });
}

function bouncePend(key, reason) {
  store.setState({ pend: { ...store.state.pend, [key]: null } });
  toast(reason + ',开关已弹回');
}

// 0x03 回读一到就落定充/放 pend:显示值只来自回读的 FET 状态字节。
// 写被静默拒绝(典型:软件锁定,ack 照样 OK)时回读还是旧值,开关自然弹回——招牌语义。
function settleMosPend(info) {
  ['charge', 'discharge'].forEach((key) => {
    const target = mosAwait[key];
    if (target === null) return;
    mosAwait[key] = null;
    store.setState({ pend: { ...store.state.pend, [key]: null } });
    const actual = key === 'charge' ? info.mos_charge : info.mos_discharge;
    const name = key === 'charge' ? '充电 MOS' : '放电 MOS';
    toast(actual === target
      ? '回读确认:' + name + (actual ? '已开启' : '已关闭')
      : name + '写入未生效(设备拒绝),开关已弹回');
  });
}

// 返回值与 mock 同契约:'ok' 已受理 | 'offline' 未连接。
// 与 mock 的一处刻意差别:锁定态不早退返回 'locked'——写照发,让设备静默拒绝、回读弹回,
// 这正是要演示的"回读为准"语义(与 C# 切片2"锁定态开关保留可拨"一条线);解锁入口在锁定横幅。
function toggleSwitch(key) {
  const s = store.state;
  if (s.link !== 'connected') {
    toast('未连接设备,无法下发命令');
    return 'offline';
  }
  if (s.pend[key] !== null) return 'ok'; // 已有在途命令,不重复入队
  const epoch = connEpoch;

  if (key === 'balance') {
    // 0xE2:协议无均衡使能回读字段(0x03 偏移 12/14 是逐串动作位图,不是使能状态),
    // 只能以写 ack 受理为准更新显示。仍非乐观更新:ack 被拒/超时弹回原值,不是偷懒。
    const target = !s.balance;
    store.setState({ pend: { ...s.pend, balance: target } });
    enqueueCommand({
      epoch,
      priority: true,
      label: '写 0xE2 均衡' + (target ? '开' : '关'),
      bytes: Array.from(codec.encodeBalanceControl(target)),
      expectReg: codec.REG_BAL,
      onFrame: (frame) => {
        if (!codec.parseWriteAck(frame, codec.REG_BAL).accepted) {
          bouncePend('balance', '设备拒绝写入');
          return;
        }
        store.setState({ pend: { ...store.state.pend, balance: null }, balance: target });
        toast('均衡' + (target ? '已开启' : '已关闭') + '(以写入受理为准)');
      },
      onTimeout: () => bouncePend('balance', '命令超时'),
    });
    return 'ok';
  }

  // charge / discharge → 0xE1。一个控制字同时管两路:另一路取在途 pend 目标(若有)
  // 否则取回读真值,避免后一条写把前一条尚未落定的写打翻。
  const target = !s['mos_' + key];
  const chargeOn = key === 'charge' ? target : (s.pend.charge !== null ? s.pend.charge : s.mos_charge);
  const dischargeOn = key === 'discharge' ? target : (s.pend.discharge !== null ? s.pend.discharge : s.mos_discharge);
  store.setState({ pend: { ...s.pend, [key]: target } });
  enqueueCommand({
    epoch,
    priority: true,
    label: '写 0xE1 ' + key + (target ? '开' : '关'),
    bytes: Array.from(codec.encodeMosControl(codec.mosControlWord(chargeOn, dischargeOn))),
    expectReg: codec.REG_MOS,
    onFrame: (frame) => {
      if (!codec.parseWriteAck(frame, codec.REG_MOS).accepted) {
        bouncePend(key, '设备拒绝写入');
        return;
      }
      // ack 受理 ≠ 落定:不改本地真值,等 0x03 回读 FET 状态字节
      mosAwait[key] = target;
      enqueueBasicRefresh(epoch);
    },
    onTimeout: () => bouncePend(key, '命令超时'),
  });
  return 'ok';
}

// ---- ⑧ 三步解锁链条(0x0003 全关 → 0x0001 开放关充 → 0x0000 全开,与 C# 切片3 一条线) ----
// 按序走队列,收 ack 再发下一条;三条 ack 收齐 ≠ 成功(错序写入被设备静默忽略,ack 照样 OK),
// 必须等 0x03 回读确认 bit12=0 且两路 FET 均开才判成功。
// 幂等可重入:首步恒为"全关",从任何残留中间态(含上次解锁走一半断线)重跑都安全。

function failUnlock(runId, reason) {
  if (runId !== unlockRunId) return;
  adjudicating = false;
  console.warn('[ble-bms] 解锁失败:', reason);
  store.setState({ unlock_stage: 9 }); // failed,按钮恢复可再点
}

function runUnlock() {
  if (store.state.link !== 'connected') {
    toast('未连接设备,无法解锁');
    return;
  }
  const runId = ++unlockRunId; // 重跑即作废上一条链
  const epoch = connEpoch;
  adjudicating = false;
  const sendStep = (i) => {
    if (runId !== unlockRunId || epoch !== connEpoch) return;
    store.setState({ unlock_stage: 2 * i + 1 }); // step(i+1)_send
    enqueueCommand({
      epoch,
      priority: true,
      label: '解锁第 ' + (i + 1) + '/3 步',
      bytes: Array.from(codec.encodeMosControl(codec.UNLOCK_SEQUENCE[i])),
      expectReg: codec.REG_MOS,
      onFrame: (frame) => {
        if (runId !== unlockRunId) return;
        if (!codec.parseWriteAck(frame, codec.REG_MOS).accepted) {
          failUnlock(runId, '第 ' + (i + 1) + ' 步被设备拒绝');
          return;
        }
        store.setState({ unlock_stage: 2 * i + 2 }); // step(i+1)_ack
        if (i < 2) {
          sendStep(i + 1);
        } else {
          store.setState({ unlock_stage: 7 }); // adjudicating:等回读裁决
          adjudicating = true;
          enqueueBasicRefresh(epoch);
        }
      },
      onTimeout: () => failUnlock(runId, '第 ' + (i + 1) + ' 步超时'),
    });
  };
  sendStep(0);
}

// 裁决用回读走常规 applyFrame 路径:即时优先读若超时,下一轮轮询 0x03 照样能裁决
function adjudicateUnlock(info) {
  if (!adjudicating) return;
  adjudicating = false;
  if (!info.mos_locked && info.mos_charge && info.mos_discharge) {
    store.setState({ unlock_stage: 8 }); // success
    toast('解锁成功 · 双 MOS 恢复导通');
  } else {
    store.setState({ unlock_stage: 9 }); // failed:bit12 仍为 1 或 FET 未全开
  }
}

function cancelUnlock() {
  unlockRunId++;
  adjudicating = false;
  store.setState({ unlock_stage: 0 });
}

module.exports = { config, startScan, connect, disconnect, toggleSwitch, runUnlock, cancelUnlock };
