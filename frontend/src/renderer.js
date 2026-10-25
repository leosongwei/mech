// WebGL2 renderer.
//
// * terrain: one static radial LOD grid, heights sampled from a R32F texture in
//   the vertex shader (the 16 km map never touches the CPU).
// * props/units: instanced primitives (box / sphere / cone / cylinder),
//   submitted through an explicit draw command list.  WebGL2 has no real
//   indirect draw, so the list is handed to WEBGL_multi_draw (one call per
//   mesh) and falls back to per-command instanced draws when the extension is
//   unavailable.
// * depth: logarithmic (all shaders write gl_FragDepth), so a 1 m..30 km view
//   range works without z-fighting.

import { compose, mat4, multiply, perspective, lookAt } from './math.js';
import { buildMeshes, MESH_BOX, MESH_SPHERE, MESH_CONE, MESH_CYL, MESH_ROCK, MESH_PYRAMID, MESH_SMOKE } from './meshes.js';

const SKY_VS = `#version 300 es
in vec2 aPos;
out vec2 vPos;
void main(){ vPos = aPos; gl_Position = vec4(aPos, 1.0, 1.0); }`;

const SKY_FS = `#version 300 es
precision highp float;
in vec2 vPos;
uniform mat4 uInvVP;
uniform vec3 uCamPos;
uniform vec3 uSun;
out vec4 fragColor;
void main(){
  vec4 p = uInvVP * vec4(vPos, 1.0, 1.0);
  vec3 dir = normalize(p.xyz / p.w - uCamPos);
  float h = max(0.0, dir.y);
  vec3 sky = mix(vec3(0.66,0.71,0.75), vec3(0.20,0.34,0.56), pow(h, 0.55));
  float glow = pow(max(0.0, dot(dir, uSun)), 5.0);
  sky += vec3(0.45,0.36,0.22) * glow * 0.35;
  float sun = pow(max(0.0, dot(dir, uSun)), 900.0);
  sky += vec3(1.0,0.95,0.82) * sun;
  fragColor = vec4(sky, 1.0);
}`;

// Far ground: one static radial LOD grid around the eye.  Heights are sampled
// in the vertex shader, so the 16 km map never touches the CPU.
const TERRAIN_VS = `#version 300 es
precision highp float;
precision highp sampler2D;
in vec2 aCell;
uniform mat4 uViewProj;
uniform sampler2D uHeight;
uniform vec2 uMapSize;
uniform float uTile;
uniform float uG;
uniform vec3 uCam;
out vec3 vWP;
out float vViewZ;
out float vDist;

float texH(vec2 p){
  // clamp the sample point, not just the texel index: the outer LOD rings
  // reach well past the map edge, and an unbounded fraction here extrapolates
  // the edge texels into kilometre-high spikes
  p = clamp(p, vec2(0.0), vec2(uMapSize) - 1.001);
  ivec2 i = ivec2(floor(p));
  i = clamp(i, ivec2(0), ivec2(uMapSize) - 2);
  vec2 f = p - vec2(i);
  float h00 = texelFetch(uHeight, i, 0).r;
  float h10 = texelFetch(uHeight, i + ivec2(1,0), 0).r;
  float h01 = texelFetch(uHeight, i + ivec2(0,1), 0).r;
  float h11 = texelFetch(uHeight, i + ivec2(1,1), 0).r;
  return mix(mix(h00,h10,f.x), mix(h01,h11,f.x), f.y);
}

void main(){
  vec2 c = aCell;
  vec2 a = abs(c);
  vec2 sg = sign(c);
  float G = uG;
  float k = uTile * G / 0.6931472;
  vec2 off = sg * k * (pow(vec2(2.0), a / G) - 1.0);
  vec2 world = uCam.xz + off;
  float h = texH(world / uTile);
  vec3 wp = vec3(world.x, h, world.y);
  vec4 clip = uViewProj * vec4(wp, 1.0);
  gl_Position = clip;
  vWP = wp;
  vViewZ = clip.w;
  vDist = distance(wp, uCam);
}`;

// Near ground: a patch of the tile grid that follows the eye.  The patch is
// built once in tile-index space; moving it is a single uniform, and every
// vertex lands exactly on a tile corner, so the terrain reads as flat blocks
// joined by faceted slopes instead of a smooth carpet.
const TILE_VS = `#version 300 es
precision highp float;
precision highp sampler2D;
in vec2 aCell;
uniform mat4 uViewProj;
uniform sampler2D uHeight;
uniform vec2 uMapSize;
uniform vec2 uOrigin;              // tile index of the patch's first vertex
uniform float uTile;
uniform vec3 uCam;
out vec3 vWP;
out float vViewZ;
out float vDist;

void main(){
  vec2 cell = clamp(uOrigin + aCell, vec2(0.0), uMapSize - 1.0);
  float h = texelFetch(uHeight, ivec2(cell), 0).r;
  vec3 wp = vec3(cell.x * uTile, h, cell.y * uTile);
  vec4 clip = uViewProj * vec4(wp, 1.0);
  gl_Position = clip;
  vWP = wp;
  vViewZ = clip.w;
  vDist = distance(wp, uCam);
}`;

// Shared ground shading: flat per-triangle normals (derivatives), OpenTTD-ish
// tile colours, the tile seam drawn only while a tile is big enough on screen.
const TERRAIN_FS = `#version 300 es
precision highp float;
precision highp usampler2D;
in vec3 vWP;
in float vViewZ;
in float vDist;
uniform usampler2D uTiles;
uniform vec2 uMapSize;
uniform float uTile;
uniform vec3 uSun;
uniform vec3 uFogColor;
uniform float uFogDensity;
uniform float uFar;
uniform vec4 uNear;                // covered patch (minx, minz, maxx, maxz)
out vec4 fragColor;

float hash21(vec2 p){
  return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

void main(){
  // the tile patch covers this fragment: skip the coarse mesh under it
  if (uNear.z > uNear.x &&
      vWP.x > uNear.x && vWP.x < uNear.z &&
      vWP.z > uNear.y && vWP.z < uNear.w) discard;

  // ground faces up; the derivative is constant over a facet, so a tile is
  // one flat shade and its sloped neighbour is a different one
  vec3 n = normalize(cross(dFdx(vWP), dFdy(vWP)));
  if (n.y < 0.0) n = -n;

  vec2 p = vWP.xz / uTile;
  ivec2 ti = clamp(ivec2(floor(p)), ivec2(0), ivec2(uMapSize) - 1);
  int t = int(texelFetch(uTiles, ti, 0).r);

  float tn = hash21(vec2(ti) + 0.5);                     // per tile mottle
  float field = hash21(floor(vec2(ti) * 0.125) + 3.7);   // whole fields

  // The seam is drawn in screen space - one pixel either side of the tile
  // border, whatever the angle of the ground - and fades out once a whole
  // tile shrinks to a handful of pixels.
  vec2 f = fract(p);
  vec2 fd = max(fwidth(p), vec2(1e-6));
  vec2 toSeam = min(f, 1.0 - f) / fd;               // in pixels
  float tilePx = 1.0 / max(fd.x, fd.y);             // tile size on screen
  float line = (1.0 - smoothstep(0.7, 1.7, min(toSeam.x, toSeam.y))) *
               clamp((tilePx - 6.0) / 10.0, 0.0, 1.0);

  vec3 col;
  if (t == 1) {
    col = vec3(0.30, 0.29, 0.27) * (0.90 + 0.20 * tn);
  } else if (t == 2) {
    col = vec3(0.44, 0.42, 0.38) * (0.90 + 0.20 * tn);
  } else if (t == 3 || t == 7) {
    col = mix(vec3(0.16, 0.25, 0.12), vec3(0.21, 0.31, 0.15), tn);
  } else if (t == 4) {
    col = vec3(0.35, 0.34, 0.31) * (0.86 + 0.22 * tn);
  } else if (t == 5 || t == 6) {
    col = vec3(0.34, 0.35, 0.36) * (0.94 + 0.12 * tn);
  } else {
    // grass: a patchwork of fields with the odd mown strip, plus per tile noise
    vec3 g1 = vec3(0.22, 0.34, 0.15);
    vec3 g2 = vec3(0.30, 0.43, 0.19);
    col = mix(g1, g2, field * 0.5 + tn * 0.5);
    col *= 0.93 + 0.15 * tn;
  }
  // steep facets fall into shade on their own, the way OpenTTD slopes read
  float slope = 1.0 - clamp(n.y, 0.0, 1.0);
  col *= 1.0 - 0.34 * smoothstep(0.10, 0.55, slope);
  col *= 1.0 - 0.22 * line;

  float diff = max(0.0, dot(n, uSun));
  vec3 c = col * (0.44 + 0.70 * diff);
  float fog = 1.0 - exp(-vDist * uFogDensity);
  c = mix(c, uFogColor, clamp(fog, 0.0, 1.0));
  fragColor = vec4(c, 1.0);
  gl_FragDepth = log2(max(vViewZ, 1e-4)) / log2(uFar);
}`;

// Roads: a ribbon mesh laid on the ground along the lane polylines, so a leg
// can run diagonally across the tile grid instead of stepping tile by tile.
const ROAD_VS = `#version 300 es
in vec3 aPos;
in vec3 aCol;
uniform mat4 uViewProj;
uniform vec3 uCam;
out vec3 vCol;
out float vViewZ;
out float vDist;
void main(){
  vec4 clip = uViewProj * vec4(aPos, 1.0);
  gl_Position = clip;
  vCol = aCol;
  vViewZ = clip.w;
  vDist = distance(aPos, uCam);
}`;

const ROAD_FS = `#version 300 es
precision highp float;
in vec3 vCol;
in float vViewZ;
in float vDist;
uniform vec3 uSun;
uniform vec3 uFogColor;
uniform float uFogDensity;
uniform float uFar;
out vec4 fragColor;
void main(){
  vec3 n = normalize(vec3(0.0, 1.0, 0.0));
  float diff = max(0.0, dot(n, uSun));
  vec3 c = vCol * (0.44 + 0.70 * diff);
  float fog = 1.0 - exp(-vDist * uFogDensity);
  c = mix(c, uFogColor, clamp(fog, 0.0, 1.0));
  fragColor = vec4(c, 1.0);
  gl_FragDepth = log2(max(vViewZ, 1e-4)) / log2(uFar);
}`;

const MESH_VS = `#version 300 es
precision highp float;
in vec3 aPos;
in vec3 aNormal;
in vec4 iM0;
in vec4 iM1;
in vec4 iM2;
in vec4 iM3;
in vec4 iColor;
uniform mat4 uViewProj;
out vec3 vNormal;
out vec4 vColor;
out vec3 vWorld;
out float vViewZ;
void main(){
  vec4 wp = iM0 * aPos.x + iM1 * aPos.y + iM2 * aPos.z + iM3;
  mat3 nm = mat3(normalize(iM0.xyz), normalize(iM1.xyz), normalize(iM2.xyz));
  vNormal = normalize(nm * aNormal);
  vColor = iColor;
  vWorld = wp.xyz;
  vec4 clip = uViewProj * wp;
  gl_Position = clip;
  vViewZ = clip.w;
}`;

const MESH_FS = `#version 300 es
precision highp float;
in vec3 vNormal;
in vec4 vColor;
in vec3 vWorld;
in float vViewZ;
uniform vec3 uSun;
uniform vec3 uCamPos;
uniform vec3 uFogColor;
uniform float uFogDensity;
uniform float uFar;
uniform float uUnlit;                // 1 = skip the lighting term (tracers)
out vec4 fragColor;
void main(){
  vec3 n = normalize(vNormal);
  vec3 v = normalize(uCamPos - vWorld);
  float diff = max(0.0, dot(n, uSun));
  float rim = pow(1.0 - max(0.0, dot(n, v)), 3.0) * 0.16;
  vec3 c = mix(vColor.rgb * (0.40 + 0.78 * diff + rim), vColor.rgb, uUnlit);
  float dist = distance(vWorld, uCamPos);
  float fog = 1.0 - exp(-dist * uFogDensity);
  c = mix(c, uFogColor, clamp(fog, 0.0, 1.0));
  fragColor = vec4(c, vColor.a);
  gl_FragDepth = log2(max(vViewZ, 1e-4)) / log2(uFar);
}`;

function compile(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
    throw new Error('shader: ' + gl.getShaderInfoLog(s) + '\n' + src.slice(0, 400));
  }
  return s;
}

function program(gl, vs, fs) {
  const p = gl.createProgram();
  gl.attachShader(p, compile(gl, gl.VERTEX_SHADER, vs));
  gl.attachShader(p, compile(gl, gl.FRAGMENT_SHADER, fs));
  gl.linkProgram(p);
  if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error('link: ' + gl.getProgramInfoLog(p));
  return p;
}

const STRIDE_FLOATS = 20;            // mat4 + vec4 colour
const CHUNK_TILES = 40;              // 640 m prop chunks
const DYN_CAP = [40000, 24000, 24000, 24000, 8000, 32000, 64];

// Optics: the radial terrain grid gets more rings per octave and a finer
// index grid the more the sight magnifies, so the ground stays smooth when
// 20x squeezes it into a three degree cone.  `k` is the half grid size.
const TERRAIN_LOD = [
  { g: 26, k: 128 },
  { g: 52, k: 160 },
  { g: 82, k: 192 },
  { g: 116, k: 224 },
];

// the tile patch that carries the near ground: TILE_PATCH tiles either way,
// re-centred on the eye one whole tile at a time
const TILE_PATCH = 48;

export class Renderer {
  constructor(canvas) {
    const gl = canvas.getContext('webgl2', { antialias: true, alpha: false, powerPreference: 'high-performance' });
    if (!gl) throw new Error('WebGL2 unavailable');
    this.gl = gl;
    this.canvas = canvas;
    this.fogColor = [0.66, 0.71, 0.75];
    this.fogDensity = 1 / 3600;
    this.sun = (() => { const s = [0.55, 0.72, 0.42]; const l = Math.hypot(...s); return s.map((x) => x / l); })();
    this.far = 32000;
    this.near = 1.0;
    this.stats = { draws: 0, instances: 0 };

    // WEBGL_multi_draw: one call submits a whole command list.  Bind the
    // function to whichever object owns it (context or extension object).
    this.multi = gl.getExtension('WEBGL_multi_draw');
    this.multiFn = null;
    if (typeof gl.multiDrawArraysInstancedWEBGL === 'function') {
      this.multiFn = gl.multiDrawArraysInstancedWEBGL.bind(gl);
    } else if (this.multi && typeof this.multi.multiDrawArraysInstancedWEBGL === 'function') {
      this.multiFn = this.multi.multiDrawArraysInstancedWEBGL.bind(this.multi);
    }

    this.pSky = program(gl, SKY_VS, SKY_FS);
    this.pTerrain = program(gl, TERRAIN_VS, TERRAIN_FS);
    this.pTile = program(gl, TILE_VS, TERRAIN_FS);
    this.pRoad = program(gl, ROAD_VS, ROAD_FS);
    this.pMesh = program(gl, MESH_VS, MESH_FS);
    this.uSky = this._uniforms(this.pSky, ['uInvVP', 'uCamPos', 'uSun']);
    this.uTerrain = this._uniforms(this.pTerrain, ['uViewProj', 'uHeight', 'uTiles', 'uMapSize', 'uTile', 'uG', 'uCam', 'uSun', 'uFogColor', 'uFogDensity', 'uFar', 'uNear']);
    this.uTile = this._uniforms(this.pTile, ['uViewProj', 'uHeight', 'uTiles', 'uMapSize', 'uTile', 'uOrigin', 'uCam', 'uSun', 'uFogColor', 'uFogDensity', 'uFar', 'uNear']);
    this.uRoad = this._uniforms(this.pRoad, ['uViewProj', 'uCam', 'uSun', 'uFogColor', 'uFogDensity', 'uFar']);
    this.uMesh = this._uniforms(this.pMesh, ['uViewProj', 'uSun', 'uCamPos', 'uFogColor', 'uFogDensity', 'uFar', 'uUnlit']);
    this.tileOrigin = [0, 0];

    // ---- primitive meshes in one VBO ---------------------------------
    const raw = buildMeshes();
    this.meshes = [];
    let total = 0;
    for (const m of raw) total += m.length;
    const verts = new Float32Array(total);
    let o = 0;
    for (const m of raw) {
      this.meshes.push({ first: o / 6, count: m.length / 6 });
      verts.set(m, o);
      o += m.length;
    }
    this.meshVBO = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.meshVBO);
    gl.bufferData(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);

    // sky triangle
    this.skyVBO = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.skyVBO);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);

    // dynamic instance buffers (one per mesh type)
    this.dyn = this.meshes.map((_, i) => {
      const data = new Float32Array(DYN_CAP[i] * STRIDE_FLOATS);
      const vbo = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.bufferData(gl.ARRAY_BUFFER, data.byteLength, gl.DYNAMIC_DRAW);
      return { data, count: 0, vbo, cap: DYN_CAP[i] };
    });

    // blended pass for impact effects
    this.dynA = this.meshes.map((_, i) => {
      const data = new Float32Array(12000 * STRIDE_FLOATS);
      const vbo = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.bufferData(gl.ARRAY_BUFFER, data.byteLength, gl.DYNAMIC_DRAW);
      return { data, count: 0, vbo, cap: 12000 };
    });

    this.propBatches = null;
    this.meshFirst = this.meshes.map((m) => m.first);
    this.meshCount = this.meshes.map((m) => m.count);
  }

  _uniforms(prog, names) {
    const o = {};
    for (const n of names) o[n] = this.gl.getUniformLocation(prog, n);
    return o;
  }

  resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 1.75);
    const w = Math.floor(this.canvas.clientWidth * dpr);
    const h = Math.floor(this.canvas.clientHeight * dpr);
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
  }

  // ------------------------------------------------------------------ map
  setMap(map) {
    const gl = this.gl;
    this.map = map;
    this.heightTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.heightTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32F, map.w, map.h, 0, gl.RED, gl.FLOAT, map.heights);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST],
    [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) {
      gl.texParameteri(gl.TEXTURE_2D, k, v);
    }
    this.tileTex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, this.tileTex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8UI, map.w, map.h, 0, gl.RED_INTEGER, gl.UNSIGNED_BYTE, map.tiles);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST],
    [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) {
      gl.texParameteri(gl.TEXTURE_2D, k, v);
    }
    this._buildTilePatch();
    this._buildRoads(map);
    this._buildProps(map);
    this.setZoomLevel(0);
  }

  // The near ground: one patch of the tile grid, kept around the eye by a
  // uniform.  Its vertices are tile corners, so the facets are the tiles.
  _buildTilePatch() {
    const gl = this.gl;
    const K = TILE_PATCH;
    const n = 2 * K;
    const cells = new Float32Array((n + 1) * (n + 1) * 2);
    let o = 0;
    for (let j = 0; j <= n; j++) {
      for (let i = 0; i <= n; i++) {
        cells[o++] = i;
        cells[o++] = j;
      }
    }
    const idx = new Uint32Array(n * n * 6);
    o = 0;
    for (let j = 0; j < n; j++) {
      for (let i = 0; i < n; i++) {
        const a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
        idx[o++] = a; idx[o++] = c; idx[o++] = b;
        idx[o++] = b; idx[o++] = c; idx[o++] = d;
      }
    }
    this.tileVBO = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.tileVBO);
    gl.bufferData(gl.ARRAY_BUFFER, cells, gl.STATIC_DRAW);
    this.tileIBO = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.tileIBO);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW);
    this.tileIndexCount = idx.length;
  }

  // rebuild the terrain grid for a new optics level (cheap, once per step)
  setZoomLevel(level) {
    const lod = TERRAIN_LOD[Math.max(0, Math.min(TERRAIN_LOD.length - 1, level))];
    if (this.terrainG === lod.g) return;
    this.terrainG = lod.g;
    this._buildTerrainGrid(lod.k);
  }

  _buildTerrainGrid(K) {
    const gl = this.gl;
    if (this.terrainVBO) gl.deleteBuffer(this.terrainVBO);
    const n = 2 * K + 1;
    const cells = new Float32Array(n * n * 2);
    let o = 0;
    for (let j = -K; j <= K; j++) {
      for (let i = -K; i <= K; i++) {
        cells[o++] = i;
        cells[o++] = j;
      }
    }
    const idx = new Uint32Array((n - 1) * (n - 1) * 6);
    o = 0;
    for (let j = 0; j < n - 1; j++) {
      for (let i = 0; i < n - 1; i++) {
        const a = j * n + i, b = a + 1, c = a + n, d = c + 1;
        idx[o++] = a; idx[o++] = c; idx[o++] = b;
        idx[o++] = b; idx[o++] = c; idx[o++] = d;
      }
    }
    this.terrainVBO = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.terrainVBO);
    gl.bufferData(gl.ARRAY_BUFFER, cells, gl.STATIC_DRAW);
    if (this.terrainIBO) gl.deleteBuffer(this.terrainIBO);
    this.terrainIBO = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.terrainIBO);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW);
    this.terrainIndexCount = idx.length;
  }

  // Roads are ribbons along the lane polylines: gravel shoulders, asphalt and
  // a dashed centre line.  Being ribbons, a leg is free to cut diagonally
  // across the tile grid instead of stepping tile by tile.
  _buildRoads(map) {
    const gl = this.gl;
    const lanes = map.lanes || [];
    const tile = map.tile;
    // cross section from the left shoulder to the right one: gravel, asphalt,
    // the dashed centre line, asphalt, gravel.  Each band is its own quad so
    // the paint does not bleed into the asphalt.
    const EDGE = [-8.0, -5.4, -0.8, 0.8, 5.4, 8.0];
    const GRAVEL = [0.35, 0.32, 0.26];
    const ASPHALT = [0.21, 0.205, 0.20];
    const PAINT = [0.62, 0.60, 0.50];
    const band = [GRAVEL, ASPHALT, PAINT, ASPHALT, GRAVEL];
    const out = [];
    const step = 8.0;                 // metres between cross sections
    const hgt = (x, z) => {
      const h = map.heights;
      const fx = Math.min(Math.max(x / tile, 0), map.w - 1.001);
      const fz = Math.min(Math.max(z / tile, 0), map.h - 1.001);
      const i = fx | 0, j = fz | 0;
      const tx = fx - i, tz = fz - j;
      const a = h[j * map.w + i], b = h[j * map.w + i + 1];
      const c = h[(j + 1) * map.w + i], d = h[(j + 1) * map.w + i + 1];
      return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
    };
    const vert = (x, y, z, col, k, c) => {
      // a little per-section grit so the surface is not a flat grey slab
      const g = 0.94 + 0.12 * Math.abs(Math.sin(k * 12.9898 + c * 78.233) % 1);
      out.push(x, y, z, col[0] * g, col[1] * g, col[2] * g);
    };
    for (const lane of lanes) {
      if (lane.length < 2) continue;
      // resample the polyline at a fixed spacing so corners are rounded off
      const pts = [];
      for (let k = 0; k < lane.length - 1; k++) {
        const [ax, az] = lane[k];
        const [bx, bz] = lane[k + 1];
        const seg = Math.hypot(bx - ax, bz - az);
        const n = Math.max(1, Math.round(seg / step));
        for (let i = 0; i < n; i++) {
          const t = i / n;
          pts.push([ax + (bx - ax) * t, az + (bz - az) * t]);
        }
      }
      pts.push(lane[lane.length - 1]);
      // miter normals so the ribbon has no notch on the inside of a corner
      const nrm = [];
      for (let k = 0; k < pts.length; k++) {
        const p0 = pts[Math.max(0, k - 1)];
        const p1 = pts[Math.min(pts.length - 1, k + 1)];
        let dx = p1[0] - p0[0], dz = p1[1] - p0[1];
        const len = Math.hypot(dx, dz) || 1;
        nrm.push([-dz / len, dx / len]);
      }
      const rows = [];
      for (let k = 0; k < pts.length; k++) {
        const [x, z] = pts[k];
        const [nx, nz] = nrm[k];
        const row = [];
        for (let c = 0; c < EDGE.length; c++) {
          const px = x + nx * EDGE[c];
          const pz = z + nz * EDGE[c];
          row.push([px, hgt(px, pz) + 0.30, pz]);
        }
        rows.push(row);
      }
      for (let k = 0; k < rows.length - 1; k++) {
        // one 8 m dash every 24 m: the centre line is dashed
        const dash = k % 3 === 0;
        for (let b = 0; b < band.length; b++) {
          const col = (b === 2 && !dash) ? ASPHALT : band[b];
          const a0 = rows[k][b], a1 = rows[k][b + 1];
          const b0 = rows[k + 1][b], b1 = rows[k + 1][b + 1];
          vert(a0[0], a0[1], a0[2], col, k, b);
          vert(b0[0], b0[1], b0[2], col, k, b);
          vert(a1[0], a1[1], a1[2], col, k, b);
          vert(a1[0], a1[1], a1[2], col, k, b);
          vert(b0[0], b0[1], b0[2], col, k, b);
          vert(b1[0], b1[1], b1[2], col, k, b);
        }
      }
    }
    this.roadCount = out.length / 6;
    if (!this.roadCount) return;
    this.roadVBO = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, this.roadVBO);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(out), gl.STATIC_DRAW);
  }

  _buildProps(map) {
    const gl = this.gl;
    const w = map.w, h = map.h, tile = map.tile;
    const nCx = Math.ceil(w / CHUNK_TILES), nCz = Math.ceil(h / CHUNK_TILES);
    const t0 = performance.now();
    const hash = (a, b) => {
      const s = Math.sin(a * 12.9898 + b * 78.233) * 43758.5453;
      return s - Math.floor(s);
    };

    // One plan per tile, used twice: first to count the instances, then to
    // fill them.  A tree tile grows a small stand of trunks and canopies, a
    // forest tile a thick one - several trees share a single tile, the way
    // OpenTTD plants them - and a building tile is a plain block with a roof.
    const TRUNK = [0.26, 0.19, 0.13];
    // house walls: concrete, sand, brick, slate
    const HOUSES = [
      [0.62, 0.60, 0.55], [0.72, 0.63, 0.48], [0.58, 0.40, 0.34], [0.44, 0.46, 0.48],
    ];
    const plant = (i, j, t, emit) => {
      const x = (i + 0.5) * tile;
      const z = (j + 0.5) * tile;
      const y = map.heightAt(x, z);
      const r1 = hash(i, j), r2 = hash(j * 1.7, i * 2.3);
      if (t === 3 || t === 7) {
        const n = t === 7 ? 3 + Math.floor(r1 * 3) : 1 + Math.floor(r1 * 2);
        for (let q = 0; q < n; q++) {
          const q1 = hash(i + q * 5.3, j + q * 1.9);
          const q2 = hash(j + q * 3.7, i + q * 7.1);
          const q3 = hash(i * 1.3 + q, j * 2.7 - q);
          // trunks spread over the tile but never touch its border
          const px = x + (q1 - 0.5) * tile * 0.66;
          const pz = z + (q2 - 0.5) * tile * 0.66;
          const py = map.heightAt(px, pz);
          const broad = q3 > 0.45;
          const tall = 10.0 + q3 * 8.0;                     // 10..18 m
          const rad = tall * (broad ? 0.40 : 0.26) * (0.85 + q1 * 0.3);
          const lean = (q2 - 0.5) * 0.10;
          emit(MESH_CYL, px, py + tall * 0.20, pz, q1 * 6.283, lean,
            0.7 + q3 * 0.5, tall * 0.40, 0.7 + q3 * 0.5, TRUNK);
          if (broad) {
            // broadleaf: a fat canopy ball sitting on the trunk
            emit(MESH_SPHERE, px, py + tall * 0.60, pz, q1 * 6.283, lean,
              rad, tall * 0.46, rad,
              [0.12 + q2 * 0.08, 0.26 + q1 * 0.13, 0.10 + q3 * 0.06]);
          } else {
            // conifer: one narrow cone, skirt down to the trunk
            emit(MESH_CONE, px, py + tall * 0.55, pz, q1 * 6.283, lean,
              rad, tall * 0.80, rad,
              [0.08 + q1 * 0.05, 0.19 + q2 * 0.14, 0.09 + q3 * 0.05]);
          }
        }
      } else if (t === 2) {
        // houses: a block from a small palette with a darker roof slab on top
        const sx = tile * (0.60 + r1 * 0.26);
        const sz = tile * (0.60 + r2 * 0.26);
        const sy = 7.0 + r2 * 15.0;
        const wall = HOUSES[Math.floor(r1 * HOUSES.length) % HOUSES.length];
        emit(MESH_BOX, x, y + sy * 0.5, z, 0, 0, sx, sy, sz, wall);
        emit(MESH_BOX, x, y + sy + 0.6, z, 0, 0, sx * 1.07, 1.2, sz * 1.07,
          [wall[0] * 0.5, wall[1] * 0.5, wall[2] * 0.52]);
        if (r2 > 0.55) {
          // an annex half the size, pushed into one corner
          const ax = x + (r1 - 0.5) * tile * 0.5;
          const az = z + (r2 - 0.5) * tile * 0.5;
          emit(MESH_BOX, ax, y + sy * 0.3, az, 0, 0, sx * 0.5, sy * 0.6, sz * 0.5,
            [wall[0] * 0.92, wall[1] * 0.88, wall[2] * 0.84]);
        }
      } else if (t === 4) {
        // two or three angular chunks per tile, tipped over at random angles,
        // half sunk into the ground: low poly boulders
        const sz0 = 4.6 + r1 * 5.0;
        const rock = [0.30 + r1 * 0.08, 0.285 + r1 * 0.07, 0.265 + r2 * 0.06];
        for (let q = 0; q < 3; q++) {
          const h1 = hash(i + q * 3.1, j + q * 7.7);
          const h2 = hash(j * 1.3 + q, i * 2.9 - q);
          const a = r2 * 6.283 + q * 2.1;
          const off = sz0 * 0.32 * q;
          const bw = sz0 * (0.7 - q * 0.16) * (0.85 + h1 * 0.4);
          const g = 0.88 + h2 * 0.24;
          emit(MESH_ROCK,
            x + Math.cos(a) * off, y + bw * 0.26, z + Math.sin(a) * off,
            a, (h1 - 0.5) * 0.5, bw, bw * (0.62 + h2 * 0.5), bw,
            [rock[0] * g, rock[1] * g, rock[2] * g]);
        }
      }
    };

    const want = (t) => t === 2 || t === 3 || t === 4 || t === 7;
    const counts = new Array(this.meshes.length).fill(0);
    const tally = (mesh) => { counts[mesh]++; };
    for (let j = 0; j < h; j++) {
      for (let i = 0; i < w; i++) {
        const t = map.tiles[j * w + i];
        if (want(t)) plant(i, j, t, tally);
      }
    }
    const datas = counts.map((c, i) => new Float32Array(Math.max(1, c) * STRIDE_FLOATS));
    const fills = new Array(this.meshes.length).fill(0);
    const chunkFirst = this.meshes.map(() => new Int32Array(nCx * nCz).fill(-1));
    const chunkCount = this.meshes.map(() => new Int32Array(nCx * nCz));
    let ci = 0;
    const add = (mesh, x, y, z, yaw, pitch, sx, sy, sz, col) => {
      const arr = datas[mesh];
      const idx = fills[mesh];
      if ((idx + 1) * STRIDE_FLOATS > arr.length) return;
      if (chunkFirst[mesh][ci] < 0) chunkFirst[mesh][ci] = idx;
      const base = idx * STRIDE_FLOATS;
      compose(arr.subarray(base, base + 16), x, y, z, yaw, pitch || 0, sx, sy, sz);
      arr[base + 16] = col[0]; arr[base + 17] = col[1];
      arr[base + 18] = col[2]; arr[base + 19] = 1;
      fills[mesh] = idx + 1;
      chunkCount[mesh][ci]++;
    };

    for (let cz = 0; cz < nCz; cz++) {
      for (let cx = 0; cx < nCx; cx++) {
        ci = cz * nCx + cx;
        const i0 = cx * CHUNK_TILES, j0 = cz * CHUNK_TILES;
        const i1 = Math.min(w, i0 + CHUNK_TILES), j1 = Math.min(h, j0 + CHUNK_TILES);
        for (let j = j0; j < j1; j++) {
          for (let i = i0; i < i1; i++) {
            const t = map.tiles[j * w + i];
            if (want(t)) plant(i, j, t, add);
          }
        }
      }
    }

    this.propBatches = this.meshes.map((_, m) => {
      const count = fills[m];
      if (count === 0) return null;
      const vbo = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      gl.bufferData(gl.ARRAY_BUFFER, datas[m].subarray(0, count * STRIDE_FLOATS), gl.STATIC_DRAW);
      const chunks = [];
      for (let cz = 0; cz < nCz; cz++) {
        for (let cx = 0; cx < nCx; cx++) {
          const ci = cz * nCx + cx;
          if (!chunkCount[m][ci]) continue;
          chunks.push({
            cx: (cx + 0.5) * CHUNK_TILES * tile,
            cz: (cz + 0.5) * CHUNK_TILES * tile,
            first: chunkFirst[m][ci],
            count: chunkCount[m][ci],
          });
        }
      }
      return { vbo, count, chunks };
    });
    this.propStats = {
      ms: Math.round(performance.now() - t0),
      instances: fills.reduce((a, b) => a + b, 0),
      chunks: this.propBatches.reduce((a, b) => a + (b ? b.chunks.length : 0), 0),
    };
  }

  // -------------------------------------------------------------- dynamic
  beginDynamic() {
    for (const b of this.dyn) b.count = 0;
  }

  beginParticles() {
    for (const b of this.dynA) b.count = 0;
  }

  push(mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) {
    const batch = this.dyn[mesh];
    if (!batch || batch.count >= batch.cap) return;
    const base = batch.count * STRIDE_FLOATS;
    const d = batch.data;
    compose(d.subarray(base, base + 16), px, py, pz, yaw, pitch, sx, sy, sz);
    d[base + 16] = r; d[base + 17] = g; d[base + 18] = b;
    d[base + 19] = a === undefined ? 1 : a;
    batch.count++;
  }

  pushA(mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) {
    const batch = this.dynA[mesh];
    if (!batch || batch.count >= batch.cap) return;
    const base = batch.count * STRIDE_FLOATS;
    const d = batch.data;
    compose(d.subarray(base, base + 16), px, py, pz, yaw, pitch, sx, sy, sz);
    d[base + 16] = r; d[base + 17] = g; d[base + 18] = b; d[base + 19] = a;
    batch.count++;
  }

  sphereA(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.pushA(MESH_SPHERE, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }
  coneA(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.pushA(MESH_CONE, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }
  smoke(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.push(MESH_SMOKE, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }

  box(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.push(MESH_BOX, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }
  sphere(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.push(MESH_SPHERE, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }
  cone(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.push(MESH_CONE, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }
  cyl(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.push(MESH_CYL, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }

  pyramid(px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) { this.push(MESH_PYRAMID, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a); }

  // ---------------------------------------------------------------- frame
  draw(cam) {
    const gl = this.gl;
    this.resize();
    this.stats.draws = 0;
    this.stats.instances = 0;
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.clearColor(this.fogColor[0], this.fogColor[1], this.fogColor[2], 1);
    gl.clearDepth(1.0);
    gl.enable(gl.DEPTH_TEST);
    gl.depthFunc(gl.LESS);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    this._drawSky(cam);
    this._drawTerrain(cam);
    this._setupMeshUniforms(cam);
    this._drawProps(cam);
    this._drawDynamic(cam);
    this._drawParticles();
  }

  _drawSky(cam) {
    const gl = this.gl;
    gl.useProgram(this.pSky);
    gl.uniformMatrix4fv(this.uSky.uInvVP, false, cam.invViewProj);
    gl.uniform3fv(this.uSky.uCamPos, cam.eye);
    gl.uniform3fv(this.uSky.uSun, this.sun);
    gl.disable(gl.DEPTH_TEST);
    gl.depthMask(false);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.skyVBO);
    const loc = gl.getAttribLocation(this.pSky, 'aPos');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.depthMask(true);
    gl.enable(gl.DEPTH_TEST);
    this.stats.draws++;
  }

  _drawTerrain(cam) {
    const gl = this.gl;
    const tile = this.map.tile;
    // the tile patch is centred on the eye, snapped to whole tiles so every
    // vertex stays exactly on a tile corner
    const K = TILE_PATCH;
    const ox = Math.min(Math.max(Math.round(cam.eye[0] / tile) - K, 0), this.map.w - 1 - 2 * K);
    const oz = Math.min(Math.max(Math.round(cam.eye[2] / tile) - K, 0), this.map.h - 1 - 2 * K);
    this.tileOrigin = [ox, oz];
    // world bounds of the patch, handed to the far program so it can cut the
    // same rectangle out of its own mesh
    const patch = [ox * tile, oz * tile, (ox + 2 * K) * tile, (oz + 2 * K) * tile];

    const common = (u) => {
      gl.uniformMatrix4fv(u.uViewProj, false, cam.viewProj);
      gl.uniform2f(u.uMapSize, this.map.w, this.map.h);
      gl.uniform1f(u.uTile, tile);
      gl.uniform3fv(u.uCam, cam.eye);
      gl.uniform3fv(u.uSun, this.sun);
      gl.uniform3fv(u.uFogColor, this.fogColor);
      gl.uniform1f(u.uFogDensity, this.fogDensity);
      gl.uniform1f(u.uFar, this.far);
      gl.uniform4f(u.uNear, patch[0], patch[1], patch[2], patch[3]);
      gl.activeTexture(gl.TEXTURE0);
      gl.bindTexture(gl.TEXTURE_2D, this.heightTex);
      gl.uniform1i(u.uHeight, 0);
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, this.tileTex);
      gl.uniform1i(u.uTiles, 1);
    };

    // far ground first, leaving the patch area to the tile grid
    gl.useProgram(this.pTerrain);
    common(this.uTerrain);
    gl.uniform1f(this.uTerrain.uG, this.terrainG);
    let loc = gl.getAttribLocation(this.pTerrain, 'aCell');
    gl.bindBuffer(gl.ARRAY_BUFFER, this.terrainVBO);
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.terrainIBO);
    gl.drawElements(gl.TRIANGLES, this.terrainIndexCount, gl.UNSIGNED_INT, 0);
    gl.disableVertexAttribArray(loc);
    this.stats.draws++;

    // near ground: the tile grid, flat shaded facets and all.  Its own bounds
    // are cleared in uNear, or it would discard every fragment it owns.
    gl.useProgram(this.pTile);
    common(this.uTile);
    gl.uniform4f(this.uTile.uNear, 0, 0, -1, -1);
    gl.uniform2f(this.uTile.uOrigin, ox, oz);
    loc = gl.getAttribLocation(this.pTile, 'aCell');
    gl.bindBuffer(gl.ARRAY_BUFFER, this.tileVBO);
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.tileIBO);
    gl.drawElements(gl.TRIANGLES, this.tileIndexCount, gl.UNSIGNED_INT, 0);
    gl.disableVertexAttribArray(loc);
    this.stats.draws++;

    // then the roads laid on top of it
    if (this.roadCount) {
      gl.useProgram(this.pRoad);
      gl.uniformMatrix4fv(this.uRoad.uViewProj, false, cam.viewProj);
      gl.uniform3fv(this.uRoad.uCam, cam.eye);
      gl.uniform3fv(this.uRoad.uSun, this.sun);
      gl.uniform3fv(this.uRoad.uFogColor, this.fogColor);
      gl.uniform1f(this.uRoad.uFogDensity, this.fogDensity);
      gl.uniform1f(this.uRoad.uFar, this.far);
      const aPos = gl.getAttribLocation(this.pRoad, 'aPos');
      const aCol = gl.getAttribLocation(this.pRoad, 'aCol');
      gl.bindBuffer(gl.ARRAY_BUFFER, this.roadVBO);
      gl.enableVertexAttribArray(aPos);
      gl.vertexAttribPointer(aPos, 3, gl.FLOAT, false, 24, 0);
      gl.enableVertexAttribArray(aCol);
      gl.vertexAttribPointer(aCol, 3, gl.FLOAT, false, 24, 12);
      gl.drawArrays(gl.TRIANGLES, 0, this.roadCount);
      gl.disableVertexAttribArray(aPos);
      gl.disableVertexAttribArray(aCol);
      this.stats.draws++;
    }
  }

  _setupMeshUniforms(cam) {
    const gl = this.gl;
    gl.useProgram(this.pMesh);
    gl.uniformMatrix4fv(this.uMesh.uViewProj, false, cam.viewProj);
    gl.uniform3fv(this.uMesh.uSun, this.sun);
    gl.uniform3fv(this.uMesh.uCamPos, cam.eye);
    gl.uniform3fv(this.uMesh.uFogColor, this.fogColor);
    gl.uniform1f(this.uMesh.uFogDensity, this.fogDensity);
    gl.uniform1f(this.uMesh.uFar, this.far);
  }

  _bindMesh(meshIndex, vbo, byteOffset) {
    const gl = this.gl;
    const p = this.pMesh;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.meshVBO);
    const aPos = gl.getAttribLocation(p, 'aPos');
    const aNorm = gl.getAttribLocation(p, 'aNormal');
    gl.enableVertexAttribArray(aPos);
    gl.vertexAttribPointer(aPos, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(aNorm);
    gl.vertexAttribPointer(aNorm, 3, gl.FLOAT, false, 24, 12);
    gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
    const stride = STRIDE_FLOATS * 4;
    const names = ['iM0', 'iM1', 'iM2', 'iM3', 'iColor'];
    for (let k = 0; k < 5; k++) {
      const loc = gl.getAttribLocation(p, names[k]);
      gl.enableVertexAttribArray(loc);
      gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, stride, (byteOffset || 0) + k * 16);
      gl.vertexAttribDivisor(loc, 1);
    }
  }

  _submit(meshIndex, vbo, cmds, unlit) {
    const gl = this.gl;
    if (!cmds.length) return;
    gl.uniform1f(this.uMesh.uUnlit, unlit ? 1 : 0);
    const first = this.meshFirst[meshIndex];
    const count = this.meshCount[meshIndex];
    if (this.multiFn && cmds.length > 1) {
      const firsts = new Int32Array(cmds.length);
      const counts = new Int32Array(cmds.length);
      const insts = new Int32Array(cmds.length);
      const offs = new Int32Array(cmds.length);
      let total = 0;
      for (let k = 0; k < cmds.length; k++) {
        firsts[k] = first;
        counts[k] = count;
        insts[k] = cmds[k].count;
        offs[k] = cmds[k].first;
        total += cmds[k].count;
      }
      this._bindMesh(meshIndex, vbo, 0);
      try {
        this.multiFn(gl.TRIANGLES, firsts, counts, insts, offs);
        this.stats.draws++;
        this.stats.instances += total;
        return;
      } catch (err) {
        console.warn('multi draw failed, falling back', err);
        this.multiFn = null;
      }
    }
    const stride = STRIDE_FLOATS * 4;
    const names = ['iM0', 'iM1', 'iM2', 'iM3', 'iColor'];
    const p = this.pMesh;
    for (const c of cmds) {
      this._bindMesh(meshIndex, vbo, 0);
      gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
      // no baseInstance in WebGL2: shift the attribute pointers instead
      for (let k = 0; k < 5; k++) {
        const loc = gl.getAttribLocation(p, names[k]);
        gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, stride, c.first * stride + k * 16);
      }
      gl.drawArraysInstanced(gl.TRIANGLES, first, count, c.count);
      this.stats.draws++;
      this.stats.instances += c.count;
    }
  }

  _drawProps(cam) {
    if (!this.propBatches) return;
    const R2 = 4600 * 4600;
    for (let m = 0; m < this.propBatches.length; m++) {
      const batch = this.propBatches[m];
      if (!batch) continue;
      const cmds = [];
      for (const c of batch.chunks) {
        const dx = c.cx - cam.eye[0];
        const dz = c.cz - cam.eye[2];
        if (dx * dx + dz * dz < R2) cmds.push(c);
      }
      this._submit(m, batch.vbo, cmds);
    }
  }

  _drawParticles() {
    const gl = this.gl;
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
    gl.depthMask(false);
    for (let m = 0; m < this.dynA.length; m++) {
      const batch = this.dynA[m];
      if (!batch.count) continue;
      gl.bindBuffer(gl.ARRAY_BUFFER, batch.vbo);
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, batch.data.subarray(0, batch.count * STRIDE_FLOATS));
      this._submit(m, batch.vbo, [{ first: 0, count: batch.count }]);
    }
    gl.depthMask(true);
    gl.disable(gl.BLEND);
  }

  _drawDynamic(cam) {
    const gl = this.gl;
    for (let m = 0; m < this.dyn.length; m++) {
      const batch = this.dyn[m];
      if (!batch.count) continue;
      gl.bindBuffer(gl.ARRAY_BUFFER, batch.vbo);
      gl.bufferSubData(gl.ARRAY_BUFFER, 0, batch.data.subarray(0, batch.count * STRIDE_FLOATS));
      this._submit(m, batch.vbo, [{ first: 0, count: batch.count }], m === MESH_PYRAMID);
    }
  }
}

export function makeCamera(cam, eye, target, fov, aspect, near, far) {
  const proj = perspective(mat4(), fov, aspect, near, far);
  const view = lookAt(mat4(), eye, target, [0, 1, 0]);
  cam.viewProj = multiply(cam.viewProj || mat4(), proj, view);
  cam.eye = eye;
  return cam;
}
