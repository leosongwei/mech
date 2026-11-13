// The ground, in two meshes that share one fragment shader.
//
// The far mesh is a radial LOD grid around the eye whose heights are sampled
// from the height texture in the vertex shader, so the 16 km map never touches
// the CPU.  The near mesh is a patch of the tile grid that follows the eye; its
// vertices are tile corners, so the facets are the tiles.  The far program cuts
// the patch's rectangle out of its own surface (`uNear`), and the patch clears
// that cut, which is what makes them one continuous ground with two
// resolutions.

import { HEAD_SAMPLER2D, HEAD_USAMPLER2D, SUN_DIFFUSE, groundLight, fogAt, logDepth } from './chunks.js';

export const TERRAIN_VS = `${HEAD_SAMPLER2D}
in vec2 aCell;
uniform mat4 uViewProj;
uniform sampler2D uHeight;
uniform vec2 uMapSize;
uniform float uTile;
uniform float uG;
uniform float uA0;
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
  float kk = uTile * G / 0.6931472;
  // Up to uA0 rings out the grid keeps the plain tile spacing - one quad per
  // 16 m cell, nothing merged - and only past that does the spacing start to
  // double every G rings.  The far hills are drawn at tile resolution out to
  // 3 km at 20x optics, which is where the sight actually looks.
  vec2 d = max(a - uA0, 0.0);
  vec2 spacing = uTile * exp2(d / G);
  // Step the grid with the eye in whole *tiles* instead of sliding it
  // continuously: every vertex then sits on a fixed world point until the eye
  // crosses a tile, and the far hills stay put instead of crawling over the
  // heightfield a little every frame.
  //
  // One lattice for the whole mesh, at the finest spacing, and not one per band:
  // a band that snapped to its own (coarser) grid put its vertices on a lattice
  // with a different phase, and at every band boundary the quad that straddles it
  // was stretched by up to a whole local cell - hundreds of metres out at the far
  // rings, and re-phased every time the eye crossed a cell, so the tear moved.
  vec2 base = floor(uCam.xz / uTile) * uTile;
  vec2 off = sg * (uTile * min(a, vec2(uA0)) + kk * (exp2(d / G) - 1.0));
  vec2 world = base + off;
  float h = texH(world / uTile);
  vec3 wp = vec3(world.x, h, world.y);
  vec4 clip = uViewProj * vec4(wp, 1.0);
  gl_Position = clip;
  vWP = wp;
  vViewZ = clip.w;
  vDist = distance(wp, uCam);
}`;

export const TILE_VS = `${HEAD_SAMPLER2D}
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
export const TERRAIN_FS = `${HEAD_USAMPLER2D}
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

  // a road tile is grass with asphalt laid on it by the road pass, the way an
  // OpenTTD road tile is its ground sprite plus the road: painting the whole
  // tile grey would show a 16 m swath under a 10 m road
  vec3 col;
  if (t == 2) {
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

  ${SUN_DIFFUSE}
  ${groundLight('col')}
  ${fogAt('c', 'vDist')}
  fragColor = vec4(c, 1.0);
  ${logDepth()}
}`;
