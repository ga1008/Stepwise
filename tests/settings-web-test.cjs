/* Executes the shipping settings script and native bridge protocol. Run: node tests/settings-web-test.cjs [output-directory] */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const web = path.join(__dirname, '../src/Stepwise/Web');
const output = path.resolve(process.argv[2] || path.join(__dirname, '../verification/settings-web'));
const results = [];
class Element {
  constructor(id = '') { this.id = id; this.value = ''; this.checked = false; this.disabled = false; this.hidden = false; this.listeners = {}; this.attributes = {}; this.children = []; const classes = new Set(); this.classList = { toggle(name, force) { force ? classes.add(name) : classes.delete(name); }, contains(name) { return classes.has(name); } }; }
  addEventListener(type, callback) { (this.listeners[type] ??= []).push(callback); }
  async fire(type, event = {}) { for (const callback of this.listeners[type] || []) await callback({ target: this, preventDefault() {}, stopPropagation() {}, ...event }); }
  setAttribute(name, value) { this.attributes[name] = value; }
  getAttribute(name) { return this.attributes[name]; }
  append(...children) { this.children.push(...children); for (const child of children) if (child.id) this.owner.elements[child.id] = child; }
  focus() { this.owner.activeElement = this; }
  blur() { this.owner.activeElement = null; }
  scrollIntoView() { this.scrolled = true; }
  reportValidity() { return true; }
}
const defaults = () => ({ annotation: { tool: 'rect', color: '#f04d4d', lineWidth: 5, fontSize: 28, zoomMode: 'fit', zoomScale: 1 }, systemAudio: true, microphone: true, exportFormat: 'mp4', exportMuted: false, projectParentDirectory: '', toolbar: null, rememberChoices: true, screenshotDelay: 0, recordingDelay: 0, mp4FramesPerSecond: 20, mp4MaxWidth: 0, gifFramesPerSecond: 12, gifMaxWidth: 1440, hotkeys: { screenshot: 'Ctrl+Alt+S', record: 'Ctrl+Alt+R', preview: 'Ctrl+Alt+P', showToolbar: 'Ctrl+Alt+B', settings: 'Ctrl+Alt+O' } });
function harness(preferences = defaults()) {
  const html = fs.readFileSync(path.join(web, 'settings.html'), 'utf8');
  const elements = Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element(match[1])]));
  const document = { elements, activeElement: null, getElementById(id) { assert.ok(elements[id], `Actual HTML has ${id}`); return elements[id]; }, querySelectorAll(selector) { return selector === 'nav a' ? [] : Object.values(elements); }, createElement() { const element = new Element(); element.owner = document; return element; } };
  Object.values(elements).forEach(element => element.owner = document);
  const messages = []; const window = { chrome: { webview: { postMessage(message) { messages.push(JSON.parse(JSON.stringify(message))); } } } };
  vm.runInNewContext(fs.readFileSync(path.join(web, 'settings.js'), 'utf8'), { document, window, console }, { filename: 'settings.js' });
  window.stepwiseSettings.receive({ type: 'init', preferences, defaults: defaults() });
  const receive = message => window.stepwiseSettings.receive(message);
  const validate = errors => { const latest = messages.filter(message => message.type === 'validateHotkeys').at(-1); receive({ type: 'hotkeyValidation', requestId: latest.requestId, errors: errors || {} }); };
  return { elements, document, messages, receive, validate, writes: () => messages.filter(message => message.type === 'save'), async field(id, value) { if (typeof value === 'boolean') elements[id].checked = value; else elements[id].value = String(value); await elements[id].fire('input'); }, async submit() { await elements['settings-form'].fire('submit'); } };
}
async function test(name, run) { try { await run(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, error: error.stack }); } }
(async () => {
  await test('Restores all persisted controls, validates hotkeys, and never writes on open', async () => {
    const preferences = defaults(); Object.assign(preferences, { screenshotDelay: 5, recordingDelay: 10, rememberChoices: false, mp4FramesPerSecond: 30, mp4MaxWidth: 1920, gifFramesPerSecond: 15, gifMaxWidth: 960 }); Object.assign(preferences.annotation, { color: '#112233', fontSize: 64, zoomMode: 'custom', zoomScale: 1.75 });
    const h = harness(preferences); assert.equal(h.elements['screenshot-delay'].value, '5'); assert.equal(h.elements['recording-delay'].value, '10'); assert.equal(h.elements['remember-choices'].checked, false); assert.equal(h.elements['mp4-fps'].value, '30'); assert.equal(h.elements['gif-width'].value, '960'); assert.equal(h.elements['zoom-scale'].value, '175'); assert.equal(h.elements['zoom-custom'].hidden, false); assert.equal(h.elements.save.disabled, true); assert.equal(h.writes().length, 0); assert.equal(h.messages.filter(message => message.type === 'dirty').length, 0); h.validate(); assert.equal(h.elements.save.disabled, false);
  });
  await test('Edited settings remain a draft until Save, preserving unexposed position metadata', async () => {
    const preferences = defaults(); preferences.toolbar = { screenDevice: 'DISPLAY2', dock: 'Top', xFraction: .7, yFraction: 0 }; const h = harness(preferences); h.validate(); await h.field('annotation-color', '#abcdef'); await h.field('remember-choices', false); await h.field('screenshot-delay', 3); assert.equal(h.writes().length, 0); await h.submit(); assert.equal(h.writes().length, 1); const saved = h.writes()[0].preferences; assert.equal(saved.annotation.color, '#abcdef'); assert.equal(saved.rememberChoices, false); assert.equal(saved.screenshotDelay, 3); assert.deepEqual(saved.toolbar, preferences.toolbar); assert.equal(h.elements.save.disabled, true);
  });
  await test('Hotkey capture sends current full map; stale responses cannot override new validation', async () => {
    const h = harness(); h.validate(); const input = h.elements['hotkey-screenshot']; await input.fire('keydown', { key: 'x', code: 'KeyX', ctrlKey: true, altKey: true }); const first = h.messages.at(-1); await input.fire('keydown', { key: 'y', code: 'KeyY', ctrlKey: true, altKey: true }); const second = h.messages.at(-1); assert.equal(second.hotkeys.screenshot, 'Ctrl+Alt+Y'); assert.equal(second.hotkeys.record, 'Ctrl+Alt+R'); h.receive({ type: 'hotkeyValidation', requestId: first.requestId, errors: { screenshot: '被占用' } }); assert.equal(h.elements.save.disabled, true); h.receive({ type: 'hotkeyValidation', requestId: second.requestId, errors: {} }); assert.equal(h.elements.save.disabled, false); assert.equal(h.elements['hotkey-error-screenshot'].hidden, true);
  });
  await test('Occupied hotkeys keep fields editable and block saving until a correction passes', async () => {
    const h = harness(); h.validate({ screenshot: '已被其他程序占用' }); assert.equal(h.elements['hotkey-screenshot'].disabled, false); assert.equal(h.elements.save.disabled, true); assert.equal(h.elements['hotkey-error-screenshot'].textContent, '已被其他程序占用'); await h.submit(); assert.equal(h.writes().length, 0); await h.elements['hotkey-screenshot'].fire('keydown', { key: 'Backspace' }); assert.equal(h.messages.at(-1).hotkeys.screenshot, ''); h.validate(); await h.submit(); assert.equal(h.writes()[0].preferences.hotkeys.screenshot, '');
  });
  await test('Native registered-key forwarding affects only a focused hotkey and deduplicates repeated delivery', async () => {
    const h = harness(); h.validate(); h.receive({ type: 'capturedHotkey', chord: 'Ctrl+Alt+R' }); assert.equal(h.elements['hotkey-screenshot'].value, 'Ctrl+Alt+S'); h.elements['hotkey-screenshot'].focus(); h.receive({ type: 'capturedHotkey', chord: 'Ctrl+Alt+R' }); const count = h.messages.length; h.receive({ type: 'capturedHotkey', chord: 'Ctrl+Alt+R' }); assert.equal(h.messages.length, count); assert.equal(h.elements['hotkey-screenshot'].value, 'Ctrl+Alt+R'); h.validate({ screenshot: '与立即录制重复', record: '与立即截图重复' }); assert.equal(h.elements.save.disabled, true);
  });
  await test('Modifier-only keys and Escape do not change the draft; Tab stays available for navigation', async () => {
    const h = harness(); h.validate(); const input = h.elements['hotkey-screenshot']; input.focus(); const before = h.messages.length; for (const key of ['Control', 'Alt', 'Shift', 'Tab', 'Escape']) await input.fire('keydown', { key }); assert.equal(h.messages.length, before); assert.equal(h.document.activeElement, null);
  });
  await test('Missing physical key code falls back to ASCII event key but does not accept IME text', async () => {
    const h = harness(); h.validate(); const input = h.elements['hotkey-record'];
    await input.fire('keydown', { key: 'r', ctrlKey: true, altKey: true, shiftKey: true });
    assert.equal(h.messages.at(-1).hotkeys.record, 'Ctrl+Alt+Shift+R');
    h.validate(); const count = h.messages.length;
    await input.fire('keydown', { key: '录', code: 'KeyR', ctrlKey: true, altKey: true, isComposing: true });
    assert.equal(h.messages.length, count); assert.equal(input.value, 'Ctrl+Alt+Shift+R');
    assert.match(h.elements.feedback.textContent, /输入法/);
    await input.fire('keydown', { key: '录', ctrlKey: true, altKey: true });
    assert.equal(h.messages.length, count); assert.match(h.elements.feedback.textContent, /字母/);
    await input.fire('keydown', { key: '7', code: '', ctrlKey: true, shiftKey: true });
    assert.equal(h.messages.at(-1).hotkeys.record, 'Ctrl+Shift+7');
  });
  await test('Cancel never sends a save, and restoring defaults affects only the draft until Save', async () => {
    const preferences = defaults(); preferences.annotation.color = '#123456'; preferences.toolbar = { dock: 'Right' }; const h = harness(preferences); h.validate(); await h.elements['reset-all'].fire('click'); assert.equal(h.elements['annotation-color'].value, '#f04d4d'); assert.equal(h.writes().length, 0); await h.elements.cancel.fire('click'); assert.equal(h.messages.at(-1).type, 'cancel'); assert.equal(h.writes().length, 0); h.validate(); await h.submit(); assert.deepEqual(h.writes()[0].preferences.toolbar, { dock: 'Right' });
  });
  await test('Directory chooser uses current draft and its response is persisted only by Save', async () => {
    const h = harness(); h.validate(); await h.field('project-directory', 'C:\\Projects'); await h.elements['browse-directory'].fire('click'); assert.deepEqual(h.messages.at(-1), { type: 'browseDirectory', path: 'C:\\Projects' }); h.receive({ type: 'directory', path: 'D:\\操作记录' }); assert.equal(h.writes().length, 0); await h.submit(); assert.equal(h.writes()[0].preferences.projectParentDirectory, 'D:\\操作记录');
  });
  await test('Write failure retains edits and enables retry; save-time conflict is displayed and blocks retry', async () => {
    const h = harness(); h.validate(); await h.field('gif-fps', 18); await h.submit(); h.receive({ type: 'error', message: '无法写入设置' }); assert.equal(h.elements['gif-fps'].value, '18'); assert.equal(h.elements.save.disabled, false); assert.equal(h.elements.feedback.textContent, '无法写入设置'); await h.submit(); h.receive({ type: 'saveRejected', message: '占用', errors: { preview: '被占用' } }); assert.equal(h.elements.save.disabled, true); assert.equal(h.elements['hotkey-preview'].disabled, false); assert.equal(h.elements.hotkeys.scrolled, true);
  });
  await test('Invalid output width is rejected with useful feedback before reaching native save', async () => {
    const h = harness(); h.validate(); await h.field('gif-width', 80); await h.submit(); assert.equal(h.writes().length, 0); assert.match(h.elements.feedback.textContent, /160–7680/); await h.field('gif-width', 0); await h.field('export-format', 'gif'); await h.submit(); assert.equal(h.writes()[0].preferences.gifMaxWidth, 0); assert.equal(h.writes()[0].preferences.exportMuted, false);
  });
  fs.mkdirSync(output, { recursive: true }); fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ passed: results.every(result => result.passed), total: results.length, results }, null, 2));
  for (const result of results) console.log(`${result.passed ? 'PASS' : 'FAIL'} ${result.name}${result.error ? '\n' + result.error : ''}`);
  if (results.some(result => !result.passed)) process.exitCode = 1;
})();
