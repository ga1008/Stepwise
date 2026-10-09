'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const state = { mode: 'editor', project: null, step: null, rootUrl: 'https://project.stepwise.local/', format: 'mp4', mp4Muted: false, busy: false, dirty: false, draftRevision: 0, sentRevision: -1, deleteId: null, draftTimer: null };
  const send = message => { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message); };
  const pad = value => String(value).padStart(2, '0');
  const titleOf = step => step.title || `步骤 ${step.number}`;
  const kindOf = step => ({ image: '屏幕截图', video: '屏幕录制', gif: 'GIF 动图' }[step.kind] || '素材');
  const durationOf = seconds => { const s = Math.max(0, Math.floor(Number(seconds) || 0)); return s >= 3600 ? `${Math.floor(s / 3600)}:${pad(Math.floor(s / 60) % 60)}:${pad(s % 60)}` : `${Math.floor(s / 60)}:${pad(s % 60)}`; };
  const isRecording = step => step && (step.kind === 'video' || step.kind === 'gif');
  const mediaUrl = path => {
    if (typeof path !== 'string' || !path || /^[\\/]|:|\u0000/.test(path)) return '';
    const parts = path.replace(/\\/g, '/').split('/');
    if (parts.some(part => part === '..' || part === '.')) return '';
    try { const root = new URL(state.rootUrl); if (root.origin !== 'https://project.stepwise.local') return ''; return new URL(parts.map(encodeURIComponent).join('/'), root).href; } catch { return ''; }
  };
  function showStatus(message, kind = 'error') {
    $('status').hidden = !message;
    $('status').classList.toggle('busy', kind === 'busy');
    $('status').classList.toggle('notice', kind === 'notice');
    $('status-text').textContent = message || '';
    $('dismiss-status').hidden = kind === 'busy';
  }
  function setBusy(busy, message) {
    state.busy = busy;
    document.querySelectorAll('button, input, textarea').forEach(control => {
      if (control.closest('dialog')) return;
      if (busy) { if (!control.hasAttribute('data-busy-disabled')) control.dataset.busyDisabled = String(control.disabled); control.disabled = true; }
      else if (control.hasAttribute('data-busy-disabled')) { control.disabled = control.dataset.busyDisabled === 'true'; delete control.dataset.busyDisabled; }
    });
    $('save-button').textContent = busy ? '正在保存…' : '保存步骤 ↗';
    if (!busy) updateFormat();
    showStatus(message, busy ? 'busy' : 'error');
  }
  function makeMedia(step, zoomable = false) {
    const url = mediaUrl(step.mediaPath);
    if (!url) { const error = document.createElement('p'); error.className = 'media-error'; error.textContent = '素材路径无效'; return error; }
    const media = document.createElement(step.kind === 'video' ? 'video' : 'img');
    if (step.kind === 'video') { media.controls = true; media.preload = 'metadata'; media.playsInline = true; media.muted = Boolean(step.muted); }
    else { media.alt = titleOf(step); if (zoomable) { media.classList.add('zoomable'); media.tabIndex = 0; media.title = '点击放大'; media.setAttribute('role', 'button'); media.addEventListener('click', () => showLightbox(media)); media.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); showLightbox(media); } }); } }
    media.addEventListener('error', () => { if (media.parentElement && !media.parentElement.querySelector('.media-error')) { const error = document.createElement('p'); error.className = 'media-error'; error.textContent = '素材暂时无法显示，请在项目文件夹中查看。'; media.after(error); } });
    media.src = url;
    return media;
  }
  function showLightbox(media) { $('lightbox-image').src = media.src; $('lightbox-image').alt = media.alt; $('lightbox').showModal(); }
  function editorPayload(type) {
    return { type, step: { id: state.step.id, title: $('step-title').value.trim(), description: $('step-description').value }, format: state.format, muted: state.format === 'gif' || $('muted').checked, revision: state.draftRevision };
  }
  function getDraft() { return state.mode === 'editor' && state.step && !state.busy ? editorPayload('draft') : null; }
  function flushDraft() {
    clearTimeout(state.draftTimer);
    if (state.mode !== 'editor' || !state.step || !state.dirty || state.busy || state.sentRevision === state.draftRevision) return false;
    state.sentRevision = state.draftRevision;
    send(editorPayload('draft'));
    $('draft-indicator').lastChild.textContent = ' 正在暂存…';
    return true;
  }
  function markDirty() {
    state.dirty = true;
    state.draftRevision++;
    $('title-count').textContent = `${$('step-title').value.length} / 200`;
    $('draft-indicator').lastChild.textContent = ' 正在暂存…';
    clearTimeout(state.draftTimer);
    state.draftTimer = setTimeout(flushDraft, 300);
  }
  function updateFormat() {
    document.querySelectorAll('[data-format]').forEach(button => { const selected = button.dataset.format === state.format; button.classList.toggle('selected', selected); button.setAttribute('aria-pressed', String(selected)); });
    $('muted').disabled = state.busy || state.format === 'gif';
    $('muted').checked = state.format === 'gif' || state.mp4Muted;
    $('format-hint').textContent = state.format === 'gif' ? 'GIF 无声，长录制转换需要一点时间' : '保留原始录制，可再次转换';
    $('recording-options').classList.toggle('gif-selected', state.format === 'gif');
  }
  function rememberRecordingPreferences() {
    if(state.mode==='editor'&&isRecording(state.step))send({type:'recordingPreferences',format:state.format,muted:state.mp4Muted});
  }
  function renderEditor(step, previousId, recordingDefaults) {
    if (!step) { showStatus('没有可编辑的步骤。'); return; }
    $('editor-overline').textContent = `STEP ${pad(step.number || (state.project.steps.length + 1))}`;
    $('capture-kind').textContent = step.kind === 'image' ? '本次截图' : '本次录制';
    $('capture-duration').textContent = step.durationSeconds > 0 ? durationOf(step.durationSeconds) : '';
    $('capture-details').textContent = step.width && step.height ? `${step.width} × ${step.height}` : kindOf(step);
    if (previousId !== step.id) {
      $('editor-media').replaceChildren(makeMedia(step, true));
      $('step-title').value = step.title || '';
      $('step-description').value = step.description || '';
      $('muted').checked = Boolean(step.muted);
      state.mp4Muted = Boolean(step.muted);
      state.format = step.kind === 'gif' ? 'gif' : 'mp4';
      if(isRecording(step)&&recordingDefaults&&typeof recordingDefaults==='object'){
        if(['mp4','gif'].includes(recordingDefaults.format))state.format=recordingDefaults.format;
        if(typeof recordingDefaults.muted==='boolean')state.mp4Muted=recordingDefaults.muted;
      }
      state.dirty = false;
      state.draftRevision = 0;
      state.sentRevision = -1;
      $('draft-indicator').lastChild.textContent = step.isDraft ? ' 自动暂存' : ' 已保存';
    }
    $('title-count').textContent = `${$('step-title').value.length} / 200`;
    $('recording-options').hidden = !isRecording(step);
    updateFormat();
  }
  function renderPreview() {
    const project = state.project;
    const steps = project.steps || [];
    const scroll = document.scrollingElement.scrollTop;
    $('project-title').textContent = project.title || '操作记录';
    $('step-count').textContent = pad(steps.length);
    const created = new Date(project.createdAt);
    const draftCount = steps.filter(step => step.isDraft).length;
    $('document-meta').textContent = `${steps.length} 个步骤 · ${Number.isNaN(created.getTime()) ? '' : created.toLocaleDateString('zh-CN', { year: 'numeric', month: 'long', day: 'numeric' })}${draftCount ? ` · ${draftCount} 个待补充` : ''}`;
    $('step-navigation').replaceChildren();
    $('steps').replaceChildren();
    if (!steps.length) {
      const empty = document.createElement('section'); empty.className = 'empty-state';
      const plus = document.createElement('div'); plus.className = 'empty-plus'; plus.textContent = '＋';
      const heading = document.createElement('h2'); heading.textContent = '从第一个步骤开始';
      const tip = document.createElement('p'); tip.textContent = '通过悬浮操作栏截图或录制，记录会自动汇集于此。';
      empty.append(plus, heading, tip); $('steps').append(empty);
    }
    steps.forEach((step, index) => {
      const item = $('step-template').content.firstElementChild.cloneNode(true);
      item.id = `step-${step.id}`;
      item.querySelector('.step-number').textContent = pad(step.number);
      item.querySelector('.step-label').textContent = `STEP ${pad(step.number)}`;
      if (step.isDraft) { const badge = document.createElement('span'); badge.className = 'draft-badge'; badge.textContent = '待补充'; item.querySelector('.step-label').append(badge); }
      item.querySelector('h2').textContent = titleOf(step);
      item.querySelector('.step-description').textContent = step.description || '';
      item.querySelector('.step-description').hidden = !step.description;
      item.querySelector('.step-media').append(makeMedia(step, true));
      if (step.kind === 'video') { const fallback = document.createElement('div'); fallback.className = 'print-video'; fallback.textContent = `视频片段 · ${String(step.mediaPath || '').split('/').pop()} · ${durationOf(step.durationSeconds)}`; item.querySelector('.step-media').append(fallback); }
      const details = [kindOf(step), step.durationSeconds > 0 ? durationOf(step.durationSeconds) : '', step.kind === 'video' && step.muted ? '无声' : ''].filter(Boolean);
      item.querySelector('figcaption').textContent = details.join(' · ');
      const up = item.querySelector('.move-up'), down = item.querySelector('.move-down');
      up.disabled = index === 0; down.disabled = index === steps.length - 1;
      up.addEventListener('click', () => send({ type: 'move', id: step.id, direction: -1 }));
      down.addEventListener('click', () => send({ type: 'move', id: step.id, direction: 1 }));
      item.querySelector('.edit-step').addEventListener('click', () => send({ type: 'edit', id: step.id }));
      item.querySelector('.delete-step').addEventListener('click', () => { state.deleteId = step.id; $('delete-dialog').showModal(); });
      $('steps').append(item);
      const link = document.createElement('a'); link.href = `#${item.id}`;
      const number = document.createElement('span'); number.textContent = pad(step.number);
      const label = document.createElement('span'); label.textContent = titleOf(step);
      link.append(number, label); link.addEventListener('click', event => { event.preventDefault(); item.scrollIntoView({ behavior: 'smooth', block: 'start' }); });
      $('step-navigation').append(link);
    });
    document.scrollingElement.scrollTop = scroll;
  }
  function receive(message) {
    if (typeof message === 'string') { try { message = JSON.parse(message); } catch { return; } }
    if (!message || typeof message !== 'object') return;
    if (message.type === 'draftSaved') { if (Number(message.revision) >= state.draftRevision) { state.dirty = false; $('draft-indicator').lastChild.textContent = ' 已暂存'; } return; }
    if (message.type === 'preferencesError') { showStatus(message.message || '设置暂未保存，请重试。', 'notice'); return; }
    if (message.type === 'notice') { setBusy(false, ''); showStatus(message.message || '已完成', 'notice'); return; }
    if (message.type === 'error') { state.dirty = true; state.sentRevision = -1; setBusy(false, message.message || '操作失败，请重试。'); $('draft-indicator').lastChild.textContent = ' 暂存未完成'; return; }
    if (message.type === 'busy') { setBusy(true, message.message || '正在处理…'); return; }
    if (message.type !== 'init' || !message.project) return;
    const previousId = state.step && state.step.id;
    state.mode = message.mode === 'preview' ? 'preview' : 'editor'; state.project = message.project; state.step = message.step || null;
    if (typeof message.rootUrl === 'string') state.rootUrl = message.rootUrl;
    document.body.classList.toggle('preview-mode', state.mode === 'preview');
    $('editor').hidden = state.mode !== 'editor'; $('preview').hidden = state.mode !== 'preview'; $('preview-button').hidden = state.mode === 'preview'; $('loading').hidden = true;
    $('warnings').replaceChildren();
    const warnings = Array.isArray(message.warnings) ? message.warnings : [];
    warnings.forEach(warning => { const item = document.createElement('p'); item.textContent = String(warning); $('warnings').append(item); });
    $('warnings').hidden = warnings.length === 0;
    setBusy(false, '');
    if (state.mode === 'editor') renderEditor(state.step, previousId, message.recordingDefaults); else renderPreview();
  }
  window.stepwise = { receive, flushDraft, getDraft };
  if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', event => receive(event.data));
  $('step-title').addEventListener('input', markDirty); $('step-description').addEventListener('input', markDirty); $('muted').addEventListener('change', () => { if(state.busy||state.format==='gif'||!isRecording(state.step))return;state.mp4Muted = $('muted').checked;rememberRecordingPreferences();markDirty(); });
  document.querySelectorAll('[data-format]').forEach(button => button.addEventListener('click', () => { if(state.busy||!isRecording(state.step))return;state.format = button.dataset.format;updateFormat();rememberRecordingPreferences();markDirty(); }));
  $('step-form').addEventListener('submit', event => { event.preventDefault(); if (state.busy || !state.step) return; clearTimeout(state.draftTimer); const message = editorPayload('save'); setBusy(true, isRecording(state.step) ? '正在保存素材和文档…' : '正在保存步骤…'); send(message); });
  const close = () => { if (state.busy) return; flushDraft(); send({ type: 'close' }); };
  $('close-button').addEventListener('click', close);
  $('preview-button').addEventListener('click', () => { flushDraft(); send({ type: 'preview' }); });
  $('reveal-button').addEventListener('click', () => send({ type: 'reveal' }));
  $('dismiss-status').addEventListener('click', () => showStatus(''));
  $('lightbox-close').addEventListener('click', () => $('lightbox').close()); $('lightbox').addEventListener('click', event => { if (event.target === $('lightbox')) $('lightbox').close(); });
  const rename = () => { $('rename-input').value = state.project.title; $('rename-dialog').showModal(); $('rename-input').focus(); $('rename-input').select(); };
  $('rename-button').addEventListener('click', rename); $('project-title').addEventListener('click', rename); $('project-title').addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); rename(); } });
  $('rename-cancel').addEventListener('click', () => $('rename-dialog').close());
  $('rename-form').addEventListener('submit', event => { event.preventDefault(); const title = $('rename-input').value.trim(); if (!title) { $('rename-input').focus(); return; } send({ type: 'rename', title }); $('rename-dialog').close(); });
  $('delete-cancel').addEventListener('click', () => $('delete-dialog').close());
  $('delete-confirm').addEventListener('click', () => { if (state.deleteId) send({ type: 'delete', id: state.deleteId }); state.deleteId = null; $('delete-dialog').close(); });
  $('print-button').addEventListener('click', () => { if (window.chrome && window.chrome.webview) send({ type: 'print' }); else window.print(); });
  $('export-button').addEventListener('click', () => { if (state.mode === 'preview' && !state.busy) send({ type: 'export' }); });
  $('recover-button').addEventListener('click', () => send({ type: 'recover' }));
  window.addEventListener('keydown', event => { if (document.querySelector('dialog[open]')) return; if (event.key === 'Escape') { event.preventDefault(); close(); } if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && state.mode === 'editor') { event.preventDefault(); $('step-form').requestSubmit(); } });
  window.addEventListener('beforeunload', flushDraft);
  send({ type: 'ready' });
})();
