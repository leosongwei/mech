// The blocks every program is assembled from.
//
// They are text, not a shading language: a program is a template literal, and
// the frames' shared terms - the sun's diffuse lobe, the distance fade, the log
// depth write - are written once here and pasted where a surface wants them.
//
// No `#version` line: the device asks three for GLSL 3.00 ES and three puts the
// directive in front of whatever source a material carries.

// Fragment shaders have no default float precision in GLSL ES 3.00.
export const HEAD = 'precision highp float;';
export const HEAD_SAMPLER2D = `${HEAD}\nprecision highp sampler2D;`;
export const HEAD_USAMPLER2D = `${HEAD}\nprecision highp usampler2D;`;

// One directional sun lights the world, so the diffuse term is the dot product
// and how much of it a surface takes is the surface's own business: the ground
// and the asphalt share `groundLight`, a painted body adds a rim term, an unlit
// primitive skips the lot.
export const SUN_DIFFUSE = 'float diff = max(0.0, dot(n, uSun));';
export const groundLight = (base) => `vec3 c = ${base} * (0.44 + 0.70 * diff);`;

// Distance fades into the fog colour and nothing else does.
export const fogAt = (target = 'c', dist = 'vDist') =>
  `float fog = 1.0 - exp(-${dist} * uFogDensity);
  ${target} = mix(${target}, uFogColor, clamp(fog, 0.0, 1.0));`;

// Logarithmic depth: the fragment's depth is the log of its clip-space w over
// the log of the far plane, which is what makes a 1 m .. 32 km view work on a
// 24 bit buffer without a z-fighting band. The denominator is a uniform because
// the overlay draws at its own scale.
export const logDepth = (far = 'uFar') =>
  `gl_FragDepth = log2(max(vViewZ, 1e-4)) / log2(${far});`;

// Interleaved gradient noise: a per-pixel value in [0,1) that reads as grain
// rather than a lattice.
export const IGN = `float ign(vec2 p) {
  return fract(52.9829189 * fract(0.06711056 * p.x + 0.00583715 * p.y));
}`;
