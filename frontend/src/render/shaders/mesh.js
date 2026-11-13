// Instanced primitives: one program for every body in the world - hulls, parts,
// props, tracers, particles.  The per-primitive *behaviour* (a facade paints its
// storeys, a tracer is unlit) arrives as a uniform read off `mesh-table.js`, so
// this program is the same code for all eleven shapes.

import { HEAD, SUN_DIFFUSE, fogAt, logDepth } from './chunks.js';

export const MESH_VS = `${HEAD}
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
out vec3 vLocal;               // position in the primitive's unit cube
out vec3 vLocalN;              // and its normal, before the instance turns it
out vec3 vScale;               // the instance's size in metres
void main(){
  vec4 wp = iM0 * aPos.x + iM1 * aPos.y + iM2 * aPos.z + iM3;
  mat3 nm = mat3(normalize(iM0.xyz), normalize(iM1.xyz), normalize(iM2.xyz));
  vNormal = normalize(nm * aNormal);
  vColor = iColor;
  vWorld = wp.xyz;
  vLocal = aPos;
  vLocalN = aNormal;
  vScale = vec3(length(iM0.xyz), length(iM1.xyz), length(iM2.xyz));
  vec4 clip = uViewProj * wp;
  gl_Position = clip;
  vViewZ = clip.w;
}`;

export const MESH_FS = `${HEAD}
in vec3 vNormal;
in vec4 vColor;
in vec3 vWorld;
in float vViewZ;
in vec3 vLocal;
in vec3 vLocalN;
in vec3 vScale;
uniform vec3 uSun;
uniform vec3 uCamPos;
uniform vec3 uFogColor;
uniform float uFogDensity;
uniform float uFar;
uniform float uUnlit;                // 1 = skip the lighting term (tracers)
uniform float uFacade;               // 1 = paint storeys and windows on the walls
uniform float uOutline;              // 0 off, 1 cylinder, 2 cone (overlay paint)
out vec4 fragColor;

// A plain cube read as a house: a darker plinth, a cornice under the roof and
// a grid of windows, measured on the instance's own walls so every building
// gets the rows its height affords.
vec3 facade(vec3 wall, vec3 n, vec3 lp, vec3 sc) {
  if (abs(n.y) > 0.5) return wall * (n.y > 0.0 ? 0.74 : 0.88);
  float up = lp.y * sc.y + sc.y * 0.5;                    // metres above the base
  float along = abs(n.x) > 0.5 ? lp.z * sc.z : lp.x * sc.x;
  float plinth = step(up, 1.2);
  float cornice = step(sc.y - 0.9, up);
  vec3 c = mix(wall, wall * 0.70, plinth);
  c = mix(c, wall * 0.86, cornice);
  // one window band per storey (the wall columns are 4 m floors), 3.2 m apart
  float rowv = fract(up / 4.0);
  float colu = fract(along / 3.2);
  float win = step(0.30, rowv) * step(rowv, 0.76) *
              step(0.22, colu) * step(colu, 0.78) *
              step(1.6, up) * step(up, sc.y - 1.3);
  float frame = step(0.24, rowv) * step(rowv, 0.82) *
                step(0.16, colu) * step(colu, 0.84) *
                step(1.6, up) * step(up, sc.y - 1.3);
  c = mix(c, wall * 1.20, frame * 0.45);
  float id = floor(up * 0.25) * 7.0 + floor(along / 3.2);
  float r = fract(sin(id * 12.9898) * 43758.5453);
  vec3 glass = mix(vec3(0.10, 0.13, 0.18), vec3(0.21, 0.27, 0.33), r);
  return mix(c, glass, win);
}

void main(){
  vec3 n = normalize(vNormal);
  vec3 v = normalize(uCamPos - vWorld);
  ${SUN_DIFFUSE}
  float rim = pow(1.0 - max(0.0, dot(n, v)), 3.0) * 0.16;
  vec3 albedo = uFacade > 0.5 ? facade(vColor.rgb, n, vLocal, vScale) : vColor.rgb;
  vec3 c = mix(albedo * (0.40 + 0.78 * diff + rim), vColor.rgb, uUnlit);
  // The overlay's paint: the cone's base cap is white and everything else keeps
  // the arrow's red.  Seen down the arrow's own axis - which is exactly what an
  // arrow for a round on the crosshair is - that cap is the whole silhouette,
  // so a white disc says "the back of the head" at a glance, and a white cap
  // ringed by red says the same once the arrow starts to turn.  White on the
  // near end would be a lie, and the near end is only ever the shaft's own cap.
  // uOutline picks the piece: 1 the cylinder, 2 the cone.
  if (uOutline > 0.5) {
    c = mix(c, vColor.rgb, 0.35);
    float base = step(1.5, uOutline) * step(0.90, abs(vLocalN.y)) * step(0.49, -vLocal.y);
    c = mix(c, vec3(0.90, 0.90, 0.88), base);
  }
  float dist = distance(vWorld, uCamPos);
  ${fogAt('c', 'dist')}
  fragColor = vec4(c, vColor.a);
  ${logDepth()}
}`;
