// The one group of tolerance constants.  Each host keeps its own copy of these
// numbers (here, and the `Tol` class in `tests/backend/Support/MiniTest.cs`) and
// they are the same numbers:
// they are a contract, not a taste, so changing one without the other is the bug the
// cross-side comparison is meant to catch.
//
// Why these figures: the two implementations are ~120 lines of the same formula, so
// what separates them is *policy inside the solver* (a convergence threshold, an
// iteration count, a ray's step), not floating point - measured, float32 against
// float64 moves the answer by 0.13 mm over a 4 km shot.  And what a hull can absorb
// is much larger: a 30 mm shell's own dispersion is 1.1 m at 1 km.
export const TOL = {
  // m, how far apart two hosts' intercept points may be (grows with the range, since
  // so does everything the solve is made of)
  point: (dist) => Math.max(0.25, 1e-4 * dist),
  // s, the time of flight
  tof: 0.02,
  // rad, the bore elevation and the two sight angles (1 mrad is 1 m at 1 km)
  angle: 1e-3,
  // L1: the round must pass inside half the target's own radius
  miss: (radius) => 0.5 * radius,
};
