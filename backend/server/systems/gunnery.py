"""The sight leads, the machine follows.

A mech's aim is a free turret on the roof.  It answers the crew - a pilot's
mouse or the AI's intercept solution - *instantly*, and everything the crew
sees is seen through it: the eye the camera sits in (`mathutil.sight_eye`), the
IR seeker, and the beam a SACLOS missile rides.

The hull underneath and the arms beside it are steel on pivots, so they can only
catch up: `sight_yaw` is where the sight points, `yaw` swings after it at
`MECH_HULL_RATE` and the arms' `pitch` climbs after it at `MECH_ARM_RATE`.
Flick the view faster than that and the machine lags the cross - the barrels,
and the impact markers the client draws off them, trail behind the reticle for
a moment.

Because the sight stands `defs.MECH_SIGHT_Z` behind the hull's rotation axis,
that catch-up is not free of side effects: a hull swinging under a sight that
already points somewhere else carries the sight - and the pilot's eye - round
an arc.  `mathutil.sight_eye` is that geometry; this system is what makes it
happen.

Every other kind of unit aims with its whole mount and has nothing to catch up
with, so its sight is simply mirrored from the mount it aims with: the hull for
a rifleman or a civilian, the turret for a carrier.  That keeps one meaning for
`sight_yaw` on the wire - where the crew is looking - whoever is reading it.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu

if TYPE_CHECKING:
    from ..world import World


def run(world: World, dt: float) -> None:
    e = world.ecs
    hull_step = np.float32(defs.MECH_HULL_RATE * dt)
    arm_step = np.float32(defs.MECH_ARM_RATE * dt)
    for g in e.groups("sight_yaw", "sight_pitch", "yaw", "pitch", "turret_yaw", "etype"):
        n = g.n
        yaw = g.bufs["yaw"][:n]
        pitch = g.bufs["pitch"][:n]
        sy = g.bufs["sight_yaw"][:n]
        sp = g.bufs["sight_pitch"][:n]
        if "vehicle" in g.bufs:
            # a carrier's hull belongs to the driver - `yaw` is its steering
            # state - and the crew turns nothing but the turret, which the AI
            # turns directly: its sight *is* the turret
            sy[:] = g.bufs["turret_yaw"][:n]
            sp[:] = pitch
            continue
        mech = g.bufs["etype"][:n] == defs.ETYPE_MECH
        if not mech.any():
            # a rifle is aimed with the man's whole body: nothing lags anything
            sy[:] = yaw
            sp[:] = pitch
            continue
        rows = np.nonzero(mech)[0]
        if rows.size != n:
            rest = np.nonzero(~mech)[0]
            sy[rest] = yaw[rest]
            sp[rest] = pitch[rest]
        yaw[rows] = mu.turn_toward_arr(yaw[rows], sy[rows], hull_step)
        # the mount's mechanical stops: the sight may look higher than the arms
        # can elevate, and an arm sits against its stop until the sight comes
        # back into the envelope
        want = np.clip(sp[rows], defs.MECH_PITCH_MIN, defs.MECH_PITCH_MAX)
        pitch[rows] = np.clip(mu.turn_toward_arr(pitch[rows], want, arm_step),
                              defs.MECH_PITCH_MIN, defs.MECH_PITCH_MAX)
