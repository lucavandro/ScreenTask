const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function setup({ failConstructor = false, failNegotiation = false, play } = {}) {
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  const rtc = source.slice(source.indexOf('  let rtcPeer = null;'), source.indexOf('  // Connect WebSockets (Video + Audio)'));
  const messages = [], peers = [], timers = new Map();
  const socket = { readyState: 1, send: text => messages.push(JSON.parse(text)) };
  const classes = () => ({ add() {}, remove() {} });
  class Peer {
    constructor(config) {
      if (failConstructor) throw new Error('Unavailable');
      this.config = config; this.iceGatheringState = 'complete'; peers.push(this);
    }
    async setRemoteDescription(value) { if (failNegotiation) throw new Error('Invalid SDP'); this.remote = value; }
    async createAnswer() { return { type: 'answer', sdp: 'test answer' }; }
    async setLocalDescription(value) { this.localDescription = value; }
    close() { this.closed = true; }
  }
  const context = {
    RTCPeerConnection: Peer, MediaStream: class {}, WebSocket: { OPEN: 1 }, wsVideo: socket,
    canvas: { classList: classes() }, rtcVideo: { classList: classes(), play: play || (async () => {}), videoWidth: 1280, videoHeight: 720, requestVideoFrameCallback() {} },
    transportDisplay: {}, resolutionDisplay: {}, fpsDisplay: {}, currentFps: 0,
    performance, showToast() {}, setTimeout: fn => { const key = {}; timers.set(key, fn); return key; },
    clearTimeout: key => timers.delete(key), setInterval: () => ({}), clearInterval() {}
  };
  vm.createContext(context);
  vm.runInContext(`${rtc}\nglobalThis.start = startWebRtc; globalThis.reset = resetWebRtc;`, context);
  return { context, socket, messages, peers, timers };
}

test('negotiates receive-only video without public ICE servers and disposes the peer on reset', async () => {
  const s = setup();
  await s.context.start(s.socket, 'test offer');
  assert.deepEqual(s.messages, [{ type: 'webrtc-answer', sdp: 'test answer' }]);
  assert.equal(s.peers[0].config.iceServers.length, 0);
  assert.equal(s.peers[0].remote.type, 'offer');
  s.context.reset();
  assert.equal(s.peers[0].closed, true);
  assert.equal(s.timers.size, 0);
});

for (const failure of [{ failConstructor: true }, { failNegotiation: true }]) {
  test(`returns to JPEG on ${Object.keys(failure)[0]}`, async () => {
    const s = setup(failure);
    await s.context.start(s.socket, 'offer');
    assert.deepEqual(s.messages, [{ type: 'webrtc-fallback' }]);
    assert.equal(s.context.transportDisplay.textContent, 'JPEG');
  });
}

test('missing video times out to JPEG', async () => {
  const s = setup();
  await s.context.start(s.socket, 'offer');
  [...s.timers.values()][0]();
  assert.equal(s.messages.at(-1).type, 'webrtc-fallback');
  assert.equal(s.peers[0].closed, true);
});

test('a stale playback failure cannot close a replacement peer', async () => {
  let rejectPlay;
  const s = setup({ play: () => new Promise((_, reject) => { rejectPlay = reject; }) });
  await s.context.start(s.socket, 'offer');
  const playback = s.peers[0].ontrack({ streams: [{}] });
  await s.context.start(s.socket, 'new offer');
  rejectPlay(new Error('Old playback rejected'));
  await playback;
  assert.equal(s.peers[1].closed, undefined);
  assert.equal(s.messages.filter(m => m.type === 'webrtc-fallback').length, 0);
  s.context.reset();
});

test('a stale socket cannot negotiate a new peer', async () => {
  const s = setup();
  await s.context.start({ readyState: 1 }, 'offer');
  assert.equal(s.peers.length, 0);
  assert.equal(s.messages.length, 0);
});

test('PIN challenge tears down WebRTC even when the audio socket closes first', async () => {
  const s = setup();
  await s.context.start(s.socket, 'offer');
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  Object.assign(s.context, {
    resetVideoDecoder() {}, stopTelemetryHeartbeat() {}, isConnected: true,
    wsAudio: null, overlay: { classList: { add() {} } }, pinModal: { classList: { remove() {} } },
    pinError: { style: {} }, pinInput: { focus() {}, setAttribute() {} },
    audioDiagModal: { classList: { add() {} } }
  });
  s.socket.close = () => {};
  vm.runInContext(source.slice(source.indexOf('  function showPinModal()'), source.indexOf('  async function checkServerStatus()')), s.context);
  s.context.showPinModal();
  assert.equal(s.peers[0].closed, true);
  assert.equal(s.context.rtcVideo.srcObject, null);
  assert.equal(s.context.wsVideo, null);
  assert.equal(s.context.isConnected, false);
});

test('wheel and touch controls are attached to both video surfaces', () => {
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  const surface = () => ({ style: {}, handlers: {}, addEventListener(type, fn) { this.handlers[type] = fn; } });
  const context = { canvas: surface(), rtcVideo: surface(), window: { addEventListener() {} },
    scale: 1, panX: 0, panY: 0, zoomHud: { style: {} }, btnResetZoom: surface(),
    debouncedSendTelemetry() {}, performance, showToast() {} };
  vm.createContext(context);
  vm.runInContext(source.slice(source.indexOf('  function onVideoSurface('), source.indexOf('  function getDistance(')), context);
  for (const type of ['wheel', 'mousedown', 'touchstart', 'touchmove', 'touchend'])
    assert.equal(typeof context.rtcVideo.handlers[type], 'function');
  context.rtcVideo.handlers.wheel({ preventDefault() {}, deltaY: -1 });
  assert.equal(context.scale, 1.15);
  assert.equal(context.rtcVideo.style.transform, context.canvas.style.transform);
});
