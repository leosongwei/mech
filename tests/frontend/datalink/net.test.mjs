import test from 'node:test';
import assert from 'node:assert/strict';

import { Client } from '../../../frontend/src/net/client.js';
import { CLASSES, PROJ_TARGET_LINK, ROWS, TARGET_LINK } from '../../../frontend/src/net/rows.js';
import { View } from '../../../frontend/src/net/view.js';
import { CLOCK, HEADER, PROJ_STRIDE } from '../../../frontend/src/net/wire.js';

const SIZE = { u8: 1, u16: 2, f32: 4 };

function fieldOffset(cls, name) {
  let off = 0;
  for (const f of CLASSES[cls].fields) {
    if (f[0] === name) return off;
    off += SIZE[f[1]] * (f[2] || 1);
  }
  throw new Error(`missing field ${name}`);
}

function writeRow(dv, off, cls, row) {
  for (const f of CLASSES[cls].fields) {
    const size = SIZE[f[1]];
    const count = f[2] || 1;
    for (let k = 0; k < count; k++) {
      const value = row[f[0]];
      const v = Array.isArray(value) ? value[k] : value;
      if (v === undefined) continue;
      const at = off + k * size;
      if (f[1] === 'u8') dv.setUint8(at, v);
      else if (f[1] === 'u16') dv.setUint16(at, v, true);
      else dv.setFloat32(at, v, true);
    }
    off += size * count;
  }
  return off;
}

function snapshot({ mech = [], apc = [], projectiles = [] } = {}) {
  const bytes = HEADER + mech.length * ROWS[0] + apc.length * ROWS[1]
    + projectiles.length * PROJ_STRIDE;
  const buf = new ArrayBuffer(bytes);
  const dv = new DataView(buf);
  dv.setUint32(0, 0x4843454d, true); // MECH
  dv.setUint32(4, 30, true);
  dv.setUint16(CLOCK, mech.length, true);
  dv.setUint16(CLOCK + 2, apc.length, true);
  dv.setUint16(CLOCK + 10, projectiles.length, true);
  let off = HEADER;
  for (const row of mech) off = writeRow(dv, off, 0, { etype: 3, faction: 1, ...row });
  for (const row of apc) off = writeRow(dv, off, 1, { etype: 4, faction: 1, ...row });
  for (const p of projectiles) {
    dv.setUint16(off, p.id, true);
    dv.setUint8(off + 2, p.kind ?? 2);
    dv.setUint8(off + 3, p.wid ?? 9);
    dv.setUint8(off + 4, p.faction ?? 1);
    dv.setUint8(off + 5, p.flags ?? 0);
    dv.setUint8(off + 6, p.pad6 ?? 0);
    dv.setUint8(off + 7, p.pad7 ?? 0);
    dv.setFloat32(off + 8, p.x ?? 0, true);
    dv.setFloat32(off + 12, p.y ?? 0, true);
    dv.setFloat32(off + 16, p.z ?? 0, true);
    dv.setFloat32(off + 20, p.vx ?? 0, true);
    dv.setFloat32(off + 24, p.vy ?? 0, true);
    dv.setFloat32(off + 28, p.vz ?? 0, true);
    off += PROJ_STRIDE;
  }
  assert.equal(off, buf.byteLength);
  return buf;
}

test('row widths stay fixed and entity link follows the init capability', () => {
  assert.deepEqual(ROWS, [76, 48, 30, 25, 20]);
  assert.equal(PROJ_STRIDE, 32);
  const view = new View();
  const frame = snapshot({
    mech: [{ id: 10, flags: TARGET_LINK }],
    apc: [{ id: 11, flags: TARGET_LINK }],
  });
  view.absorb(frame);
  assert.equal(view.entities.get(10).link, false);
  assert.equal(view.entities.get(11).link, false);

  view.datalink = true;
  view.absorb(frame);
  assert.equal(view.entities.get(10).link, true);
  assert.equal(view.entities.get(11).link, true);

  view.absorb(snapshot({ mech: [{ id: 10, flags: 0 }] }));
  assert.equal(view.entities.get(10).link, false, 'a cleared bit must not inherit an old link');
});

test('projectile +5 bit0 decodes without touching position, velocity or padding', () => {
  assert.equal(PROJ_TARGET_LINK, 1);
  const frame = snapshot({
    projectiles: [
      { id: 20, flags: 1, pad6: 0xff, pad7: 0x80,
        x: 10, y: 20, z: 30, vx: 40, vy: 50, vz: 60 },
      { id: 21, flags: 0xfe, x: 70, y: 80, z: 90 },
    ],
  });
  const view = new View();
  view.absorb(frame);
  assert.equal(view.projectiles[0].link, false);
  view.datalink = true;
  view.absorb(frame);
  assert.deepEqual(view.projectiles.map((p) => p.link), [true, false]);
  assert.deepEqual(
    [view.projectiles[0].x, view.projectiles[0].y, view.projectiles[0].z,
      view.projectiles[0].vx, view.projectiles[0].vy, view.projectiles[0].vz],
    [10, 20, 30, 40, 50, 60]);
  assert.deepEqual(
    [view.projectiles[1].x, view.projectiles[1].y, view.projectiles[1].z],
    [70, 80, 90]);
});

test('init datalink is optional and only the value 1 enables decoding', async () => {
  const original = globalThis.WebSocket;
  class FakeSocket {
    constructor() { this.readyState = 0; }
  }
  globalThis.WebSocket = FakeSocket;
  try {
    for (const [payload, expected] of [[{ t: 'init' }, false], [{ t: 'init', datalink: 1 }, true]]) {
      const client = new Client('ws://test');
      const connected = client.connect();
      client.ws.onmessage({ data: JSON.stringify(payload) });
      await connected;
      assert.equal(client.view.datalink, expected);
    }
  } finally {
    globalThis.WebSocket = original;
  }
});
