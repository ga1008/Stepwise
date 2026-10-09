/* Run: node tests/annotation-engine-test.cjs [font-path] [output-directory]
 * Uses a real raster canvas: NODE_PATH or the bundled Codex runtime supplies @napi-rs/canvas.
 */
'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
let raster;
try { raster = require('@napi-rs/canvas'); }
catch { raster = require(path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/@napi-rs/canvas')); }
const { createCanvas, GlobalFonts, loadImage } = raster;
const Engine = require('../src/Stepwise/Web/annotation-engine.js');
const fontPath = process.argv[2];
const fontLoaded = Boolean(fontPath && fs.existsSync(fontPath) && GlobalFonts.registerFromPath(fontPath, 'Stepwise Sans'));
const output = path.resolve(process.argv[3] || path.join(__dirname, '../verification/annotation-engine'));
fs.mkdirSync(output, { recursive: true });
const cases = [];
const make = (width = 640, height = 480, image = null) => new Engine({ width, height, image, canvasFactory: createCanvas });
const draw = (engine, type, a, b, style) => { engine.begin(type, a, style); return engine.end(b); };
const px = (canvas, x, y) => [...canvas.getContext('2d').getImageData(x, y, 1, 1).data];
const almost = (actual, expected, epsilon = 0.001) => assert.ok(Math.abs(actual - expected) < epsilon, `${actual} ≈ ${expected}`);
async function test(name, operation) {
  try { await operation(); cases.push({ name, passed: true }); }
  catch (error) { cases.push({ name, passed: false, error: error.stack }); }
}
(async () => {
  await test('Gesture snapshots color, line width and font controls; default is 5 original pixels', () => {
    const engine = make(), style = { color: '#00aa33', lineWidth: 9, fontSize: 36 };
    engine.begin('rectangle', { x: 10, y: 10 }, style);
    style.color = '#000000'; style.lineWidth = 88;
    const first = engine.end({ x: 100, y: 80 });
    assert.equal(first.style.color, '#00aa33'); assert.equal(first.style.lineWidth, 9);
    const second = draw(engine, 'ellipse', { x: 120, y: 10 }, { x: 200, y: 80 });
    assert.equal(second.style.lineWidth, 5);
  });
  await test('Reverse-direction rectangle keeps source bounds and outline-only hit testing', () => {
    const engine = make(), layer = draw(engine, 'rectangle', { x: 200, y: 160 }, { x: 40, y: 30 });
    assert.deepEqual(engine.boundsOfLayer(layer), { x: 37.5, y: 27.5, width: 165, height: 135 });
    assert.equal(engine.hitTest({ x: 40, y: 80 }, 0)?.id, layer.id);
    assert.equal(engine.hitTest({ x: 120, y: 100 }, 0), null);
    assert.equal(engine.hitTest({ x: 36, y: 80 }, 0), null);
    assert.equal(engine.hitTest({ x: 36, y: 80 }, 2)?.id, layer.id);
  });
  await test('Freehand keeps one transparent layer per gesture; dot clicks remain editable', () => {
    const engine = make();
    engine.begin('pen', { x: 10, y: 10 });
    for (let index = 1; index <= 100; index++) engine.update({ x: index + 10, y: index + 10 });
    const first = engine.end();
    assert.equal(engine.layers.length, 1); assert.equal(first.points.length, 101);
    assert.equal(engine.hitTest({ x: 50, y: 50 }, 0)?.id, first.id);
    assert.equal(engine.hitTest({ x: 80, y: 30 }, 0), null);
    engine.begin('pen', { x: 160, y: 160 }); const dot = engine.end();
    assert.equal(engine.layers.length, 2); assert.equal(engine.hitTest({ x: 160, y: 160 }, 0)?.id, dot.id);
    const canvas = createCanvas(640, 480); engine.render(canvas, { selection: false }); assert.ok(px(canvas, 160, 160)[3] > 200);
  });
  await test('Ellipse follows curved paint instead of its rectangular bounding box', () => {
    const engine = make(), layer = draw(engine, 'ellipse', { x: 30, y: 30 }, { x: 230, y: 130 });
    assert.equal(engine.hitTest({ x: 130, y: 30 }, 0)?.id, layer.id);
    assert.equal(engine.hitTest({ x: 230, y: 80 }, 0)?.id, layer.id);
    assert.equal(engine.hitTest({ x: 130, y: 80 }, 0), null);
    assert.equal(engine.hitTest({ x: 30, y: 30 }, 0), null);
  });
  await test('Transparent rectangle and ellipse interiors never block a lower painted layer', () => {
    const engine = make();
    const bottom = draw(engine, 'pen', { x: 25, y: 90 }, { x: 250, y: 90 }, { color: '#ff0000' });
    const rectangle = draw(engine, 'rectangle', { x: 40, y: 40 }, { x: 230, y: 160 });
    const ellipse = draw(engine, 'ellipse', { x: 70, y: 50 }, { x: 200, y: 130 });
    engine.select(rectangle.id);
    assert.equal(engine.hitTest({ x: 130, y: 90 }, 0)?.id, bottom.id);
    assert.equal(engine.hitTest({ x: 40, y: 90 }, 0)?.id, rectangle.id);
    assert.equal(engine.hitTest({ x: 70, y: 90 }, 0)?.id, ellipse.id);
  });
  await test('Eight arrow directions produce a solid arrow aimed exactly at mouse release', () => {
    const directions = [[1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1]];
    for (const [dx, dy] of directions) {
      const engine = make(), start = { x: 300, y: 240 }, end = { x: 300 + dx * 130, y: 240 + dy * 130 };
      const layer = draw(engine, 'arrow', start, end);
      const polygon = engine.arrowPolygon(layer);
      assert.deepEqual(polygon[3], end); assert.equal(polygon.length, 7);
      const middle = { x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 };
      assert.equal(engine.hitTest(middle, 0)?.id, layer.id);
      const canvas = createCanvas(640, 480); engine.render(canvas, { selection: false });
      assert.ok(px(canvas, Math.floor(middle.x), Math.floor(middle.y))[3] > 200);
      const ux = (end.x - start.x) / Math.hypot(end.x - start.x, end.y - start.y);
      const uy = (end.y - start.y) / Math.hypot(end.x - start.x, end.y - start.y);
      for (const p of polygon) assert.ok((p.x - start.x) * ux + (p.y - start.y) * uy >= -0.001);
    }
  });
  await test('Arrow size grows smoothly with length, saturates for large desktops, ignores pen width', () => {
    const engine = make(), widths = [];
    for (const length of [1, 5, 20, 100, 400, 1000, 10000]) {
      const layer = draw(engine, 'arrow', { x: 0, y: 200 }, { x: length, y: 200 }, { lineWidth: 1 });
      const polygon = engine.arrowPolygon(layer), width = engine.boundsOfLayer(layer).height;
      widths.push(width);
      assert.ok(polygon.every(p => Number.isFinite(p.x) && Number.isFinite(p.y)));
      assert.ok(width <= 56.001); assert.ok(width <= length * 0.44 + 0.001);
      const largePen = structuredClone(layer); largePen.style.lineWidth = 100;
      assert.deepEqual(engine.arrowPolygon(largePen), polygon);
    }
    for (let index = 1; index < widths.length; index++) assert.ok(widths[index] > widths[index - 1]);
  });
  await test('Transparent arrow bounding-box corners permit lower strokes to be selected', () => {
    const engine = make();
    const below = draw(engine, 'pen', { x: 50, y: 190 }, { x: 350, y: 190 });
    const top = draw(engine, 'arrow', { x: 70, y: 200 }, { x: 350, y: 200 });
    assert.ok(engine.boundsOfLayer(top).y < 190);
    assert.equal(engine.hitTest({ x: 100, y: 190 }, 0)?.id, below.id);
    assert.equal(engine.hitTest({ x: 340, y: 200 }, 0)?.id, top.id);
  });
  await test('Layer movement retains stacking and commits exactly one undo action per drag', () => {
    const engine = make();
    const layer = draw(engine, 'rectangle', { x: 10, y: 10 }, { x: 100, y: 100 });
    const before = engine.serialize();
    engine.beginMove(layer.id, { x: 10, y: 10 });
    for (let i = 1; i <= 30; i++) engine.updateMove({ x: 10 + i, y: 10 + i * 2 });
    engine.endMove();
    assert.deepEqual(engine.layers[0].start, { x: 40, y: 70 });
    engine.undo(); assert.deepEqual(engine.serialize(), before);
    engine.redo(); assert.deepEqual(engine.layers[0].start, { x: 40, y: 70 });
    engine.undo(); engine.undo(); assert.equal(engine.layers.length, 0);
  });
  await test('Interrupted drawing and interrupted movement roll back without a history entry', () => {
    const engine = make(), layer = draw(engine, 'pen', { x: 10, y: 10 }, { x: 100, y: 100 });
    const baseline = engine.serialize();
    engine.begin('rectangle', { x: 20, y: 20 }); engine.update({ x: 200, y: 200 }); engine.cancelGesture();
    assert.deepEqual(engine.serialize(), baseline);
    engine.beginMove(layer.id, { x: 10, y: 10 }); engine.updateMove({ x: 80, y: 40 }); engine.cancelGesture();
    assert.deepEqual(engine.serialize(), baseline);
    engine.undo(); assert.equal(engine.layers.length, 0);
  });
  await test('Selection does not alter pixels/history; deletion is undoable; new changes discard redo', () => {
    const engine = make(), layer = draw(engine, 'ellipse', { x: 10, y: 10 }, { x: 100, y: 100 });
    engine.select(null); engine.select(layer.id); engine.select(null); engine.select(layer.id);
    assert.equal(engine.deleteSelected(), true); assert.equal(engine.layers.length, 0);
    engine.undo(); assert.equal(engine.layers.length, 1); assert.equal(engine.canRedo, true);
    draw(engine, 'pen', { x: 2, y: 2 }, { x: 20, y: 20 }); assert.equal(engine.canRedo, false);
    engine.undo(); engine.undo(); assert.equal(engine.layers.length, 0);
  });
  await test('Text preserves Chinese, English, full/half-width punctuation and multiline content', () => {
    const engine = make();
    const text = '步骤 1：打开设置。Hello, Win11!\r\n中文（）【】“”；半角()[]";！？';
    const style = { color: '#2255cc', fontSize: 28 };
    const layer = engine.addText({ x: 20, y: 20 }, text, style);
    style.color = '#ff0000'; style.fontSize = 80;
    assert.equal(layer.text, text.replace(/\r\n/g, '\n')); assert.equal(layer.style.color, '#2255cc'); assert.equal(layer.style.fontSize, 28);
    assert.ok(engine.boundsOfLayer(layer).height > 28);
    const canvas = createCanvas(640, 480); engine.render(canvas, { selection: false });
    fs.writeFileSync(path.join(output, 'chinese-text.png'), canvas.toBuffer('image/png'));
    const pixels = canvas.getContext('2d').getImageData(0, 0, 640, 150).data;
    assert.ok(pixels.filter((value, index) => index % 4 === 3 && value > 100).length > 1000);
    assert.equal(engine.serialize().layers[0].text, layer.text);
  });
  await test('Text hit testing uses glyph alpha: holes, whitespace, and interline gaps pass through', () => {
    const engine = make();
    const text = engine.addText({ x: 40, y: 40 }, 'OO    OO\nOO', { fontSize: 50 });
    const canvas = createCanvas(640, 480); engine.render(canvas, { selection: false });
    const bounds = engine.boundsOfLayer(text);
    const ctx = canvas.getContext('2d');
    let ink = null, blank = null;
    for (let y = Math.ceil(bounds.y) + 4; y < bounds.y + bounds.height - 4 && (!ink || !blank); y++) for (let x = Math.ceil(bounds.x) + 4; x < bounds.x + bounds.width - 4; x++) {
      const alpha = ctx.getImageData(x - 1, y - 1, 3, 3).data.filter((value, index) => index % 4 === 3);
      if (!ink && alpha.every(value => value > 220)) ink = { x: x + 0.5, y: y + 0.5 };
      if (!blank && alpha.every(value => value === 0)) blank = { x: x + 0.5, y: y + 0.5 };
    }
    assert.ok(ink); assert.ok(blank);
    assert.equal(engine.hitTest(ink, 0)?.id, text.id);
    assert.equal(engine.hitTest(blank, 0), null);
    const below = draw(engine, 'pen', { x: blank.x - 10, y: blank.y }, { x: blank.x + 10, y: blank.y }, { lineWidth: 2 });
    const movedText = engine.layers.shift(); engine.layers.push(movedText); // Set fixture z-order without changing its painted geometry.
    assert.equal(engine.hitTest(blank, 0)?.id, below.id);
    assert.equal(engine.hitTest(ink, 0)?.id, text.id);
  });
  await test('Dragging text translates both glyph hit testing and rendering, without stale alpha masks', () => {
    const engine = make(), layer = engine.addText({ x: 30, y: 30 }, '中A。', { fontSize: 48 });
    const canvas = createCanvas(640, 480); engine.render(canvas, { selection: false });
    let ink;
    for (let y = 30; y < 100 && !ink; y++) for (let x = 30; x < 200; x++) if (px(canvas, x, y)[3] > 240) { ink = { x: x + 0.5, y: y + 0.5 }; break; }
    assert.ok(ink); assert.equal(engine.hitTest(ink, 0)?.id, layer.id);
    engine.moveLayer(layer.id, 200, 100);
    assert.equal(engine.hitTest(ink, 0), null);
    assert.equal(engine.hitTest({ x: ink.x + 200, y: ink.y + 100 }, 0)?.id, layer.id);
    engine.undo(); assert.equal(engine.hitTest(ink, 0)?.id, layer.id);
  });
  await test('Long text alpha picking uses a small fixed pointer mask instead of a giant canvas', () => {
    const engine = make(), layer = engine.addText({ x: 10, y: 10 }, '中A'.repeat(3000), { fontSize: 28 });
    engine.hitTest({ x: 20, y: 30 }, 3);
    assert.ok(engine.boundsOfLayer(layer).width > 32768);
    assert.ok(engine._textHitCanvas.width <= 9); assert.ok(engine._textHitCanvas.height <= 9);
  });
  await test('Flattened PNG retains source dimensions, original image, colors, and no selection chrome', async () => {
    const source = createCanvas(320, 180), base = source.getContext('2d');
    base.fillStyle = '#fff1dc'; base.fillRect(0, 0, 320, 180);
    const engine = make(320, 180, source), layer = draw(engine, 'rectangle', { x: 30, y: 30 }, { x: 180, y: 120 }, { color: '#ff0000' });
    engine.select(layer.id);
    const dataURL = engine.flattenPNG();
    assert.ok(dataURL.startsWith('data:image/png;base64,'));
    const image = await loadImage(dataURL); assert.equal(image.width, 320); assert.equal(image.height, 180);
    const target = createCanvas(320, 180); target.getContext('2d').drawImage(image, 0, 0);
    assert.deepEqual(px(target, 100, 30), [255, 0, 0, 255]);
    assert.deepEqual(px(target, 20, 20), [255, 241, 220, 255]);
    assert.deepEqual(px(target, 23, 23), [255, 241, 220, 255]);
    assert.deepEqual(px(source, 100, 30), [255, 241, 220, 255]);
    fs.writeFileSync(path.join(output, 'flattened-source.png'), target.toBuffer('image/png'));
  });
  await test('JSON round-trip preserves vector layers, source size, text and exact flattened raster', () => {
    const engine = make();
    draw(engine, 'pen', { x: 10, y: 15 }, { x: 80, y: 70 }, { color: '#ff8833' });
    draw(engine, 'arrow', { x: 120, y: 150 }, { x: 250, y: 80 });
    engine.addText({ x: 20, y: 220 }, '中文 A, B。\n第二行', { fontSize: 32 });
    const saved = engine.serialize(), restored = make(); restored.load(JSON.parse(JSON.stringify(saved)));
    assert.deepEqual(restored.serialize(), saved); assert.equal(restored.flattenPNG(), engine.flattenPNG());
    const added = draw(restored, 'ellipse', { x: 350, y: 100 }, { x: 500, y: 200 });
    assert.ok(!saved.layers.some(layer => layer.id === added.id));
    assert.throws(() => make(20, 20).load(saved), /dimensions/);
    assert.throws(() => restored.load({ ...saved, layers: [saved.layers[0], saved.layers[0]] }), /Duplicate/);
  });
  await test('Viewport transforms do not mutate source coordinates; image clipping prevents markup outside screenshot', () => {
    const engine = make(100, 100); draw(engine, 'pen', { x: -30, y: 50 }, { x: 200, y: 50 }, { lineWidth: 10 });
    const before = engine.serialize(), canvas = createCanvas(300, 300), ctx = canvas.getContext('2d');
    ctx.translate(20, 30); ctx.scale(2, 2); engine.draw(ctx, { selection: false });
    assert.equal(px(canvas, 10, 130)[3], 0); assert.ok(px(canvas, 80, 130)[3] > 200); assert.equal(px(canvas, 230, 130)[3], 0);
    assert.deepEqual(engine.serialize(), before);
  });
  await test('Export refuses an unfinished gesture, preventing half-committed screenshot state', () => {
    const engine = make(); engine.begin('pen', { x: 20, y: 20 });
    assert.throws(() => engine.flattenPNG(), /Finish/); engine.cancelGesture();
    assert.ok(engine.flattenPNG().startsWith('data:image/png;base64,'));
  });
  const report = { generatedAt: new Date().toISOString(), renderer: '@napi-rs/canvas (real raster pixels)', fontPath: fontPath || null, fontLoaded, passed: cases.every(item => item.passed), cases };
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ passed: report.passed, total: cases.length, fontLoaded, output, failures: cases.filter(item => !item.passed) }, null, 2));
  if (!report.passed) process.exitCode = 1;
})();
