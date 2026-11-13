// Roads are laid tile by tile out of the same pieces the server stores: one
// quad per cardinal bit (so a straight is two of them, an elbow two adjacent
// ones, a crossroads four) and one 45 degree band per diagonal bit.  Every
// vertex sits on the flat facet the terrain draws under it, so the asphalt hugs
// the terraces instead of floating over them, and the seam between two road
// tiles is a tile border - which is the whole OpenTTD look.
//
// The geometry is built once per map and drawn by the terrain pass, on top of
// the near ground.

import { ROAD_N, ROAD_E, ROAD_S, ROAD_W, ROAD_D_WS, ROAD_D_NE, treeHash } from '../map/tiles.js';
import { ROAD_VS, ROAD_FS } from './shaders/road.js';

const ROAD_UNIFORMS = ['uViewProj', 'uCam', 'uSun', 'uFogColor', 'uFogDensity', 'uFar'];

export class Roads {
  constructor(gpu) {
    this.gpu = gpu;
    this.program = gpu.program('road', ROAD_VS, ROAD_FS, ROAD_UNIFORMS);
    this.aPos = gpu.attrib(this.program.prog, 'aPos');
    this.aCol = gpu.attrib(this.program.prog, 'aCol');
    this.vbo = null;
    this.count = 0;
  }

  build(map) {
    const gl = this.gpu.gl;
    this.gpu.release(this.vbo);
    this.vbo = null;
    this.count = 0;
    const roads = map.roads;
    if (!roads) return;
    const T = map.tile;
    const HW = 5.0;                     // asphalt half width: a 10 m road
    const LO = T * 0.5 - HW, HI = T * 0.5 + HW;
    const D = HW * Math.SQRT2;          // reach of a 45 degree band, along an axis
    const LIFT = 0.06;                  // asphalt sits just proud of the grass
    const ASPHALT = [0.235, 0.230, 0.220];
    const PAINT = [0.60, 0.585, 0.505];

    // Height of the flat triangle the terrain patch draws under (x, z).
    // `map.heightAt` *is* that rule (both hosts and this pass solve the same
    // two triangles), so a road vertex lands on the plane the patch draws.
    const facet = (x, z) => map.heightAt(x, z);

    const out = [];
    const vert = (x, z, col, lift) => out.push(x, facet(x, z) + lift, z,
      col[0], col[1], col[2]);

    // A convex polygon in world metres, cut along the tile's own terrain
    // diagonal before it is triangulated.  Half of it is then flat on one
    // terrain triangle and half on the other; without the cut a road triangle
    // spans the crease between the two panels and dips below the ground,
    // which is what showed up as flickering slivers along the verges.
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

    this.count = out.length / 6;
    if (!this.count) return;
    this.vbo = this.gpu.buffer(gl.ARRAY_BUFFER, new Float32Array(out), gl.STATIC_DRAW);
  }

  draw(R, cam) {
    if (!this.count) return;
    const gl = this.gpu.gl;
    const gpu = this.gpu;
    const u = this.program.u;
    gpu.useProgram(this.program);
    gl.uniformMatrix4fv(u.uViewProj, false, cam.viewProj);
    gl.uniform3fv(u.uCam, cam.eye);
    gl.uniform3fv(u.uSun, R.sun);
    gl.uniform3fv(u.uFogColor, R.fogColor);
    gl.uniform1f(u.uFogDensity, R.fogDensity);
    gl.uniform1f(u.uFar, R.far);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.vbo);
    gl.enableVertexAttribArray(this.aPos);
    gl.vertexAttribPointer(this.aPos, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(this.aCol);
    gl.vertexAttribPointer(this.aCol, 3, gl.FLOAT, false, 24, 12);
    gl.drawArrays(gl.TRIANGLES, 0, this.count);
    gl.disableVertexAttribArray(this.aPos);
    gl.disableVertexAttribArray(this.aCol);
    R.stats.draws++;
  }

  dispose() {
    this.gpu.release(this.vbo);
    this.vbo = null;
    this.count = 0;
  }
}

// The asphalt is drawn by the terrain pass (it is part of the same state and the
// same moment of the frame), so it has no row of its own in `PASSES`.
export const roads = {
  name: 'roads',
  draw(R, cam) { R.roads.draw(R, cam); },
};
