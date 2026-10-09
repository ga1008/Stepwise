/* Stepwise screenshot annotations. All geometry is in original screenshot pixels. */
(function (root, factory) {
  const Engine = factory();
  if (typeof module === 'object' && module.exports) module.exports = Engine;
  else root.AnnotationEngine = Engine;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const TOOLS = new Set(['rectangle', 'ellipse', 'pen', 'arrow']);
  const finite = (value, fallback = 0) => Number.isFinite(Number(value)) ? Number(value) : fallback;
  const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
  const point = value => ({ x: finite(value?.x), y: finite(value?.y) });
  const distance = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  const clone = value => JSON.parse(JSON.stringify(value));
  const segmentDistance = (p, a, b) => {
    const dx = b.x - a.x, dy = b.y - a.y, lengthSquared = dx * dx + dy * dy;
    const t = lengthSquared ? clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared, 0, 1) : 0;
    return Math.hypot(p.x - a.x - t * dx, p.y - a.y - t * dy);
  };
  const polygonContains = (p, vertices) => {
    let inside = false;
    for (let i = 0, j = vertices.length - 1; i < vertices.length; j = i++) {
      const a = vertices[i], b = vertices[j];
      if ((a.y > p.y) !== (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
    }
    return inside;
  };
  function extent(points, margin = 0) {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const p of points) { minX = Math.min(minX, p.x); minY = Math.min(minY, p.y); maxX = Math.max(maxX, p.x); maxY = Math.max(maxY, p.y); }
    return { x: minX - margin, y: minY - margin, width: maxX - minX + margin * 2, height: maxY - minY + margin * 2 };
  }
  function makeCanvas(width, height) {
    if (typeof document !== 'undefined') { const canvas = document.createElement('canvas'); canvas.width = width; canvas.height = height; return canvas; }
    if (typeof OffscreenCanvas !== 'undefined') return new OffscreenCanvas(width, height);
    throw new Error('AnnotationEngine requires canvasFactory outside a browser.');
  }
  function normalizeStyle(style, family) {
    // Snapshot primitive values: subsequent toolbar changes never recolor existing layers.
    return {
      color: typeof style?.color === 'string' && /^#(?:[\da-f]{3}|[\da-f]{4}|[\da-f]{6}|[\da-f]{8})$/i.test(style.color) ? style.color : '#ef4444',
      lineWidth: clamp(finite(style?.lineWidth, 5), 1, 100),
      fontSize: clamp(finite(style?.fontSize, 28), 8, 400),
      fontFamily: String(style?.fontFamily || family || 'Stepwise Sans'),
      lineHeight: clamp(finite(style?.lineHeight, 1.35), 1, 2.5)
    };
  }

  class AnnotationEngine {
    constructor(options = {}, legacyHeight) {
      if (typeof options === 'number') options = { width: options, height: legacyHeight };
      this.width = Math.max(1, Math.round(finite(options.width, 1)));
      this.height = Math.max(1, Math.round(finite(options.height, 1)));
      this.image = options.image || null;
      this.fontFamily = options.fontFamily || 'Stepwise Sans';
      this.layers = [];
      this.selectedId = null;
      this.onChange = options.onChange || null;
      this.maxHistory = clamp(finite(options.maxHistory, 80), 1, 300);
      this._factory = options.canvasFactory || makeCanvas;
      this._undo = [];
      this._redo = [];
      this._gesture = null;
      this._move = null;
      this._nextId = 1;
      this._textBounds = new Map();
      this._hitCanvas = null;
      this._textHitCanvas = null;
    }

    get canUndo() { return this._undo.length > 0; }
    get canRedo() { return this._redo.length > 0; }
    get isDrawing() { return Boolean(this._gesture); }
    get isMoving() { return Boolean(this._move); }
    get previewLayer() { return this._gesture?.layer || null; }
    _emit(reason) { if (typeof this.onChange === 'function') this.onChange({ reason, engine: this }); }
    _snapshot() { return { layers: clone(this.layers), selectedId: this.selectedId }; }
    _pushHistory(before) {
      this._undo.push(before);
      if (this._undo.length > this.maxHistory) this._undo.shift();
      this._redo = [];
    }
    _restore(snapshot) { this.layers = clone(snapshot.layers); this.selectedId = snapshot.selectedId; this._textBounds.clear(); }
    _find(layerOrId) { return typeof layerOrId === 'object' ? layerOrId : this.layers.find(layer => layer.id === layerOrId) || null; }
    _newLayer(type, style) { return { id: `stroke-${this._nextId++}`, type, style: normalizeStyle(style, this.fontFamily) }; }
    _context() {
      if (!this._hitCanvas) this._hitCanvas = this._factory(1, 1);
      return this._hitCanvas.getContext('2d', { willReadFrequently: true });
    }

    begin(tool, position, style = {}) {
      if (tool === 'rect') tool = 'rectangle';
      if (!TOOLS.has(tool)) throw new Error(`Unsupported drawing tool: ${tool}`);
      this.cancelGesture(false);
      const p = point(position), layer = this._newLayer(tool, style);
      if (tool === 'pen') layer.points = [p];
      else { layer.start = p; layer.end = { ...p }; }
      this._gesture = { layer, before: this._snapshot() };
      this.selectedId = null;
      this._emit('begin');
      return layer;
    }
    update(position) {
      if (!this._gesture) return false;
      const p = point(position), layer = this._gesture.layer;
      if (layer.type === 'pen') {
        // Keep actual pointer geometry, including slow strokes; drop only identical samples.
        if (distance(layer.points[layer.points.length - 1], p) > 0.05) layer.points.push(p);
      } else layer.end = p;
      this._emit('drawing');
      return true;
    }
    end(position) {
      if (!this._gesture) return null;
      if (position) this.update(position);
      const { layer, before } = this._gesture;
      this._gesture = null;
      if (layer.type !== 'pen' && distance(layer.start, layer.end) < 0.5) {
        this.selectedId = before.selectedId;
        this._emit('cancel');
        return null;
      }
      this._pushHistory(before);
      this.layers.push(layer);
      this.selectedId = layer.id;
      this._emit('added');
      return layer;
    }
    cancelGesture(notify = true) {
      let changed = false;
      if (this._gesture) { this.selectedId = this._gesture.before.selectedId; this._gesture = null; changed = true; }
      if (this._move) { this._restore(this._move.before); this._move = null; changed = true; }
      if (changed && notify) this._emit('cancel');
      return changed;
    }
    addText(position, text, style = {}) {
      const content = String(text ?? '').replace(/\r\n?/g, '\n');
      if (!content.trim()) return null;
      this.cancelGesture(false);
      const before = this._snapshot(), layer = this._newLayer('text', style);
      layer.position = point(position);
      layer.text = content;
      this._pushHistory(before);
      this.layers.push(layer);
      this.selectedId = layer.id;
      this._emit('added');
      return layer;
    }
    select(id) {
      this.selectedId = id && this.layers.some(layer => layer.id === id) ? id : null;
      this._emit('selected');
      return this._find(this.selectedId);
    }
    beginMove(id, position) {
      this.cancelGesture(false);
      const layer = this._find(id);
      if (!layer) return false;
      const before = this._snapshot();
      this.selectedId = layer.id;
      this._move = { id: layer.id, start: point(position), original: clone(layer), before, dx: 0, dy: 0 };
      this._emit('moveBegin');
      return true;
    }
    _translate(layer, dx, dy) {
      const translate = p => ({ x: p.x + dx, y: p.y + dy });
      if (layer.type === 'text') layer.position = translate(layer.position);
      else if (layer.type === 'pen') layer.points = layer.points.map(translate);
      else { layer.start = translate(layer.start); layer.end = translate(layer.end); }
    }
    updateMove(position) {
      if (!this._move) return false;
      const p = point(position), move = this._move;
      move.dx = p.x - move.start.x; move.dy = p.y - move.start.y;
      const changed = clone(move.original);
      this._translate(changed, move.dx, move.dy);
      const index = this.layers.findIndex(layer => layer.id === move.id);
      this.layers[index] = changed;
      this._emit('moving');
      return true;
    }
    endMove(position) {
      if (!this._move) return false;
      if (position) this.updateMove(position);
      const move = this._move;
      this._move = null;
      if (Math.abs(move.dx) + Math.abs(move.dy) > 0.001) this._pushHistory(move.before);
      this._emit('moved');
      return true;
    }
    moveLayer(id, dx, dy) {
      if (!this.beginMove(id, { x: 0, y: 0 })) return false;
      return this.endMove({ x: finite(dx), y: finite(dy) });
    }
    deleteSelected() { return this.deleteLayer(this.selectedId); }
    deleteLayer(id) {
      this.cancelGesture(false);
      const index = this.layers.findIndex(layer => layer.id === id);
      if (index < 0) return false;
      this._pushHistory(this._snapshot());
      this.layers.splice(index, 1);
      this._textBounds.delete(id);
      if (this.selectedId === id) this.selectedId = null;
      this._emit('deleted');
      return true;
    }
    undo() {
      this.cancelGesture(false);
      if (!this.canUndo) return false;
      this._redo.push(this._snapshot());
      this._restore(this._undo.pop());
      this._emit('undo');
      return true;
    }
    redo() {
      this.cancelGesture(false);
      if (!this.canRedo) return false;
      this._undo.push(this._snapshot());
      this._restore(this._redo.pop());
      this._emit('redo');
      return true;
    }

    arrowPolygon(layerOrId) {
      const layer = this._find(layerOrId);
      if (!layer || layer.type !== 'arrow') return [];
      const a = layer.start, b = layer.end, length = distance(a, b);
      if (length < 0.01) return [];
      const ux = (b.x - a.x) / length, uy = (b.y - a.y) / length;
      // Smoothly saturating size gives useful tiny arrows and restrained multi-monitor arrows.
      // Pen thickness has no role here. The arrow tip is exactly the release coordinate.
      const headLength = Math.min(length * 0.38, 8 + 64 * (1 - Math.exp(-length / 400)));
      const halfHead = Math.min(length * 0.22, 3 + 25 * (1 - Math.exp(-length / 320)));
      const halfShaft = Math.min(halfHead * 0.32, 1 + 8 * (1 - Math.exp(-length / 420)));
      const at = (forward, side) => ({ x: a.x + ux * forward - uy * side, y: a.y + uy * forward + ux * side });
      return [at(0, halfShaft), at(length - headLength, halfShaft), at(length - headLength, halfHead), { ...b }, at(length - headLength, -halfHead), at(length - headLength, -halfShaft), at(0, -halfShaft)];
    }
    _font(ctx, style) {
      const family = String(style.fontFamily).replace(/["\\\r\n]/g, '');
      ctx.font = `${style.fontSize}px "${family}", sans-serif`;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'top';
      ctx.direction = 'ltr';
    }
    _textInfo(layer) {
      const cacheKey = JSON.stringify([layer.text, layer.style.fontSize, layer.style.fontFamily, layer.style.lineHeight]);
      const cached = this._textBounds.get(layer.id);
      if (cached?.key === cacheKey) return cached;
      const ctx = this._context();
      this._font(ctx, layer.style);
      const lines = layer.text.split('\n'), lineHeight = layer.style.fontSize * layer.style.lineHeight;
      let minX = 0, minY = 0, maxX = 0, maxY = 0;
      lines.forEach((line, index) => {
        const metrics = ctx.measureText(line || ' '), baselineOffset = index * lineHeight;
        minX = Math.min(minX, -finite(metrics.actualBoundingBoxLeft));
        minY = Math.min(minY, baselineOffset - finite(metrics.actualBoundingBoxAscent));
        maxX = Math.max(maxX, metrics.width, finite(metrics.actualBoundingBoxRight));
        maxY = Math.max(maxY, baselineOffset + finite(metrics.actualBoundingBoxDescent, layer.style.fontSize));
      });
      const info = { key: cacheKey, lines, lineHeight, bounds: { x: minX, y: minY, width: maxX - minX, height: maxY - minY } };
      this._textBounds.set(layer.id, info);
      return info;
    }
    clearTextCache() { this._textBounds.clear(); this._emit('fontReady'); }
    boundsOfLayer(layerOrId) {
      const layer = this._find(layerOrId);
      if (!layer) return null;
      if (layer.type === 'text') { const b = this._textInfo(layer).bounds; return { x: layer.position.x + b.x, y: layer.position.y + b.y, width: b.width, height: b.height }; }
      if (layer.type === 'arrow') { const vertices = this.arrowPolygon(layer); return vertices.length ? extent(vertices) : extent([layer.start, layer.end]); }
      return extent(layer.type === 'pen' ? layer.points : [layer.start, layer.end], layer.style.lineWidth / 2);
    }
    _path(ctx, layer) {
      ctx.beginPath();
      if (layer.type === 'rectangle') ctx.rect(Math.min(layer.start.x, layer.end.x), Math.min(layer.start.y, layer.end.y), Math.abs(layer.end.x - layer.start.x), Math.abs(layer.end.y - layer.start.y));
      else if (layer.type === 'ellipse') {
        const rx = Math.abs(layer.end.x - layer.start.x) / 2, ry = Math.abs(layer.end.y - layer.start.y) / 2;
        if (rx < 0.01 || ry < 0.01) { ctx.moveTo(layer.start.x, layer.start.y); ctx.lineTo(layer.end.x, layer.end.y); }
        else ctx.ellipse((layer.start.x + layer.end.x) / 2, (layer.start.y + layer.end.y) / 2, rx, ry, 0, 0, Math.PI * 2);
      } else if (layer.type === 'pen') {
        ctx.moveTo(layer.points[0].x, layer.points[0].y);
        for (let index = 1; index < layer.points.length; index++) ctx.lineTo(layer.points[index].x, layer.points[index].y);
      } else if (layer.type === 'arrow') {
        const polygon = this.arrowPolygon(layer);
        if (polygon.length) { ctx.moveTo(polygon[0].x, polygon[0].y); for (const p of polygon.slice(1)) ctx.lineTo(p.x, p.y); ctx.closePath(); }
      }
    }
    _drawLayer(ctx, layer) {
      ctx.save();
      ctx.globalAlpha = 1;
      ctx.globalCompositeOperation = 'source-over';
      ctx.strokeStyle = layer.style.color;
      ctx.fillStyle = layer.style.color;
      ctx.lineWidth = layer.style.lineWidth;
      ctx.lineJoin = 'round';
      ctx.lineCap = 'round';
      ctx.setLineDash([]);
      if (layer.type === 'text') {
        this._font(ctx, layer.style);
        const info = this._textInfo(layer);
        info.lines.forEach((line, index) => ctx.fillText(line, layer.position.x, layer.position.y + index * info.lineHeight));
      } else if (layer.type === 'pen' && layer.points.length === 1) {
        ctx.beginPath(); ctx.arc(layer.points[0].x, layer.points[0].y, layer.style.lineWidth / 2, 0, Math.PI * 2); ctx.fill();
      } else {
        this._path(ctx, layer);
        if (layer.type === 'arrow') ctx.fill(); else ctx.stroke();
      }
      ctx.restore();
    }
    _textHit(layer, p, tolerance) {
      // Sample only the tiny area under the pointer. Even very long text needs no huge raster mask.
      const radius = Math.ceil(tolerance), size = radius * 2 + 3;
      if (!this._textHitCanvas) this._textHitCanvas = this._factory(size, size);
      const canvas = this._textHitCanvas;
      if (canvas.width !== size || canvas.height !== size) { canvas.width = size; canvas.height = size; }
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, size, size);
      const originX = Math.floor(p.x) - radius, originY = Math.floor(p.y) - radius;
      ctx.translate(-originX, -originY);
      this._drawLayer(ctx, layer);
      const pixels = ctx.getImageData(0, 0, size, size).data;
      for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
        if (Math.hypot(originX + x + 0.5 - p.x, originY + y + 0.5 - p.y) <= tolerance + 0.75 && pixels[(y * size + x) * 4 + 3] > 12) return true;
      }
      return false;
    }
    hitTest(position, tolerance = 3) {
      const p = point(position), margin = clamp(finite(tolerance, 3), 0, 64);
      for (let index = this.layers.length - 1; index >= 0; index--) {
        const layer = this.layers[index], bounds = this.boundsOfLayer(layer);
        if (p.x < bounds.x - margin || p.y < bounds.y - margin || p.x > bounds.x + bounds.width + margin || p.y > bounds.y + bounds.height + margin) continue;
        if (layer.type === 'text') { if (this._textHit(layer, p, margin)) return layer; }
        else if (layer.type === 'arrow') {
          const polygon = this.arrowPolygon(layer);
          if (polygonContains(p, polygon) || polygon.some((a, i) => segmentDistance(p, a, polygon[(i + 1) % polygon.length]) <= margin)) return layer;
        } else if (layer.type === 'pen') {
          const radius = layer.style.lineWidth / 2 + margin;
          if (layer.points.length === 1 ? distance(p, layer.points[0]) <= radius : layer.points.slice(1).some((b, i) => segmentDistance(p, layer.points[i], b) <= radius)) return layer;
        } else {
          const ctx = this._context();
          ctx.setTransform(1, 0, 0, 1, 0, 0);
          ctx.lineWidth = layer.style.lineWidth + margin * 2;
          ctx.lineJoin = 'round'; ctx.lineCap = 'round'; ctx.setLineDash([]);
          this._path(ctx, layer);
          if (ctx.isPointInStroke(p.x, p.y)) return layer;
        }
      }
      return null;
    }
    draw(ctx, options = {}) {
      ctx.save();
      // Clip markup to the source screenshot, including during viewport pan and zoom.
      ctx.beginPath(); ctx.rect(0, 0, this.width, this.height); ctx.clip();
      if (options.background !== false && this.image) ctx.drawImage(this.image, 0, 0, this.width, this.height);
      for (const layer of this.layers) this._drawLayer(ctx, layer);
      if (this._gesture) this._drawLayer(ctx, this._gesture.layer);
      if (options.selection !== false && this.selectedId) {
        const b = this.boundsOfLayer(this.selectedId);
        if (b) { ctx.strokeStyle = '#18a999'; ctx.lineWidth = 1; ctx.setLineDash([5, 3]); ctx.strokeRect(b.x - 4, b.y - 4, b.width + 8, b.height + 8); }
      }
      ctx.restore();
    }
    render(canvas, options = {}) {
      if (canvas.width !== this.width) canvas.width = this.width;
      if (canvas.height !== this.height) canvas.height = this.height;
      const ctx = canvas.getContext('2d');
      ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, canvas.width, canvas.height);
      this.draw(ctx, options);
      return canvas;
    }
    flattenPNG() {
      if (this._gesture || this._move) throw new Error('Finish the active stroke before exporting.');
      const canvas = this._factory(this.width, this.height);
      this.render(canvas, { selection: false, background: true });
      if (typeof canvas.toDataURL !== 'function') throw new Error('PNG export requires an HTML canvas or a canvasFactory supporting toDataURL.');
      return canvas.toDataURL('image/png');
    }
    serialize() { return { version: 1, width: this.width, height: this.height, layers: clone(this.layers) }; }
    toJSON() { return this.serialize(); }
    load(data) {
      if (!data || data.version !== 1 || !Array.isArray(data.layers)) throw new Error('Unsupported annotation document.');
      if (data.width !== this.width || data.height !== this.height) throw new Error('Annotation dimensions do not match the screenshot.');
      const knownIds = new Set();
      const layers = data.layers.map(source => {
        if (!source || (!TOOLS.has(source.type) && source.type !== 'text')) throw new Error('Invalid annotation layer.');
        const id = String(source.id || `stroke-${this._nextId++}`);
        if (knownIds.has(id)) throw new Error('Duplicate annotation layer id.');
        knownIds.add(id);
        const layer = { id, type: source.type, style: normalizeStyle(source.style, this.fontFamily) };
        if (source.type === 'text') { layer.position = point(source.position); layer.text = String(source.text || ''); }
        else if (source.type === 'pen') { if (!Array.isArray(source.points) || !source.points.length) throw new Error('Empty pen stroke.'); layer.points = source.points.map(point); }
        else { layer.start = point(source.start); layer.end = point(source.end); }
        return layer;
      });
      this.cancelGesture(false);
      this.layers = layers; this.selectedId = null; this._undo = []; this._redo = []; this._textBounds.clear();
      this._nextId = Math.max(this._nextId, ...layers.map(layer => /^stroke-\d+$/.test(layer.id) ? Number(layer.id.slice(7)) + 1 : 1));
      this._emit('loaded');
    }
  }
  return AnnotationEngine;
});
