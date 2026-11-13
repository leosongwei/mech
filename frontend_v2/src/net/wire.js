// The frame envelope: the magics, the strides, the clock block every binary frame
// starts with, and the round events as rows.
//
// Nothing here knows about the match.  A binary frame is `CLOCK` bytes of clock
// plus eight u16 block counts (`HEADER` in all), then the five unit classes, the
// projectiles, the smoke and - every eighth tick - the minimap's contacts.  A
// clock block on its own is a whole frame (`MAGIC_PING`): what a congested pilot
// gets instead of a picture.  These numbers are the ones in `Net/Netcode.cs`.

export const MAGIC_FX = 0x4d454346;    // "MECF": the round events, as 24 B rows
export const MAGIC_PING = 0x4d454350;  // "MECP": the clock block alone, no rows at all
export const FX_STRIDE = 24;           // kind, wid, id, x, y, z, r, flags (+3 pad)
export const CLOCK = 16;               // magic, tick, echo, tps, loss - both frames
export const HEADER = 32;              // clock + eight u16 counts
export const PROJ_STRIDE = 32;
export const SMOKE_STRIDE = 32;
export const CONTACT_STRIDE = 6;       // minimap point: x, z, faction, etype
export const CONTACT_QUANT = 8;        // metres per unit of the u16

// One `DataView` for the client, re-pointed at the frame being read instead of
// built per frame.  A frame is read in passes - the clock, then the fx rows or
// the rows and the four blocks behind them - and each pass used to construct a
// view of the same bytes.  `setBuffer` is idempotent, so whoever holds the
// frame first pays for the view and the rest read through it.
let _view = new DataView(new ArrayBuffer(0));
let _src = null;

export function setBuffer(buf) {
  if (buf !== _src) {
    _src = buf;
    _view = new DataView(buf);
  }
  return _view;
}

export function isFxFrame(buf) {
  return buf.byteLength >= 6 && setBuffer(buf).getUint32(0, true) === MAGIC_FX;
}

// The clock block: 16 B, and the whole of a frame the server sent to hold a
// congested connection.
export function isPingFrame(buf) {
  return buf.byteLength >= CLOCK && setBuffer(buf).getUint32(0, true) === MAGIC_PING;
}

// The clock block every binary frame starts with: tick, the echo of our own ack,
// the server's tick rate and this pilot's loss.  `at` is where the block starts,
// which is 0 in every frame the server builds.
export function readClock(dv, at = 0) {
  return {
    tick: dv.getUint32(at + 4, true),
    echo: dv.getUint32(at + 8, true),
    tps: dv.getUint16(at + 12, true) / 100,
    loss: dv.getUint16(at + 14, true) / 100,
  };
}

// A round leaving or landing, off the binary fx frame: 24 B a row, twelve hundred
// bytes for a whole gatling storm where JSON paid 68 B a bang.  The events are
// rebuilt in the shape `game._fx` already speaks, so nothing downstream knows the
// difference.
export function decodeFx(buf) {
  const dv = setBuffer(buf);
  const n = dv.getUint16(4, true);
  const evs = new Array(n);
  let off = 6;
  for (let k = 0; k < n; k++) {
    const kind = dv.getUint8(off);
    const wid = dv.getUint8(off + 1);
    const id = dv.getUint16(off + 2, true);
    const x = dv.getFloat32(off + 4, true);
    const y = dv.getFloat32(off + 8, true);
    const z = dv.getFloat32(off + 12, true);
    const r = dv.getFloat32(off + 16, true);
    const fl = dv.getUint8(off + 20);
    evs[k] = kind === 1
      ? { t: 'fx', k: 'shot', x, y, z, wid, eid: id, arm: (fl >> 3) & 3 }
      : { t: 'fx', k: 'boom', x, y, z, wid, proj: id, r,
          big: fl & 1, blast: (fl >> 1) & 1, small: (fl >> 2) & 1 };
    off += FX_STRIDE;
  }
  return evs;
}
