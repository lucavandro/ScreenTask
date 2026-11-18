const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function setup({ native = true, reject = false, webkit = false } = {}) {
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  const block = source.slice(source.indexOf('  // Fullscreen handling'), source.indexOf('  // Touch Zoom & Pan Handlers'));
  const listeners = {};
  const timers = new Map();
  const classes = () => {
    const values = new Set();
    return { add: s => values.add(s), remove: s => values.delete(s), contains: s => values.has(s),
      toggle: (s, on) => on ? values.add(s) : values.delete(s) };
  };
  const document = { body: { classList: classes() }, documentElement: { classList: classes() },
    addEventListener: (type, fn) => listeners[type] = fn };
  const element = () => ({ hidden: false, attributes: {}, handlers: {},
    addEventListener(type, fn) { this.handlers[type] = fn; },
    setAttribute(name, value) { this.attributes[name] = value; },
    focus() { document.activeElement = this; },
    contains(value) { return value === this || value?.owner === this; } });
  const button = element(), reveal = element(), header = element(), toolbar = element(), audio = element();
  audio.owner = toolbar;
  reveal.hidden = true;
  header.contains = value => value === button;
  document.getElementById = id => ({ viewerHeader: header, btnHeaderToggle: reveal })[id];
  document.querySelector = () => toolbar;
  const request = async () => {
    if (reject) throw new Error('Fullscreen unavailable');
    document[webkit ? 'webkitFullscreenElement' : 'fullscreenElement'] = document.documentElement;
    listeners[webkit ? 'webkitfullscreenchange' : 'fullscreenchange']();
  };
  const exit = async () => {
    document.fullscreenElement = document.webkitFullscreenElement = null;
    listeners[webkit ? 'webkitfullscreenchange' : 'fullscreenchange']();
  };
  if (native) {
    document.documentElement[webkit ? 'webkitRequestFullscreen' : 'requestFullscreen'] = request;
    document[webkit ? 'webkitExitFullscreen' : 'exitFullscreen'] = exit;
  }
  const modal = () => { const classList = classes(); classList.add('hidden'); return { classList }; };
  const context = { document, btnFullscreen: button,
    canvas: element(), rtcVideo: element(), audioDiagModal: modal(), pinModal: modal(),
    window: { scrollTo() {}, addEventListener() {}, matchMedia: () => ({ matches: false }) }, sendTelemetry() {},
    setTimeout: fn => { const key = {}; timers.set(key, fn); return key; }, clearTimeout: key => timers.delete(key) };
  vm.createContext(context);
  vm.runInContext(`${block}\nglobalThis.toggle = toggleFullscreen; globalThis.expand = setHeaderExpanded;`, context);
  return { context, document, button, reveal, header, toolbar, audio, listeners, timers,
    idle: () => { const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach(fn => fn()); } };
}

test('native fullscreen collapses the header, exposes its toggle, and restores focus on exit', async () => {
  const s = setup();
  s.button.focus();
  await s.context.toggle();
  assert.equal(s.header.hidden, true);
  assert.equal(s.toolbar.hidden, true);
  assert.equal(s.reveal.hidden, false);
  assert.equal(s.reveal.attributes['aria-expanded'], 'false');
  assert.equal(s.document.activeElement, s.reveal);
  s.reveal.handlers.click();
  assert.equal(s.header.hidden, false);
  assert.equal(s.toolbar.hidden, false);
  assert.equal(s.reveal.attributes['aria-expanded'], 'true');
  await s.context.toggle();
  assert.equal(s.header.hidden, false);
  assert.equal(s.reveal.hidden, true);
  assert.equal(s.button.attributes['aria-pressed'], 'false');
  assert.equal(s.document.activeElement, s.button);
});

test('fullscreen controls auto-hide together and stream clicks collapse them immediately', async () => {
  const s = setup();
  await s.context.toggle();
  s.context.expand(true);
  s.idle();
  assert.equal(s.header.hidden, true);
  assert.equal(s.toolbar.hidden, true);
  s.context.expand(true);
  s.context.canvas.handlers.click();
  assert.equal(s.header.hidden, true);
  assert.equal(s.toolbar.hidden, true);
});

test('focused playback controls and open dialogs are not hidden by the idle timer', async () => {
  const s = setup();
  await s.context.toggle();
  s.context.expand(true);
  s.audio.focus();
  s.idle();
  assert.equal(s.toolbar.hidden, false);
  s.document.activeElement = null;
  s.toolbar.handlers.focusout();
  s.idle();
  assert.equal(s.toolbar.hidden, true);
  s.context.expand(true);
  s.context.audioDiagModal.classList.remove('hidden');
  s.idle();
  assert.equal(s.header.hidden, false);
});

test('keyboard opening focuses the fullscreen action and closing never strands toolbar focus', async () => {
  const s = setup();
  await s.context.toggle();
  s.reveal.handlers.click({ detail: 0 });
  assert.equal(s.document.activeElement, s.button);
  s.audio.focus();
  s.context.expand(false);
  assert.equal(s.document.activeElement, s.reveal);
  assert.equal(s.toolbar.hidden, true);
});

for (const options of [{ native: false }, { reject: true }, { webkit: true }]) {
  test(`fullscreen remains usable with ${JSON.stringify(options)}`, async () => {
    const s = setup(options);
    await s.context.toggle();
    assert.equal(s.header.hidden, true);
    assert.equal(s.document.body.classList.contains('viewer-fullscreen'), true);
    s.context.expand(true);
    assert.equal(s.header.hidden, false);
    await s.context.toggle();
    assert.equal(s.document.body.classList.contains('viewer-fullscreen'), false);
    assert.equal(s.header.hidden, false);
    assert.equal(s.reveal.hidden, true);
  });
}

test('simulated fullscreen supports H and Escape without intercepting form input', async () => {
  const s = setup({ native: false });
  await s.context.toggle();
  let prevented = 0;
  const key = (value, editing = false) => s.listeners.keydown({ key: value,
    target: { closest: () => editing }, preventDefault() { prevented++; } });
  key('h', true);
  assert.equal(s.header.hidden, true);
  key('h');
  assert.equal(s.header.hidden, false);
  key('Escape');
  assert.equal(s.document.body.classList.contains('viewer-fullscreen'), false);
  assert.equal(prevented, 2);
});

test('external browser fullscreen exit restores the header and clears expanded state', async () => {
  const s = setup();
  await s.context.toggle();
  s.context.expand(true);
  s.document.fullscreenElement = null;
  s.listeners.fullscreenchange();
  assert.equal(s.document.body.classList.contains('header-expanded'), false);
  assert.equal(s.header.hidden, false);
  assert.equal(s.reveal.hidden, true);
});

function setupDialogs() {
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  const block = source.slice(source.indexOf('  // Dialog focus stays'), source.indexOf("  btnPlayChime?.addEventListener"));
  const listeners = {}, document = { addEventListener: (type, fn) => listeners[type] = fn };
  const element = () => ({ handlers: {}, disabled: false, getClientRects: () => [{}],
    addEventListener(type, fn) { this.handlers[type] = fn; }, focus() { document.activeElement = this; } });
  const modal = () => ({ hidden: true, controls: [],
    get classList() { return { contains: () => this.hidden, add: () => this.hidden = true, remove: () => this.hidden = false }; },
    contains(value) { return this.controls.includes(value); }, querySelectorAll() { return this.controls; },
    addEventListener(type, fn) { this[type] = fn; } });
  const audioDiagModal = modal(), pinModal = modal();
  const opener = element(), closer = element(), last = element();
  audioDiagModal.controls = [closer, last];
  pinModal.controls = [element(), element()];
  const context = { document, audioDiagModal, pinModal, btnAudioDiag: opener, btnCloseAudioDiag: closer,
    fullscreenUiActive: false, headerExpanded: false, btnHeaderToggle: element(),
    resetToolbarHideTimer() {}, initAudio() {}, updateAudioInfoUi() {}, updateAudioBtn() {} };
  vm.createContext(context);
  vm.runInContext(block, context);
  const key = (value, shiftKey = false) => {
    let prevented = false;
    listeners.keydown({ key: value, shiftKey, preventDefault() { prevented = true; }, stopImmediatePropagation() {} });
    return prevented;
  };
  return { context, document, audioDiagModal, pinModal, opener, closer, last, key };
}

test('audio dialog cycles keyboard focus and restores the opener on Escape', () => {
  const s = setupDialogs();
  s.opener.focus();
  s.opener.handlers.click();
  assert.equal(s.document.activeElement, s.closer);
  assert.equal(s.key('Tab', true), true);
  assert.equal(s.document.activeElement, s.last);
  assert.equal(s.key('Tab'), true);
  assert.equal(s.document.activeElement, s.closer);
  assert.equal(s.key('Escape'), true);
  assert.equal(s.audioDiagModal.hidden, true);
  assert.equal(s.document.activeElement, s.opener);
});

test('PIN focus is contained without allowing Escape to dismiss authentication', () => {
  const s = setupDialogs();
  s.pinModal.hidden = false;
  s.pinModal.controls[1].focus();
  assert.equal(s.key('Tab'), true);
  assert.equal(s.document.activeElement, s.pinModal.controls[0]);
  assert.equal(s.key('Escape'), false);
  assert.equal(s.pinModal.hidden, false);
});

test('closing a dialog returns to the reveal control if fullscreen controls have collapsed', () => {
  const s = setupDialogs();
  s.opener.focus();
  s.opener.handlers.click();
  s.context.fullscreenUiActive = true;
  s.closer.handlers.click();
  assert.equal(s.document.activeElement, s.context.btnHeaderToggle);
});
