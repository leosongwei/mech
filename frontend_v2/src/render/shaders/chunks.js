// The blocks every one of the five programs needs, written once.
//
// They were copied into three of the shaders by hand - the preamble, the fog
// line, the log depth write - and a change to one copy was a change to the
// others by hope.  Each block is a function of the names it is pasted among
// (`fogAt('c', 'vDist')`), so the *assembled* source of every program is still
// the source the old renderer built, character for character: the blocks are
// text, not a new shading language.

export const GLSL = '#version 300 es';

// A vertex shader that takes no samplers needs no sampler precision; the terrain
// and tile vertex shaders fetch heights, so they do.
export const HEAD = `${GLSL}\nprecision highp float;`;
export const HEAD_SAMPLER2D = `${HEAD}\nprecision highp sampler2D;`;
export const HEAD_USAMPLER2D = `${HEAD}\nprecision highp usampler2D;`;

// The sun's diffuse term.  Everything in the world is lit by one directional
// light at `uSun`, so the term is `max(0, dot(n, uSun))` and the *amount* of it
// belongs to the surface: the ground and the asphalt share `groundLight`, a
// painted body adds a rim term, and an unlit primitive skips the lot.
export const SUN_DIFFUSE = 'float diff = max(0.0, dot(n, uSun));';
export const groundLight = (base) => `vec3 c = ${base} * (0.44 + 0.70 * diff);`;

// Distance fades into the fog colour and nothing else does - the far LOD grid is
// cut away by `uNear`, not by the fog.  `dist` is whatever length the shader has
// already measured (the terrain interpolates it, the mesh program computes it
// from the eye).
export const fogAt = (target = 'c', dist = 'vDist') =>
  `float fog = 1.0 - exp(-${dist} * uFogDensity);
  ${target} = mix(${target}, uFogColor, clamp(fog, 0.0, 1.0));`;

// Logarithmic depth: `gl_FragDepth` is the log of the clip-space w over the log
// of the far plane, which is what makes a 1 m..32 km view work without
// z-fighting on a 24 bit buffer.  The denominator is `uFar` because the overlay
// draws at its own scale (see `shaders/overlay.js`).
export const logDepth = (far = 'uFar') =>
  `gl_FragDepth = log2(max(vViewZ, 1e-4)) / log2(${far});`;
