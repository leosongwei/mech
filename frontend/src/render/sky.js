// The sky: one full-screen triangle, drawn first with no depth at all. Its
// colour is a function of the view direction the inverse view-projection
// recovers, so the gradient is anchored to the world rather than to the screen,
// and the sun's glow sits where the light on the ground says it does.

import { BufferGeometry, BufferAttribute, Mesh } from 'three';
import { skyMaterial } from './materials.js';

export class Sky {
  constructor(U, order) {
    const geometry = new BufferGeometry();
    geometry.setAttribute('position', new BufferAttribute(
      new Float32Array([-1, -1, 0, 3, -1, 0, -1, 3, 0]), 3));
    this.mesh = new Mesh(geometry, skyMaterial(U));
    this.mesh.frustumCulled = false;
    this.mesh.matrixAutoUpdate = false;
    this.mesh.renderOrder = order;
  }
}
