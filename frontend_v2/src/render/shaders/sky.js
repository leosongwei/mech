// The sky: one full-screen triangle behind everything, shaded from the inverse
// view-projection so the gradient is a direction in world space and the sun sits
// where the light on the ground says it does.

import { GLSL, HEAD } from './chunks.js';

export const SKY_VS = `${GLSL}
in vec2 aPos;
out vec2 vPos;
void main(){ vPos = aPos; gl_Position = vec4(aPos, 1.0, 1.0); }`;

export const SKY_FS = `${HEAD}
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
