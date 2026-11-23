// The body program: one material for every instanced body in the world - hulls,
// parts, props, tracers, particles. A body is a primitive (a unit-sized
// `position`/`normal` pair) plus an instance matrix and a colour, so what a
// primitive *is* becomes a compile-time branch on its defines rather than a
// uniform looked up per draw:
//
//   FACADE   the box paints window rows, storeys and a plinth on its walls
//   UNLIT    the tracer keeps its colour and takes no light
//   DITHER   the fragment is kept or thrown away against per-pixel noise, which
//            is how the impact pass draws transparency without blending
//
// One program per combination that actually occurs, and a primitive's variants
// are chosen from a table (`primitives.js`) rather than by hand at a call site.

import { HEAD, SUN_DIFFUSE, fogAt, logDepth, IGN } from './chunks.js';

export const WORLD_VS = `${HEAD}
in vec3 position;
in vec3 normal;
in mat4 iMatrix;
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
  vec4 wp = iMatrix * vec4(position, 1.0);
  // the instance's three columns are its axes, already scaled: their lengths
  // are the size in metres and their directions are the local frame
  mat3 nm = mat3(normalize(iMatrix[0].xyz), normalize(iMatrix[1].xyz),
                 normalize(iMatrix[2].xyz));
  vNormal = normalize(nm * normal);
  vColor = iColor;
  vWorld = wp.xyz;
  vLocal = position;
  vLocalN = normal;
  vScale = vec3(length(iMatrix[0].xyz), length(iMatrix[1].xyz), length(iMatrix[2].xyz));
  vec4 clip = uViewProj * wp;
  gl_Position = clip;
  vViewZ = clip.w;
}`;

export const WORLD_FS = `${HEAD}
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
out vec4 fragColor;

#ifdef DITHER
${IGN}
#endif

#ifdef FACADE
// A plain cube read as a house: a darker plinth, a cornice under the roof and a
// grid of windows, measured on the instance's own walls so every building gets
// the rows its height affords.
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
#endif

void main(){
  vec3 n = normalize(vNormal);
  vec3 v = normalize(uCamPos - vWorld);
  ${SUN_DIFFUSE}
  float rim = pow(1.0 - max(0.0, dot(n, v)), 3.0) * 0.16;
#ifdef FACADE
  vec3 albedo = facade(vColor.rgb, n, vLocal, vScale);
#else
  vec3 albedo = vColor.rgb;
#endif
  vec3 c = albedo * (0.40 + 0.78 * diff + rim);
#ifdef UNLIT
  c = vColor.rgb;
#endif
  float dist = distance(vWorld, uCamPos);
  ${fogAt('c', 'dist')}
  float alpha = vColor.a;
#ifdef DITHER
  // A fragment keeps the whole pixel when its alpha beats the noise and leaves
  // the pixel to the layer behind it when it does not, so a fireball is a
  // speckle whose density is its opacity. The layers still stack - a pixel one
  // body threw away is a pixel the next one fills - which is what keeps a cloud
  // of thin puffs reading solid. The threshold depends only on where the pixel
  // is, so a held picture is a still one and a moving eye slides the world
  // through the grain.
  if (alpha <= ign(gl_FragCoord.xy)) discard;
  alpha = 1.0;
#endif
  fragColor = vec4(c, alpha);
  ${logDepth()}
}`;
