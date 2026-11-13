// A frame is a list, not a call chain.
//
// The order below is the order the old `Renderer.draw()` had, line for line:
// sky, then the ground and the asphalt, then the props, then everything that
// moves, then the blended particles, then the screen-space overlay.  What
// changed is where the order *lives* - these six entries instead of a method
// body, so "which pass emits, which pass clears the depth buffer" is a fact
// about a pass rather than a comment about a line.
//
// `emit(batches)` is how the world's instances get into the buffers: units,
// projectiles, smoke canisters, puffs and the blended particles all arrive in
// one call, from the `world` pass, which is the only place the model code runs.

import { SKY_VS, SKY_FS } from './shaders/sky.js';
import { terrain } from './terrain.js';
import { props } from './props.js';
import { overlay } from './overlay.js';

// The sky owns one program and one triangle; both are made on the first frame
// that draws it and kept for the life of the context (the `Gpu` tracker deletes
// them with everything else).
const SKY = new WeakMap();

function skyResources(R) {
  let s = SKY.get(R.gpu);
  if (s) return s;
  const gpu = R.gpu;
  const gl = gpu.gl;
  const program = gpu.program('sky', SKY_VS, SKY_FS, ['uInvVP', 'uCamPos', 'uSun']);
  s = {
    program,
    vbo: gpu.buffer(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW),
    aPos: gpu.attrib(program.prog, 'aPos'),
  };
  SKY.set(gpu, s);
  return s;
}

const sky = {
  name: 'sky',
  // one full-screen triangle, in front of no depth at all: it is the far plane
  draw(R, cam) {
    const gl = R.gpu.gl;
    const gpu = R.gpu;
    const s = skyResources(R);
    gpu.useProgram(s.program);
    gl.uniformMatrix4fv(s.program.u.uInvVP, false, cam.invViewProj);
    gl.uniform3fv(s.program.u.uCamPos, cam.eye);
    gl.uniform3fv(s.program.u.uSun, R.sun);
    gpu.withState({ depth: false, depthWrite: false }, () => {
      gl.bindBuffer(gl.ARRAY_BUFFER, s.vbo);
      gl.enableVertexAttribArray(s.aPos);
      gl.vertexAttribPointer(s.aPos, 2, gl.FLOAT, false, 0, 0);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      gl.disableVertexAttribArray(s.aPos);
    });
    R.stats.draws++;
  },
};

// Everything that moves is emitted here and submitted here: one pass, because
// where a hull is drawn relative to the terrain and to the props is one
// question, and because the emitters need the frame's camera and the frame's
// buffers in the same breath.
const world = {
  name: 'world',
  draw(R, cam, emit) {
    if (emit && R.takeEmit()) emit(R.batches);
    R.flushOpaque();
  },
};

// The blended sprites - dirt, sparks, fire, a missile's trail - after the opaque
// bodies and before the overlay.  Alpha blends, and no depth writes: a puff of
// dust must not hide the hull behind it.
const particles = {
  name: 'particles',
  draw(R, cam) {
    R.gpu.withState({ blend: true, depthWrite: false }, () => R.flushBlended());
  },
};

export const PASSES = [sky, terrain, props, world, particles, overlay];

export function drawFrame(R, cam, emit) {
  R.beginFrame(cam);
  for (const p of PASSES) p.draw(R, cam, emit);
}
