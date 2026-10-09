/* The real document-page script, exercised through its host message boundary. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const web = path.join(__dirname, '../src/Stepwise/Web');
const html = fs.readFileSync(path.join(web, 'app.html'), 'utf8');
const messages = [];
class Element {
  constructor() {
    this.listeners = {}; this.dataset = {}; this.attributes = {}; this.children = []; this.value = ''; this.disabled = false;
    this.lastChild = { textContent: '' }; this.classList = { add() {}, remove() {}, toggle() {} };
  }
  addEventListener(type, callback) { (this.listeners[type] ??= []).push(callback); }
  fire(type) { for (const callback of this.listeners[type] || []) callback({ preventDefault() {} }); }
  hasAttribute(name) { return name === 'data-busy-disabled' ? Object.hasOwn(this.dataset, 'busyDisabled') : Object.hasOwn(this.attributes, name); }
  setAttribute(name, value) { this.attributes[name] = value; }
  closest() { return null; }
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = items; }
}
const elements = Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element()]));
const document = new Element();
document.body = new Element(); document.scrollingElement = { scrollTop: 0 };
document.getElementById = id => { assert.ok(elements[id], `Real HTML element ${id} exists`); return elements[id]; };
document.createElement = () => new Element(); document.querySelector = () => null;
document.querySelectorAll = selector => selector === 'button, input, textarea' ? Object.values(elements) : [];
const window = new Element();
window.chrome = { webview: { postMessage: message => messages.push(JSON.parse(JSON.stringify(message))), addEventListener() {} } };
vm.runInNewContext(fs.readFileSync(path.join(web, 'app.js'), 'utf8'), { window, document, URL, console, setTimeout, clearTimeout }, { filename: 'app.js' });
const results = [];
function test(name, run) { try { run(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, error: error.stack }); } }
test('Export appears inside preview tools and existing print action remains', () => {
  const previewStart = html.indexOf('<main id="preview"');
  assert.ok(html.indexOf('id="export-button"') > previewStart);
  assert.ok(html.indexOf('id="export-button"') < html.indexOf('</main>', previewStart));
  assert.equal([...html.matchAll(/id="export-button"/g)].length, 1);
  elements['print-button'].fire('click'); assert.equal(messages.at(-1).type, 'print');
});
test('Editor and uninitialized page cannot send export requests', () => {
  const before = messages.filter(item => item.type === 'export').length;
  elements['export-button'].fire('click');
  assert.equal(messages.filter(item => item.type === 'export').length, before);
});
test('Preview requests a host-owned export without sending mutable document data', () => {
  window.stepwise.receive({ type: 'init', mode: 'preview', project: { title: '分享测试', createdAt: '2026-10-09', steps: [] } });
  assert.equal(elements.preview.hidden, false);
  elements['export-button'].fire('click');
  assert.deepEqual(messages.at(-1), { type: 'export' });
});
test('Busy preview blocks export and recovers after host failure', () => {
  const before = messages.filter(item => item.type === 'export').length;
  window.stepwise.receive({ type: 'busy', message: '正在整理文档' });
  assert.equal(elements['export-button'].disabled, true);
  elements['export-button'].fire('click');
  assert.equal(messages.filter(item => item.type === 'export').length, before);
  window.stepwise.receive({ type: 'error', message: '导出未完成' });
  assert.equal(elements['export-button'].disabled, false);
  elements['export-button'].fire('click');
  assert.equal(messages.filter(item => item.type === 'export').length, before + 1);
});
const output = path.resolve(process.argv[2] || path.join(__dirname, '../verification/export-bridge'));
fs.mkdirSync(output, { recursive: true });
fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ passed: results.every(item => item.passed), total: results.length, results }, null, 2));
for (const item of results) console.log(`${item.passed ? 'PASS' : 'FAIL'} ${item.name}${item.error ? '\n' + item.error : ''}`);
if (results.some(item => !item.passed)) process.exitCode = 1;
