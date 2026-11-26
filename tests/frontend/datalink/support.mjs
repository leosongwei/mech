// Small, deterministic app fixtures for the datalink client tests.  They use the
// real sight and lead code with a flat field; no browser, WebGL or server exists.

export function flatMap() {
  const tiles = new Uint8Array(100);
  const walls = new Uint8Array(100);
  return {
    tile: 100, w: 10, h: 10, tiles, walls,
    heightAt() { return 0; },
    canopiesAt() { return []; },
  };
}

export function ownEntity(extra = {}) {
  return {
    id: 1, type: 3, faction: 0, x: 0, y: 0, z: 0,
    px: 0, py: 0, pz: 0, vx: 0, vy: 0, vz: 0,
    hp: [100, 0, 100, 100], weapon: [0, 0],
    ...extra,
  };
}

export function vehicle(id, faction, x, extra = {}) {
  return {
    id, type: 3, faction, x, y: 0, z: 0,
    px: x, py: 0, pz: 0, vx: 0, vy: 0, vz: 0,
    mvx: 0, mvy: 0, mvz: 0, link: false,
    ...extra,
  };
}

export function projectile(id, faction, x, extra = {}) {
  return {
    id, kind: 2, wid: 9, faction, x, y: 20, z: 0,
    vx: 0, vy: 0, vz: 0, link: false,
    ...extra,
  };
}

export function smokeAt(x, y = 2, z = 0, r = 30, ry = r) {
  return { x, y, z, rx: r, ry, rz: r, canister: false };
}

export function testApp(entities = [], projectiles = []) {
  const own = ownEntity();
  const app = {
    net: {
      seq: 1, tick: 30,
      entities: new Map(entities.map((e) => [e.id, e])),
      projectiles, smoke: [], meta: { lock_target: -1, locks: [] },
    },
    map: flatMap(),
    init: { zero: { range: 4000, max_elev: 1.05 } },
    defs: {
      mech: { eye: 5 },
      units: { 3: { height: 6, radius: 2 }, 4: { height: 3, radius: 2 } },
      weapons: { 9: { kind: 'saclos', hit_r: 1 } },
    },
    aim: {
      sightEye: (o, pose = o) => [pose.x, pose.y + 5, pose.z],
      fireLag: () => 0,
      firePose: (o) => o,
      muzzle: (o) => [o.x, o.y + 5, o.z],
      fov: () => 1.05,
      ringPx: () => 7,
    },
    hud: { w: 800, h: 600, vp: null, _project: () => [400, 300] },
    input: { alt: false, uiOpen: false },
    rwr: { threats: [] },
    aimAssist: false,
    assistRing: 4,
    zeroRange: 1000,
    leads: [],
    aimSnap: null,
  };
  app.sightEye = (o) => [o.x, o.y + 5, o.z];
  return { app, own };
}
