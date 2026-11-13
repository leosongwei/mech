// The map in the corner: a 320 x 320 bake of the tile types, and the marks on
// top of it.
//
// The bake is the expensive half and it happens once, on `setMap` - one pixel
// per tile-type sample, nearest-neighbour, into an offscreen canvas the frame
// then blits as a 210 px square.  The marks come off the snapshot's *contacts*
// block rather than the entity table: the snapshot is a tier sphere around this
// pilot's eye and would have hidden everything past 2-6 km, while the map is the
// strategic picture of all 16 km and is what the contacts block is for.

import { MINIMAP, TAU } from './layout.js';
import { rgbTrunc, rgbaRound } from './canvas.js';
import { FACTION_COLORS } from '../scene/palette.js';

const BAKE = 320;

// The tiles of the field as a picture: `map.tiles` is one byte per 16 m cell, so
// the bake is a nearest sample of it and the whole map fits an offscreen canvas
// a few hundred pixels square.
export function bakeMap(map) {
  const N = BAKE;
  const c = document.createElement('canvas');
  c.width = N;
  c.height = N;
  const g = c.getContext('2d');
  const img = g.createImageData(N, N);
  const sx = map.w / N;
  const sz = map.h / N;
  const palette = {
    0: [64, 96, 48], 1: [70, 68, 64], 2: [134, 128, 118],
    3: [40, 74, 38], 4: [104, 104, 108], 5: [92, 94, 100], 6: [92, 94, 100],
    7: [26, 52, 26],
  };
  for (let j = 0; j < N; j++) {
    for (let i = 0; i < N; i++) {
      const x = Math.min(map.w - 1, (i * sx) | 0);
      const z = Math.min(map.h - 1, (j * sz) | 0);
      const t = map.tiles[z * map.w + x];
      const p = palette[t] || palette[0];
      const o = (j * N + i) * 4;
      img.data[o] = p[0];
      img.data[o + 1] = p[1];
      img.data[o + 2] = p[2];
      img.data[o + 3] = 255;
    }
  }
  g.putImageData(img, 0, 0);
  return { canvas: c, size: { w: map.w, h: map.h, tile: map.tile } };
}

export function minimap(ctx, s, baked) {
  if (!baked) return;
  const size = MINIMAP.size;
  const x0 = MINIMAP.x;
  const y0 = MINIMAP.y;
  ctx.save();
  ctx.globalAlpha = 0.94;
  ctx.drawImage(baked.canvas, x0, y0, size, size);
  ctx.strokeStyle = 'rgba(160,220,190,0.5)';
  ctx.lineWidth = 1;
  ctx.strokeRect(x0, y0, size, size);
  const S = baked.size;
  const kx = size / (S.w * S.tile);
  const kz = size / (S.h * S.tile);

  // towns
  for (const t of s.towns || []) {
    const tx = x0 + t.x * kx;
    const tz = y0 + t.z * kz;
    const f = t.owner;
    ctx.fillStyle = f < 0 ? 'rgba(200,180,255,0.75)' : rgbaRound(f, 0.95);
    ctx.fillRect(tx - 2.5, tz - 2.5, 5, 5);
    if (t.progress > 0.02) {
      ctx.strokeStyle = 'rgba(255,255,255,0.8)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      // the town's capture arc keeps the constant the old file gave it, a hair
      // short of a whole turn (`TAU` minus 0.0002): the last frame of a capture
      // is one pixel of gap, and it is the frame the town changes hands on
      ctx.arc(tx, tz, 6, -1.57, -1.57 + 6.283 * Math.min(1, t.progress));
      ctx.stroke();
    }
  }
  // units: the map shows the whole field, so it reads the contacts block (the
  // snapshot itself is a tier sphere around this pilot's eye and would have
  // hidden everything past 2-6 km)
  for (const e of s.contacts || []) {
    if (e.etype === 5) {
      ctx.fillStyle = '#fff';
      ctx.fillRect(x0 + e.x * kx - 4, y0 + e.z * kz - 4, 8, 8);
      continue;
    }
    if (e.etype === 6) continue;
    const px = x0 + e.x * kx;
    const pz = y0 + e.z * kz;
    const sz = e.etype === 4 ? 3 : 1.6;
    ctx.fillStyle = FACTION_COLORS[e.faction] ? rgbTrunc(e.faction) : DEFAULT_UNIT;
    ctx.fillRect(px - sz, pz - sz, sz * 2, sz * 2);
  }
  // own
  if (s.own) {
    const px = x0 + s.own.x * kx;
    const pz = y0 + s.own.z * kz;
    const yaw = s.own.yaw;
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(px, pz);
    ctx.lineTo(px + Math.cos(yaw) * 14, pz + Math.sin(yaw) * 14);
    ctx.stroke();
    ctx.fillStyle = '#7ce0a0';
    ctx.beginPath();
    ctx.arc(px, pz, 3, 0, TAU);
    ctx.fill();
  }
  ctx.restore();
}

// a unit of no side at all (`FACTION_COLORS[e.faction] || [0.8, 0.8, 0.8]` in
// the old table - truncated channels, like the marks that do have one)
const DEFAULT_UNIT = 'rgb(204,204,204)';
