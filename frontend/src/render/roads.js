// Roads: laid tile by tile out of the same pieces the server stores - one quad
// per cardinal bit (so a straight is two of them, an elbow two adjacent ones, a
// crossroads four) and one 45 degree band per diagonal bit. Every vertex sits on
// the flat facet the ground draws under it, so the asphalt hugs the terraces
// instead of floating over them, and the seam between two road tiles is a tile
// border - which is the whole OpenTTD look.
//
// The geometry is built once per map and drawn after the near ground.

import { BufferGeometry, BufferAttribute, Mesh } from 'three';

import { ROAD_N, ROAD_E, ROAD_S, ROAD_W, ROAD_D_WS, ROAD_D_NE, treeHash } from '../map/tiles.js';
import { roadMaterial } from './materials.js';

export class Roads {
  constructor(U, map, order) {
    this.mesh = new Mesh(this._geometry(map), roadMaterial(U));
    this.mesh.frustumCulled = false;
    this.mesh.matrixAutoUpdate = false;
    this.mesh.renderOrder = order;
    this._total = this.mesh.geometry.attributes.position.count;
    this._count = this._total;
  }

  // How many vertices of the asphalt are drawn (`g.renderer.roads.count`, the
  // screenshot door: write 0 to take the roads out of the picture). The geometry
  // is built once per map, so this is a draw range rather than a rebuild.
  get count() {
    return this._count;
  }

  set count(n) {
    this._count = Math.max(0, Math.min(this._total, n | 0));
    this.mesh.visible = this._count > 0;
    this.mesh.geometry.setDrawRange(0, this._count);
  }

  _geometry(map) {
    const roads = map.roads;
    const out = [];
    const T = map.tile;
    const HW = 5.0;                     // asphalt half width: a 10 m road
    const LO = T * 0.5 - HW, HI = T * 0.5 + HW;
    const D = HW * Math.SQRT2;          // reach of a 45 degree band, along an axis
    const LIFT = 0.06;                  // asphalt sits just proud of the grass
    const ASPHALT = [0.235, 0.230, 0.220];
    const PAINT = [0.60, 0.585, 0.505];

    // Height of the flat triangle the ground draws under (x, z). `map.heightAt`
    // *is* that rule (both hosts and this pass solve the same two triangles), so
    // a road vertex lands on the plane the patch draws.
    const facet = (x, z) => map.heightAt(x, z);

    const vert = (x, z, col, lift) => out.push(x, facet(x, z) + lift, z,
      col[0], col[1], col[2]);

    // A convex polygon in world metres, cut along the tile's own terrain
    // diagonal before it is triangulated. Half of it is then flat on one terrain
    // triangle and half on the other; without the cut a road triangle spans the
    // crease between the two panels and dips below the ground, which is what
    // showed up as flickering slivers along the verges.
    const clipHalf = (pts, side, ox, oz) => {
      const outPts = [];
      const f = (p) => side * (T - (p[0] - ox) - (p[1] - oz));
      for (let k = 0; k < pts.length; k++) {
        const a = pts[k], b = pts[(k + 1) % pts.length];
        const fa = f(a), fb = f(b);
        if (fa >= 0) outPts.push(a);
        if ((fa > 0 && fb < 0) || (fa < 0 && fb > 0)) {
          const t = fa / (fa - fb);
          outPts.push([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t]);
        }
      }
      return outPts;
    };
    const poly = (pts, ox, oz, col, lift) => {
      for (const side of [1, -1]) {
        const q = clipHalf(pts, side, ox, oz);
        for (let k = 1; k + 1 < q.length; k++) {
          vert(q[0][0], q[0][1], col, lift);
          vert(q[k][0], q[k][1], col, lift);
          vert(q[k + 1][0], q[k + 1][1], col, lift);
        }
      }
    };

    if (roads) {
      for (let j = 0; j < map.h; j++) {
        for (let i = 0; i < map.w; i++) {
          const bits = roads[j * map.w + i];
          if (!bits) continue;
          const x0 = i * T, z0 = j * T;
          const m = T * 0.5;
          // a little per tile grit, so a long straight is not one flat slab
          const g = 0.92 + treeHash(i, j) * 0.16;
          const col = [ASPHALT[0] * g, ASPHALT[1] * g, ASPHALT[2] * g];

          // ---- the cardinal halves ------------------------------------
          if (bits & ROAD_N) poly([[x0 + LO, z0], [x0 + HI, z0], [x0 + HI, z0 + m], [x0 + LO, z0 + m]], x0, z0, col, LIFT);
          if (bits & ROAD_S) poly([[x0 + LO, z0 + m], [x0 + HI, z0 + m], [x0 + HI, z0 + T], [x0 + LO, z0 + T]], x0, z0, col, LIFT);
          if (bits & ROAD_W) poly([[x0, z0 + LO], [x0 + m, z0 + LO], [x0 + m, z0 + HI], [x0, z0 + HI]], x0, z0, col, LIFT);
          if (bits & ROAD_E) poly([[x0 + m, z0 + LO], [x0 + T, z0 + LO], [x0 + T, z0 + HI], [x0 + m, z0 + HI]], x0, z0, col, LIFT);

          // ---- the 45 degree bands ------------------------------------
          // Each one is the band a corner cut by the tile: it runs from the
          // middle of one edge to the middle of the next, so the tile before it
          // and the tile after it join it at full width.
          if (bits & ROAD_D_WS) {
            poly([[x0, z0 + m - D], [x0, z0 + m + D],
              [x0 + m - D, z0 + T], [x0 + m + D, z0 + T]], x0, z0, col, LIFT);
          }
          if (bits & ROAD_D_NE) {
            poly([[x0 + m - D, z0], [x0 + m + D, z0],
              [x0 + T, z0 + m - D], [x0 + T, z0 + m + D]], x0, z0, col, LIFT);
          }

          // ---- centre line --------------------------------------------
          // only where the road runs straight through or across the tile: a
          // junction carries no paint, the way it does not in OpenTTD
          const dash = LIFT + 0.03;
          if (bits === (ROAD_N | ROAD_S)) {
            poly([[x0 + m - 0.3, z0 + 4.8], [x0 + m + 0.3, z0 + 4.8],
              [x0 + m + 0.3, z0 + 11.2], [x0 + m - 0.3, z0 + 11.2]], x0, z0, PAINT, dash);
          } else if (bits === (ROAD_E | ROAD_W)) {
            poly([[x0 + 4.8, z0 + m - 0.3], [x0 + 4.8, z0 + m + 0.3],
              [x0 + 11.2, z0 + m + 0.3], [x0 + 11.2, z0 + m - 0.3]], x0, z0, PAINT, dash);
          } else if (bits === ROAD_D_WS) {
            // down the middle of the band, from (0, m) to (m, T)
            const a = 0.3 / Math.SQRT2;
            poly([[x0 + 2 + a, z0 + 10 - a], [x0 + 2 - a, z0 + 10 + a],
              [x0 + 6 - a, z0 + 14 + a], [x0 + 6 + a, z0 + 14 - a]], x0, z0, PAINT, dash);
          } else if (bits === ROAD_D_NE) {
            // down the middle of the band, from (m, 0) to (T, m)
            const a = 0.3 / Math.SQRT2;
            poly([[x0 + 10 + a, z0 + 2 + a], [x0 + 10 - a, z0 + 2 - a],
              [x0 + 14 - a, z0 + 6 - a], [x0 + 14 + a, z0 + 6 + a]], x0, z0, PAINT, dash);
          }
        }
      }
    }

    // position and colour interleave in `out` (six floats a vertex): three wants
    // them as two attributes, so they are split here once, at build time
    const n = out.length / 6;
    const position = new Float32Array(n * 3);
    const color = new Float32Array(n * 3);
    for (let v = 0; v < n; v++) {
      position[v * 3] = out[v * 6];
      position[v * 3 + 1] = out[v * 6 + 1];
      position[v * 3 + 2] = out[v * 6 + 2];
      color[v * 3] = out[v * 6 + 3];
      color[v * 3 + 1] = out[v * 6 + 4];
      color[v * 3 + 2] = out[v * 6 + 5];
    }
    const geo = new BufferGeometry();
    geo.setAttribute('position', new BufferAttribute(position, 3));
    geo.setAttribute('color', new BufferAttribute(color, 3));
    return geo;
  }

  dispose() {
    this.mesh.geometry.dispose();
    this.mesh.material.dispose();
  }
}
