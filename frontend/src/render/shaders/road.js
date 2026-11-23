// The asphalt: a ribbon mesh laid on the ground along the road bits, so a leg
// can run diagonally across the tile grid instead of stepping tile by tile.

import { HEAD, SUN_DIFFUSE, groundLight, fogAt, logDepth } from './chunks.js';

export const ROAD_VS = `${HEAD}
in vec3 position;
in vec3 color;
uniform mat4 uViewProj;
uniform vec3 uCam;
out vec3 vCol;
out float vViewZ;
out float vDist;
void main(){
  vec4 clip = uViewProj * vec4(position, 1.0);
  gl_Position = clip;
  vCol = color;
  vViewZ = clip.w;
  vDist = distance(position, uCam);
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
