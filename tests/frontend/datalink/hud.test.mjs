import test from 'node:test';
import assert from 'node:assert/strict';

import { ARM_COLORS, ENEMY_TARGET, FRIENDLY_TARGET, MISSILE_RING } from '../../../frontend/src/hud/layout.js';
import { leads, targetStyle } from '../../../frontend/src/hud/leads.js';

class FakeCanvas {
  constructor() {
    this.strokeStyle = '#000000';
    this.fillStyle = '#000000';
    this.lineWidth = 1;
    this.globalAlpha = 1;
    this.dash = [];
    this.stack = [];
    this.path = [];
    this.strokes = [];
  }
  save() {
    this.stack.push({ strokeStyle: this.strokeStyle, fillStyle: this.fillStyle,
      lineWidth: this.lineWidth, globalAlpha: this.globalAlpha, dash: [...this.dash] });
  }
  restore() {
    const state = this.stack.pop();
    assert.ok(state, 'unbalanced canvas restore');
    Object.assign(this, state);
  }
  setLineDash(dash) { this.dash = [...dash]; }
  beginPath() { this.path = []; }
  arc(x, y, r, a0, a1) { this.path.push({ shape: 'arc', x, y, r, a0, a1 }); }
  moveTo(x, y) { this.path.push({ shape: 'move', x, y }); }
  lineTo(x, y) { this.path.push({ shape: 'line', x, y }); }
  strokeRect(x, y, w, h) {
    this.strokes.push({ shape: 'rect', x, y, w, h, color: this.strokeStyle,
      dash: [...this.dash], alpha: this.globalAlpha });
  }
  stroke() {
    if (!this.path.length) return;
    this.strokes.push({ shape: this.path.length === 1 && this.path[0].shape === 'arc'
      ? 'circle' : 'path', path: this.path.map((p) => ({ ...p })), color: this.strokeStyle,
      dash: [...this.dash], alpha: this.globalAlpha });
  }
  fill() { }
}

function target(id, kind, faction, source, x, marks = []) {
  return { id, kind, faction, source, x, y: 100, hw: 10, hh: 10, marks };
}

test('target style uses the exact faction colours and solid/dashed sources', () => {
  assert.deepEqual(targetStyle({ faction: 0, source: 'direct' }, 0),
    { color: FRIENDLY_TARGET, dash: [] });
  assert.deepEqual(targetStyle({ faction: 1, source: 'direct' }, 0),
    { color: ENEMY_TARGET, dash: [] });
  assert.deepEqual(targetStyle({ faction: 1, source: 'datalink' }, 0),
    { color: ENEMY_TARGET, dash: [3, 3] });
  assert.equal(FRIENDLY_TARGET, 'rgb(30,30,255)');
  assert.equal(ENEMY_TARGET, 'rgb(30,255,30)');
});

test('fake Canvas draws blue friendly boxes and green enemy boxes with source dashes', () => {
  const ctx = new FakeCanvas();
  ctx.setLineDash([9, 9]);
  leads(ctx, {
    playerFaction: 0,
    leads: [
      target(1, 'vehicle', 0, 'direct', 50),
      target(2, 'vehicle', 1, 'direct', 150),
      target(3, 'vehicle', 1, 'datalink', 250),
    ],
  });
  const rects = ctx.strokes.filter((s) => s.shape === 'rect');
  assert.deepEqual(rects.map((r) => [r.color, r.dash]), [
    [FRIENDLY_TARGET, []], [ENEMY_TARGET, []], [ENEMY_TARGET, [3, 3]],
  ]);
  assert.deepEqual(ctx.dash, [9, 9], 'target dashes must not leak out of leads()');
  assert.equal(ctx.stack.length, 0);
});

test('guided rounds are friendly blue or enemy green circles, direct or linked', () => {
  const ctx = new FakeCanvas();
  leads(ctx, {
    playerFaction: 0,
    leads: [
      target(4, 'missile', 0, 'direct', 50),
      target(5, 'missile', 1, 'direct', 150),
      target(6, 'missile', 1, 'datalink', 250),
    ],
  });
  const circles = ctx.strokes.filter((s) => s.shape === 'circle');
  assert.equal(circles.length, 3);
  assert.ok(circles.every((c) => c.path[0].r === MISSILE_RING));
  assert.deepEqual(circles.map((c) => [c.color, c.dash]), [
    [FRIENDLY_TARGET, []], [ENEMY_TARGET, []], [ENEMY_TARGET, [3, 3]],
  ]);
});

test('a datalink target outline does not dash its arm-coloured pre-aim ring', () => {
  const ctx = new FakeCanvas();
  ctx.setLineDash([9, 9]);
  leads(ctx, {
    playerFaction: 0,
    leads: [target(7, 'vehicle', 1, 'datalink', 100, [
      { arm: 0, x: 140, y: 100, ring: 7 },
    ])],
  });
  assert.ok(ctx.strokes.some((s) => s.shape === 'rect'
    && s.color === ENEMY_TARGET && s.dash.join() === '3,3'));
  const ring = ctx.strokes.find((s) => s.shape === 'circle' && s.path[0].r === 7);
  assert.ok(ring, 'pre-aim ring was not drawn');
  assert.equal(ring.color, ARM_COLORS[0]);
  assert.deepEqual(ring.dash, []);
  assert.deepEqual(ctx.dash, [9, 9], 'pre-aim drawing must restore the caller dash');
});
