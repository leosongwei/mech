// The overlay's own program: the threat arrows beside the warning dial.
//
// They are the cylinder and the cone the world is made of, drawn in the HUD's
// CSS pixels. This program has the overlay's own uniforms and none of the
// world's shader traffic: no facade, no unlit term, no fog, and a depth write
// whose only job is to sort the pieces against each other.
//
// `uPiece` names the piece (1 shaft, 2 head): the cone's base cap is painted
// white, which is what says "this arrow points away from you".

import { HEAD, SUN_DIFFUSE, logDepth } from './chunks.js';

export const OVERLAY_VS = `${HEAD}
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
void main(){
  vec4 wp = iMatrix * vec4(position, 1.0);
  mat3 nm = mat3(normalize(iMatrix[0].xyz), normalize(iMatrix[1].xyz),
                 normalize(iMatrix[2].xyz));
  vNormal = normalize(nm * normal);
  vColor = iColor;
  vWorld = wp.xyz;
  vLocal = position;
  vLocalN = normal;
  vec4 clip = uViewProj * wp;
  gl_Position = clip;
  vViewZ = clip.w;
}`;

export const OVERLAY_FS = `${HEAD}
in vec3 vNormal;
in vec4 vColor;
in vec3 vWorld;
in float vViewZ;
in vec3 vLocal;
in vec3 vLocalN;
uniform vec3 uSun;
uniform vec3 uCamPos;
uniform float uFar;                  // 2: the log depth is then a plain log2(w)
uniform float uPiece;                // 1 shaft, 2 head
out vec4 fragColor;

void main(){
  vec3 n = normalize(vNormal);
  vec3 v = normalize(uCamPos - vWorld);
  ${SUN_DIFFUSE}
  float rim = pow(1.0 - max(0.0, dot(n, v)), 3.0) * 0.16;
  vec3 c = vColor.rgb * (0.40 + 0.78 * diff + rim);
  // The cone's base cap is white and everything else keeps the arrow's colour.
  // Seen down the arrow's own axis - which is exactly what an arrow for a round
  // on the crosshair is - that cap is the whole silhouette, so a white disc says
  // "the back of the head" at a glance, and a white cap ringed by red says the
  // same once the arrow starts to turn. White on the near end would be a lie,
  // and the near end is only ever the shaft's own cap.
  if (uPiece > 0.5) {
    c = mix(c, vColor.rgb, 0.35);
    float base = step(1.5, uPiece) * step(0.90, abs(vLocalN.y)) * step(0.49, -vLocal.y);
    c = mix(c, vec3(0.90, 0.90, 0.88), base);
  }
  fragColor = vec4(c, vColor.a);
  ${logDepth()}
}`;
