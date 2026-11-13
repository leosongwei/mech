// The mech: legs on a turntable, a longitudinal hull with a cockpit grown out
// of its nose, tandem turbines on the roof, a commander's sight on a pillar and
// two high-mounted arms.
//
// The two halves that can disagree are drawn from two different angles - the
// feet from `e.leg` (the direction of travel), the upper body from `e.yaw` -
// and the sight head from a third, `e.syaw` / `e.spitch`.  The model reads all
// three and draws the joint between them.

import { MESH } from '../../render/mesh-table.js';
import { FORWARD, SIGHT_Z, at, headAt } from '../helpers.js';
import { METAL, METAL_D, METAL_L, GLASS, SOOT, facColor } from '../palette.js';
import { armModel } from '../arms.js';

export function mech(b, e, ctx) {
  const fac = facColor(e);
  const legYaw = e.leg;
  const yaw = e.yaw;
  const wdefs = ctx.defs || {};
  const flashes = ctx.flashes;
  const own = e.id === ctx.ownId;

  // --- legs ride on a turntable, not on the hull ----------------------
  // The upper body swings with the view while the feet keep the direction of
  // travel (the server turns them at 2.6 rad/s), so the two halves are bolted
  // to a pivot: a trapezoidal pedestal - a plain cuboid head-on - comes down
  // off the hull, a bearing ring and a yoke carry both legs.  Nothing else
  // bridges the joint, so the hull can swing right over its own feet.
  at(b, MESH.WEDGE, e, yaw, 0, 0, 1.88, -0.30, 2.30, 0.34, 1.44, METAL_D);
  at(b, MESH.CYL, e, legYaw, 0, 0, 1.63, -0.30, 1.12, 0.16, 1.12, METAL_L);
  at(b, MESH.BOX, e, legYaw, 0, 0, 1.40, -0.30, 1.05, 0.30, 2.60, METAL_D);

  // --- low duct legs / broad hover plenums (travel direction) ---------
  const legSegment = (a, c, width, depth, col, mesh = MESH.BOX) => {
    const dy = c[1] - a[1], dz = c[2] - a[2];
    const len = Math.hypot(dy, dz);
    const pitch = Math.atan2(-dz, dy);
    at(b, mesh, e, legYaw, pitch, (a[0] + c[0]) / 2, (a[1] + c[1]) / 2,
      (a[2] + c[2]) / 2, depth, len, width, col);
  };
  for (const side of [-1, 1]) {
    const hip = [side * 1.06, 1.38, -0.30];
    const knee = [side * 1.20, 0.98, 0.60];
    const ankle = [side * 1.28, 0.66, -0.02];
    const outer = (p) => [p[0] + side * 0.42, p[1], p[2] - 0.08];
    legSegment(hip, knee, 0.82, 1.00, METAL);
    legSegment(knee, ankle, 0.78, 0.90, METAL);
    legSegment(outer(hip), outer(knee), 0.28, 0.28, METAL_L, MESH.CYL);
    legSegment(outer(knee), outer(ankle), 0.25, 0.25, METAL_L, MESH.CYL);
    for (const p of [hip, knee, ankle]) {
      at(b, MESH.SPHERE, e, legYaw, 0, p[0], p[1], p[2], 0.60, 0.56, 0.60, METAL_D);
    }
    // A flat pressure chamber and a dark flexible skirt, not an upturned ski.
    at(b, MESH.BOX, e, legYaw, 0, side * 1.32, 0.34, 0, 4.60, 0.50, 1.20, METAL_D);
    at(b, MESH.CONE, e, legYaw, FORWARD, side * 1.32, 0.34, 2.40, 0.50, 0.55, 1.20, METAL_D);
    at(b, MESH.CONE, e, legYaw, -FORWARD, side * 1.32, 0.34, -2.40, 0.50, 0.55, 1.20, METAL_D);
    at(b, MESH.BOX, e, legYaw, 0, side * 1.32, 0.10, 0, 4.90, 0.18, 1.10, SOOT);
    at(b, MESH.BOX, e, legYaw, 0, side * 1.32, 0.61, 0, 3.65, 0.08, 0.82, METAL_L);
  }

  // --- narrow longitudinal hull / tandem vertical turbines ------------
  // One cuboid from the belly to the roof line: no separate raised deck, so
  // the hull's front cross-section is exactly what the cockpit grows out of.
  // The cockpit is that same block carried on to a short transverse line at
  // mid-height - roof and belly slope in (two trapezoids), the cheeks close
  // the sides (two triangles) - so it reads as a triangle from the side, a
  // trapezoid from above and an X head-on, never as a cone or as a smaller
  // block bolted onto the nose.
  at(b, MESH.BOX, e, yaw, 0, 0, 3.01, -1.10, 3.60, 2.12, 1.72, METAL);
  at(b, MESH.NOSE, e, yaw, 0, 0, 3.01, 1.425, 1.47, 2.12, 1.72, METAL);
  for (const side of [-1, 1]) {
    at(b, MESH.BOX, e, yaw, 0, side * 0.87, 3.00, -0.90, 2.30, 0.28, 0.05, fac);
  }
  for (const z of [-1.40, -2.40]) {
    at(b, MESH.CYL, e, yaw, 0, 0, 4.10, z, 1.04, 0.18, 1.04, METAL_D);
    at(b, MESH.CYL, e, yaw, 0, 0, 4.21, z, 0.78, 0.05, 0.78, SOOT);
    at(b, MESH.CONE, e, yaw, 0, 0, 4.30, z, 0.18, 0.18, 0.18, METAL_L);
  }
  at(b, MESH.CYL, e, yaw, FORWARD, 0, 2.90, -2.98, 0.75, 0.60, 0.75, METAL_D);
  at(b, MESH.CYL, e, yaw, FORWARD, 0, 2.90, -3.30, 0.54, 0.06, 0.54, SOOT);

  // Tank-style roof hatch on the flat roof, commander's panoramic sight behind
  // it.  The pillar lifts the head half a hatch diameter clear of the roof
  // line; the lid hinges forward over the nose, so an open hatch can never
  // foul the sight.
  at(b, MESH.CYL, e, yaw, 0, 0, 4.15, 0.25, 0.86, 0.16, 0.86, METAL_D);
  at(b, MESH.CYL, e, yaw, 0, 0, 4.24, 0.25, 0.70, 0.05, 0.70, METAL_L);
  if (!own) {
    // The head is a free turret: it stands on its pillar (whose axis is the
    // sight's, `SIGHT_Z` behind the hull's) and answers the view while the hull
    // is still coming round, so it is drawn on the *sight's* angles - the
    // snapshot's `syaw` / `spitch`, not the hull's.  It turns about the pillar
    // and tilts about its own centre, which is the eye point: nothing here can
    // move the camera that sits in it.
    const syaw = e.syaw === undefined ? yaw : e.syaw;
    const spitch = e.spitch === undefined ? 0 : e.spitch;
    // the pedestal and the hinge are the hull's: the pillar is a post, and the
    // head turns on top of it
    at(b, MESH.CYL, e, yaw, 0, 0, 4.16, SIGHT_Z, 0.52, 0.20, 0.52, METAL_D);
    at(b, MESH.BOX, e, yaw, 0, 0, 4.50, SIGHT_Z, 0.34, 0.56, 0.34, METAL_D);
    headAt(b, MESH.BOX, e, yaw, syaw, spitch, 0, 0, 0, 0.56, 0.62, 0.62, METAL_L);
    headAt(b, MESH.BOX, e, yaw, syaw, spitch, 0, 0.32, 0, 0.18, 0.10, 0.54, METAL_D);
    // One big sight window with a pair of small ones stacked beside it, set
    // into the head's front face - flush, so the block still reads as one
    // armoured box.  The big lens is the gunner's channel, the pair the
    // wide/narrow backups; the asymmetry is what tells the head apart from a
    // plain box.  The windows ride the tilted face, not the yaw plane.
    // (the windows are cylinders lying along the optical axis: their mesh
    // pitch is the forward one, tilted by the head's own elevation)
    const bore = FORWARD + spitch;
    headAt(b, MESH.CYL, e, yaw, syaw, spitch, 0.10, 0, 0.285, 0.36, 0.10, 0.36, GLASS, bore);
    for (const dy of [-0.16, 0.16]) {
      headAt(b, MESH.CYL, e, yaw, syaw, spitch, -0.20, dy, 0.285,
        0.16, 0.10, 0.16, GLASS, bore);
    }
  }

  // High, close-in weapon stations; module indices remain 2 / 3.
  for (const side of [-1, 1]) {
    const wd = wdefs[e.weapon ? e.weapon[side < 0 ? 0 : 1] : 0] || {};
    if (wd.rack) {
      // a launcher of tubes is slung on the arm's own pivot, so its link leaves
      // the hull *there* and meets the body dead centre: a link 0.3 m further
      // forward grabs a launcher standing at its own elevation under its lower
      // edge, which reads as clipping.  Longer, too - it has to reach into the
      // body, not stop at its face.
      at(b, MESH.BOX, e, yaw, 0, side * 1.04, 3.55, 0, 0.26, 0.26, 0.46, METAL_D);
    } else {
      at(b, MESH.BOX, e, yaw, 0, side * 0.99, 3.55, 0.30, 0.30, 0.34, 0.22, METAL_D);
    }
  }
  for (let arm = 0; arm < 2; arm++) armModel(b, e, arm, wdefs, flashes, ctx.now, 3.55, 1.43);
}
