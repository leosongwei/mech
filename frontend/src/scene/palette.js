// The colours of the field: a hull's metal, a house's concrete, a burnt hulk
// and the two sides of the war.
//
// `FACTION_COLORS` lives here because both ends of the picture have to agree on
// it - a hull's panels and the mark it makes on the minimap are the same
// faction, so `hud/` imports the table from this palette rather than keeping a
// copy of its own.

const METAL = [0.44, 0.46, 0.48];
const METAL_D = [0.28, 0.30, 0.33];
const METAL_L = [0.58, 0.60, 0.63];
const CONCRETE = [0.50, 0.51, 0.49];
const CONCRETE_D = [0.37, 0.38, 0.37];
const GLASS = [0.10, 0.19, 0.25];
const DEAD = [0.10, 0.10, 0.10];
const HULK = [0.13, 0.12, 0.11];    // burnt metal: wrecks and corpses
const SOOT = [0.09, 0.09, 0.09];

export { METAL, METAL_D, METAL_L, CONCRETE, CONCRETE_D, GLASS, DEAD, HULK, SOOT };

export const FACTION_COLORS = {
  0: [0.84, 0.22, 0.16],
  1: [0.20, 0.44, 0.92],
  2: [0.62, 0.36, 0.85],
  3: [0.86, 0.86, 0.84],
};

export function facColor(e) {
  return FACTION_COLORS[e.faction] || [0.8, 0.8, 0.8];
}

// the same colour, burnt down: a wreck and a corpse both keep their side
export function dim(c, k) {
  return [c[0] * k, c[1] * k, c[2] * k];
}
