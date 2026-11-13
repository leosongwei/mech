// The asphalt: a ribbon mesh laid on the ground along the road bits, so a leg
// can run diagonally across the tile grid instead of stepping tile by tile.
// Drawn straight after the near ground, in the ground's own state.

import { GLSL, HEAD, SUN_DIFFUSE, groundLight, fogAt, logDepth } from './chunks.js';

export const ROAD_VS = `${GLSL}
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

export const ROAD_FS = `${HEAD}
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
  ${SUN_DIFFUSE}
  ${groundLight('vCol')}
  ${fogAt('c', 'vDist')}
  fragColor = vec4(c, 1.0);
  ${logDepth()}
}`;
