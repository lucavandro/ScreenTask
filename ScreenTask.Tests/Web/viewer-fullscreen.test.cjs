const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function setup({ native = true, reject = false, webkit = false } = {}) {
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  const block = source.slice(source.indexOf('  // Fullscreen handling'), source.indexOf('  // Auto-Hiding Floating Toolbar'));
  const listeners = {};
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
    focus() { document.activeElement = this; } });
  const button = element(), reveal = element(), header = element();
  reveal.hidden = true;
  header.contains = value => value === button;
  document.getElementById = id => ({ viewerHeader: header, btnHeaderToggle: reveal })[id];
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
  const context = { document, btnFullscreen: button, window: { scrollTo() {} }, sendTelemetry() {} };
  vm.createContext(context);
  vm.runInContext(`${block}\nglobalThis.toggle = toggleFullscreen; globalThis.expand = setHeaderExpanded;`, context);
  return { context, document, button, reveal, header, listeners };
}

test('native fullscreen collapses the header, exposes its toggle, and restores focus on exit', async () => {
  const s = setup();
  s.button.focus();
  await s.context.toggle();
  assert.equal(s.header.hidden, true);
  assert.equal(s.reveal.hidden, false);
  assert.equal(s.reveal.attributes['aria-expanded'], 'false');
  assert.equal(s.document.activeElement, s.reveal);
  s.reveal.handlers.click();
  assert.equal(s.header.hidden, false);
  assert.equal(s.reveal.attributes['aria-expanded'], 'true');
  await s.context.toggle();
  assert.equal(s.header.hidden, false);
  assert.equal(s.reveal.hidden, true);
  assert.equal(s.button.attributes['aria-pressed'], 'false');
  assert.equal(s.document.activeElement, s.button);
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
