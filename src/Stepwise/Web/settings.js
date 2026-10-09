(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const clone = value => JSON.parse(JSON.stringify(value));
  const send = message => window.chrome?.webview?.postMessage(message);
  const actions = [['screenshot', '立即截图'], ['record', '立即录制 / 停止'], ['preview', '文档预览'], ['showToolbar', '显示操作栏'], ['settings', '打开设置']];
  const fields = {
    'annotation-tool': ['annotation', 'tool'], 'annotation-color': ['annotation', 'color'],
    'annotation-width': ['annotation', 'lineWidth'], 'annotation-font': ['annotation', 'fontSize'],
    'zoom-mode': ['annotation', 'zoomMode'], 'zoom-scale': ['annotation', 'zoomScale'],
    'screenshot-delay': ['screenshotDelay'], 'recording-delay': ['recordingDelay'],
    'system-audio': ['systemAudio'], 'microphone': ['microphone'], 'export-format': ['exportFormat'],
    'export-muted': ['exportMuted'], 'mp4-fps': ['mp4FramesPerSecond'], 'mp4-width': ['mp4MaxWidth'],
    'gif-fps': ['gifFramesPerSecond'], 'gif-width': ['gifMaxWidth'],
    'project-directory': ['projectParentDirectory'], 'remember-choices': ['rememberChoices']
  };
  const numeric = new Set(['annotation-width', 'annotation-font', 'screenshot-delay', 'recording-delay', 'mp4-fps', 'mp4-width', 'gif-fps', 'gif-width']);
  const checks = new Set(['system-audio', 'microphone', 'export-muted', 'remember-choices']);
  const hotkeyControls = {};
  let draft, baseline, defaults, dirty = false, saving = false, validationId = 0, validating = false, errors = {};

  function notice(text, kind = '') { $('feedback').textContent = text; $('feedback').className = kind; }
  function busy(value) {
    saving = value;
    document.querySelectorAll('input, select, button').forEach(element => { element.disabled = value; });
    $('save').textContent = value ? '正在保存…' : '保存设置';
    refreshSave();
  }
  function refreshSave() { $('save').disabled = !draft || saving || validating || Object.keys(errors).length > 0; }
  function markDirty() {
    const changed = JSON.stringify(draft) !== JSON.stringify(baseline);
    if (changed !== dirty) { dirty = changed; send({ type: 'dirty', value: dirty }); }
    notice(dirty ? '有未保存的更改' : '配置仅在保存后更新');
  }
  function render() {
    for (const [id, path] of Object.entries(fields)) {
      const value = path.length === 2 ? draft[path[0]][path[1]] : draft[path[0]];
      if (checks.has(id)) $(id).checked = !!value;
      else $(id).value = id === 'zoom-scale' ? String(Math.round(value * 100)) : String(value ?? '');
    }
    for (const [key] of actions) hotkeyControls[key].input.value = draft.hotkeys[key] || '';
    renderDependencies();
  }
  function renderDependencies() {
    $('color-value').textContent = $('annotation-color').value.toUpperCase();
    $('zoom-custom').hidden = $('zoom-mode').value !== 'custom';
    $('zoom-scale').required = $('zoom-mode').value === 'custom';
  }
  function changed(id) {
    if (!draft || saving) return;
    const path = fields[id]; let value;
    if (checks.has(id)) value = $(id).checked;
    else if (numeric.has(id) || id === 'zoom-scale') value = $(id).value.trim() === '' ? null : Number($(id).value) / (id === 'zoom-scale' ? 100 : 1);
    else value = $(id).value;
    if (path.length === 2) draft[path[0]][path[1]] = value; else draft[path[0]] = value;
    renderDependencies(); markDirty();
  }
  function renderErrors() {
    for (const [key] of actions) {
      const control = hotkeyControls[key], message = errors[key] || errors[key[0].toUpperCase() + key.slice(1)] || '';
      control.error.textContent = message; control.error.hidden = !message;
      control.input.classList.toggle('invalid', !!message);
      control.input.setAttribute('aria-invalid', String(!!message));
    }
    const count = Object.keys(errors).length;
    $('hotkey-summary').textContent = validating ? '正在检查占用…' : count ? '请调整标出的快捷键。' : '系统级占用检查通过';
    $('hotkey-summary').className = 'hotkey-summary' + (count ? ' error' : '');
    refreshSave();
  }
  function validateHotkeys() {
    if (!draft) return;
    validating = true; errors = {}; validationId++;
    renderErrors(); send({ type: 'validateHotkeys', requestId: validationId, hotkeys: clone(draft.hotkeys) });
  }
  function setHotkey(key, value) {
    if (!draft || saving) return;
    value = value.replace(/\s*\+\s*/g, '+');
    if (draft.hotkeys[key] === value) return;
    draft.hotkeys[key] = value; hotkeyControls[key].input.value = value;
    markDirty(); validateHotkeys();
  }
  function captureKey(event, key) {
    if (event.key === 'Tab' && !event.ctrlKey && !event.altKey && !event.metaKey) return;
    event.preventDefault(); event.stopPropagation();
    if (['Control', 'Alt', 'Shift', 'Meta', 'OS', 'AltGraph'].includes(event.key)) return;
    if (event.isComposing || event.key === 'Process') { notice('请结束输入法组词后再设置快捷键。', 'error'); return; }
    if (event.key === 'Escape' && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) { event.target.blur(); return; }
    if (['Backspace', 'Delete'].includes(event.key) && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) { setHotkey(key, ''); return; }
    let primary = '';
    if (/^Key[A-Z]$/.test(event.code)) primary = event.code.slice(3);
    else if (/^Digit[0-9]$/.test(event.code)) primary = event.code.slice(5);
    else if (/^[a-z0-9]$/i.test(event.key)) primary = event.key.toUpperCase();
    else if (/^F([1-9]|1[0-9]|2[0-4])$/.test(event.key)) primary = event.key;
    else primary = ({ ' ': 'Space', Escape: 'Escape', PrintScreen: 'PrintScreen', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Insert: 'Insert', Delete: 'Delete', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', Tab: 'Tab', Enter: 'Enter', Backspace: 'Backspace', Pause: 'Pause', ScrollLock: 'ScrollLock', NumLock: 'NumLock' })[event.key] || '';
    if (!primary) { notice('请选择字母、数字、功能键或导航键。', 'error'); return; }
    const parts = [];
    if (event.ctrlKey) parts.push('Ctrl'); if (event.altKey) parts.push('Alt'); if (event.shiftKey) parts.push('Shift'); if (event.metaKey) parts.push('Win');
    parts.push(primary); setHotkey(key, parts.join('+'));
  }
  for (const [key, label] of actions) {
    const row = document.createElement('div'); row.className = 'hotkey-row';
    const name = document.createElement('label'); name.textContent = label; name.htmlFor = 'hotkey-' + key;
    const input = document.createElement('input'); input.id = 'hotkey-' + key; input.type = 'text'; input.readOnly = true; input.placeholder = '点击后按组合键'; input.autocomplete = 'off'; input.spellcheck = false; input.setAttribute('aria-describedby', 'hotkey-error-' + key);
    const clear = document.createElement('button'); clear.type = 'button'; clear.className = 'hotkey-clear'; clear.textContent = '×'; clear.title = '停用' + label; clear.setAttribute('aria-label', '停用' + label);
    const error = document.createElement('p'); error.id = 'hotkey-error-' + key; error.className = 'hotkey-error'; error.hidden = true;
    input.addEventListener('keydown', event => captureKey(event, key));
    input.addEventListener('focus', () => notice('按组合键设置；Esc 结束录入，删除键停用。'));
    clear.addEventListener('click', () => setHotkey(key, ''));
    row.append(name, input, clear, error); $('hotkey-list').append(row); hotkeyControls[key] = { input, error, clear };
  }
  for (const id of Object.keys(fields)) {
    $(id).addEventListener('input', () => changed(id));
    $(id).addEventListener('change', () => changed(id));
  }
  document.querySelectorAll('nav a').forEach(link => link.addEventListener('click', event => { event.preventDefault(); $(link.getAttribute('href').slice(1)).scrollIntoView({ behavior: 'smooth', block: 'start' }); }));
  $('browse-directory').addEventListener('click', () => send({ type: 'browseDirectory', path: $('project-directory').value }));
  $('cancel').addEventListener('click', () => { if (!saving) send({ type: 'cancel' }); });
  $('reset-hotkeys').addEventListener('click', () => { if (!draft || saving) return; draft.hotkeys = clone(defaults.hotkeys); render(); markDirty(); validateHotkeys(); });
  $('reset-all').addEventListener('click', () => {
    if (!draft || saving) return;
    const placement = draft.toolbar;
    draft = clone(defaults); draft.toolbar = placement;
    render(); markDirty(); validateHotkeys(); notice('默认配置已填入，保存后生效。');
  });
  $('settings-form').addEventListener('submit', event => {
    event.preventDefault();
    if (!draft || saving || validating || Object.keys(errors).length > 0) return;
    if (!$('settings-form').reportValidity()) return;
    for (const [id, min, max] of [['mp4-fps', 1, 60], ['gif-fps', 1, 30], ['mp4-width', 0, 7680], ['gif-width', 0, 7680]]) {
      const value = Number($(id).value);
      if (!Number.isInteger(value) || value < min || value > max || (id.endsWith('width') && value !== 0 && value < 160)) {
        notice(id.endsWith('width') ? '最大宽度应为 160–7680 px，或 0（原始大小）。' : `帧率应为 ${min}–${max}。`, 'error'); $(id).focus(); return;
      }
    }
    if (draft.annotation.zoomMode === 'fit' && (!Number.isFinite(draft.annotation.zoomScale) || draft.annotation.zoomScale < .01 || draft.annotation.zoomScale > 8)) draft.annotation.zoomScale = 1;
    draft.projectParentDirectory = draft.projectParentDirectory.trim();
    busy(true); notice('正在保存配置…', 'pending');
    send({ type: 'save', preferences: clone(draft) });
  });
  window.stepwiseSettings = {
    receive(message) {
      if (message.type === 'init') {
        draft = clone(message.preferences); baseline = clone(message.preferences); defaults = clone(message.defaults);
        dirty = false; render(); $('loading').hidden = true; $('settings-form').hidden = false; busy(false); validateHotkeys();
      } else if (message.type === 'hotkeyValidation') {
        if (message.requestId !== validationId) return;
        validating = false; errors = message.errors || {}; renderErrors();
      } else if (message.type === 'directory') {
        if (!draft || saving) return; $('project-directory').value = message.path; changed('project-directory');
      } else if (message.type === 'capturedHotkey') {
        const focused = actions.find(([key]) => document.activeElement === hotkeyControls[key].input);
        if (focused) setHotkey(focused[0], message.chord);
      } else if (message.type === 'saveRejected') {
        validating = false; errors = message.errors || {}; busy(false); renderErrors(); notice(message.message || '请调整设置后重试。', 'error');
        if (Object.keys(errors).length) $('hotkeys').scrollIntoView({ behavior: 'smooth', block: 'start' });
      } else if (message.type === 'error') {
        validating = false; busy(false); notice(message.message || '保存失败，请重试。', 'error');
      }
    }
  };
  send({ type: 'ready' });
})();
