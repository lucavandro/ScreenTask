const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function setup() {
  const source = fs.readFileSync(path.join(__dirname, '../../ScreenTask/WebServer/viewer.js'), 'utf8');
  const decoder = source.slice(source.indexOf('  // Bound decoding work:'), source.indexOf('  // Connect WebSockets (Video + Audio)'));
  const calls = [];
  const drawn = [];
  const closed = [];
  const context = {
    Blob, performance, canvas: {}, resolutionDisplay: {}, fpsDisplay: {},
    frameCount: 0, currentFps: 0, lastFpsTime: performance.now(),
    ctx: { drawImage: bitmap => drawn.push(bitmap.id) },
    createImageBitmap: blob => new Promise((resolve, reject) => calls.push({
      blob, reject,
      resolve: id => resolve({ id, width: 1920, height: 1080, close: () => closed.push(id) })
    }))
  };
  vm.createContext(context);
  vm.runInContext(`${decoder}
    globalThis.enqueue = bytes => {
      pendingVideoFrame = { bytes, generation: videoGeneration };
      return decodeVideoFrames();
    };
    globalThis.reset = resetVideoDecoder;`, context);
  return { ...context, calls, drawn, closed };
}

test('decodes one frame at a time and replaces obsolete pending frames', async () => {
  const s = setup();
  const running = s.enqueue(new Uint8Array([1]));
  s.enqueue(new Uint8Array([2]));
  s.enqueue(new Uint8Array([3]));
  assert.equal(s.calls.length, 1);
  s.calls[0].resolve(1);
  await new Promise(setImmediate);
  assert.equal(s.calls.length, 2);
  assert.deepEqual([...new Uint8Array(await s.calls[1].blob.arrayBuffer())], [3]);
  s.calls[1].resolve(3);
  await running;
  assert.deepEqual(s.drawn, [1, 3]);
  assert.deepEqual(s.closed, [1, 3]);
});

test('connection reset closes old bitmaps without displaying stale content', async () => {
  const s = setup();
  const running = s.enqueue(new Uint8Array([1]));
  s.enqueue(new Uint8Array([2]));
  s.reset();
  s.enqueue(new Uint8Array([3]));
  s.calls[0].resolve(1);
  await new Promise(setImmediate);
  s.calls[1].resolve(3);
  await running;
  assert.deepEqual(s.drawn, [3]);
  assert.deepEqual(s.closed, [1, 3]);
});

test('decode errors do not stall the next frame', async () => {
  const s = setup();
  const running = s.enqueue(new Uint8Array([1]));
  s.enqueue(new Uint8Array([2]));
  s.calls[0].reject(new Error('Invalid JPEG'));
  await new Promise(setImmediate);
  s.calls[1].resolve(2);
  await running;
  assert.deepEqual(s.drawn, [2]);
  assert.deepEqual(s.closed, [2]);
});
