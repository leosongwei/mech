"""Weapon handling: magazines, reloads, cooldowns and projectile spawning.

The system only *fires* stored intent (the trigger bits) - aiming lives in the
player-input / AI systems, ballistics live in the projectile system.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import ballistics, components, defs, mathutil as mu, seeker

if TYPE_CHECKING:
    from ..world import World

KIND_ID = {"bullet": 0, "rocket": 1, "saclos": 2, "top": 3}

# weapon table as arrays, so a whole archetype of magazines can be refilled in
# one pass instead of a dict lookup per arm
MAG_OF = np.array([int(w["mag"]) for w in defs.WEAPONS], np.int32)


def aim_of(world: World, eid: int) -> tuple[tuple[float, float, float], float, float]:
    """(origin, yaw, pitch) of the *sight*: where this unit's crew is looking.

    For a mech that is the free turret on the roof - the eye the camera sits in
    (`mathutil.sight_eye`), the line the IR seeker sweeps, and the beam a SACLOS
    missile rides.  It answers the view instantly and is therefore *not* the
    barrels: those are on the hull and slew after it (`bore_of`), so ask for
    them at the muzzle, never here.
    """
    e = world.ecs
    pos = e["pos"][eid]
    etype = int(e["etype"][eid])
    if etype == defs.ETYPE_MECH:
        # the head is bolted to the hull, so the hull's yaw places the eye
        origin = mu.sight_eye(pos, float(e["yaw"][eid]))
        return origin, float(e["sight_yaw"][eid]), float(e["sight_pitch"][eid])
    if etype == defs.ETYPE_APC:
        yaw = float(e["turret_yaw"][eid])
        pitch = float(e["pitch"][eid])
        h = defs.UNIT_DEFS[etype]["height"]
        origin = (float(pos[0]), float(pos[1]) + h * 0.85, float(pos[2]))
        return origin, yaw, pitch
    h = defs.UNIT_DEFS.get(etype, {}).get("height", 1.8)
    return ((float(pos[0]), float(pos[1]) + h * 0.8, float(pos[2])),
            float(e["yaw"][eid]), float(e["pitch"][eid]))


def bore_of(world: World, eid: int) -> tuple[float, float]:
    """(yaw, pitch) the barrels of this unit are *actually* laid at.

    A mech's arms ride on the hull and elevate on their own pivots, so this is
    the hull's yaw and the arms' `pitch` - which trail the sight by up to the
    slew rates in `systems/gunnery.py`.  A round leaves along these, not along
    the sight line: what the cross promises and what the gun delivers only agree
    once the machine has caught up.
    """
    e = world.ecs
    if int(e["etype"][eid]) == defs.ETYPE_APC:
        return float(e["turret_yaw"][eid]), float(e["pitch"][eid])
    return float(e["yaw"][eid]), float(e["pitch"][eid])


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.units()
    if ids.size == 0:
        return

    # ---- countdowns, a whole archetype at a time -----------------------
    # Every arm counts down on its own: no row looks at another, so this is one
    # pass per group instead of two Reads per (unit, arm).  A reload that lands
    # on this tick refills the magazine here and keeps the arm from firing this
    # tick, which is what the old per-entity `continue` meant.
    just_reloaded: set[int] = set()
    for g in e.groups("cool", "reload_t", "mag", "ammo", "weapon"):
        n = g.n
        cool = g.bufs["cool"][:n]
        reload_t = g.bufs["reload_t"][:n]
        mag = g.bufs["mag"][:n]
        ammo = g.bufs["ammo"][:n]
        weapon = g.bufs["weapon"][:n]

        hot = cool > 0.0
        if hot.any():
            np.subtract(cool, dt, out=cool, where=hot)
            np.maximum(cool, 0.0, out=cool, where=hot)

        ticking = reload_t > 0.0
        if not ticking.any():
            continue
        np.subtract(reload_t, dt, out=reload_t, where=ticking)
        done = ticking & (reload_t <= 0.0)
        if not done.any():
            continue
        take = np.minimum(MAG_OF[weapon], ammo)
        np.copyto(mag, take, where=done)
        np.copyto(ammo, ammo - take, where=done)
        np.copyto(reload_t, 0.0, where=done)
        for row, arm in zip(*np.nonzero(done)):
            just_reloaded.add(int(g.ids[row]) * 2 + int(arm))

    # ---- firing, in ascending id order --------------------------------
    # `fire` draws the spread from `world.rng`, so the order of these pulls is
    # part of the simulation: keep the query order, do not group it.
    trigger = e["trigger"]
    trigger_prev = e["trigger_prev"]
    owner = e["owner"]
    # the AI's own target (`systems/ai.py` writes it): what a lock weapon on a
    # hull nobody is driving takes its lock from
    ai_target = e["target"]
    rows, archs = e.locate(ids)
    arm_bufs = e.buffers("arm_hp")
    cool_bufs = e.buffers("cool")
    reload_bufs = e.buffers("reload_t")
    mag_bufs = e.buffers("mag")
    ammo_bufs = e.buffers("ammo")
    weapon_bufs = e.buffers("weapon")

    for k in range(ids.size):
        eid = int(ids[k])
        ai = int(archs[k])
        row = int(rows[k])
        cool = cool_bufs[ai]
        reload_t = reload_bufs[ai]
        mag = mag_bufs[ai]
        ammo = ammo_bufs[ai]
        weapon = weapon_bufs[ai]
        trig = int(trigger[eid])
        if trig == 0:
            # nothing is being asked for: still drop the edge state (a released
            # trigger is what re-arms a semi automatic arm) and move on
            if trigger_prev[eid]:
                trigger_prev[eid] = 0
            continue
        for arm in (0, 1):
            if reload_t[row, arm] > 0.0:
                continue                       # still reloading
            if just_reloaded and eid * 2 + arm in just_reloaded:
                continue                       # finished this very tick
            wid = int(weapon[row, arm])
            if wid == defs.WID_NONE:
                continue
            if not (trig >> arm) & 1:
                continue
            arm_hp = arm_bufs[ai]
            if arm_hp is not None and float(arm_hp[row, arm]) <= 0.0:
                continue                       # that arm was shot off
            wd = defs.WEAPONS[wid]
            # semi automatic arms fire once per pull: the trigger has to be
            # released and pressed again, holding it does nothing
            if wd["semi"] and (int(trigger_prev[eid]) >> arm) & 1:
                continue
            if int(mag[row, arm]) <= 0:
                if int(ammo[row, arm]) > 0:
                    reload_t[row, arm] = wd["reload"]
                continue
            # a hair of float dust left over from the tick subtraction must
            # not cost a whole tick: 600 rpm on a 30 Hz clock is exactly 3
            # ticks, and 0.1 - 3 * dt rounds to a positive 1e-9
            if float(cool[row, arm]) > 1e-4:
                continue
            target = -1
            if wd["lock"]:
                pl = world.players.get(int(owner[eid]))
                if pl is not None:
                    if pl.lock_target < 0 or pl.lock_progress < 1.0:
                        continue
                    if not e.alive[pl.lock_target]:
                        continue
                    target = pl.lock_target
                else:
                    # an AI crew has no seeker display to watch: its sight is
                    # already on the hull it is engaging (the trigger waits for
                    # the bore), so the round leaves the rack holding that lock
                    tid = int(ai_target[eid])
                    if tid < 0 or not e.alive[tid]:
                        continue
                    target = tid
            fire(world, eid, arm, wid, wd, target)
            mag[row, arm] = int(mag[row, arm]) - 1
            cycle = 60.0 / max(wd["rpm"], 1.0)
            if wd["lock"] and int(owner[eid]) < 0:
                # an AI crew does not empty a rack of fire-and-forget rounds at
                # the rack's own cycle: it launches one, watches it arrive, and
                # only then thinks about the next.  Eight tubes at 0.5 s was the
                # whole nest gone in four seconds; at `AI_LAUNCH_GAP` a top
                # attack is something that keeps coming back instead
                cycle = max(cycle, defs.AI_LAUNCH_GAP)
            cool[row, arm] = cycle
        trigger_prev[eid] = trig


def fire(world: World, eid: int, arm: int, wid: int, wd: dict, target: int = -1) -> None:
    """Spawn the projectile(s) of one trigger pull."""
    e = world.ecs
    pos = e["pos"]
    etype = int(e["etype"][eid])
    # the fire control solves from the sight's eye, the round leaves the mouth
    # of the barrel: two points, and with the machine lagging the sight, two
    # headings as well
    origin, _, _ = aim_of(world, eid)
    yaw, pitch = bore_of(world, eid)

    kind = KIND_ID.get(wd["kind"], 0)
    n = max(1, int(wd["salvo"]))
    life = float(wd["life"])

    # where the tubes point, which is the one thing that decides the mouth the
    # round leaves and the heading it leaves on: a launcher bolted at its own
    # quadrant stands there whatever the arm does (the top attack's 42 deg is
    # the round's own loft), one carried on the arm adds its elevation bias to
    # the arm's aim, and one mounted flat (`elev` 0, the rocket nest) is simply
    # the arm - depression included, or a nest could never bear on anything
    # closer than its own range setting.  Everything is drawn on this angle as
    # well, so the mouth drawn is the mouth fired from.
    rack = wd.get("rack")
    elev = float(rack["elev"]) if rack else 0.0
    pitch = elev if rack and rack.get("bolted") else pitch + elev

    right = up = 0.0
    if etype == defs.ETYPE_MECH:
        # where the bore ends, in order of who knows best: the weapon itself
        # (the 105 mm's barrel runs 5 m past its receiver), then its rack (a
        # launcher of tubes ends at the mouth of the tubes), then the kind's
        # figure.
        reach = wd.get("reach")                     # the weapon's own bore
        if reach is None:
            # a launcher of tubes ends at the mouth of the tubes
            reach = rack["mouth"] if rack else defs.MECH_ARM_REACH.get(wd["kind"], 1.8)
        reach = float(reach)
        if rack:
            # a launcher of tubes is one round per tube: the round leaves the
            # tube its slot in the rack belongs to, so a half-empty pod fires
            # out of its upper rows.  `mag` is still the pre-shot count here -
            # the loop takes the round off the top of this call.
            right, up = defs.rack_tube(int(wd["mag"]) - int(e["mag"][eid][arm]), rack)
        origin = mu.muzzle_point(pos[eid], yaw, defs.MOD_LEFT if arm == 0 else defs.MOD_RIGHT,
                                 pitch, reach, right, up)

    # direct fire is zeroed: hold off for the drop so the round meets the sight
    # line at the pilot's setting.  A pilot's gun uses that fixed range, which
    # keeps the barrel still while the sight crosses a crest; the AI solves
    # against the target it is aiming at, because it never has to read a
    # reticle.  `bias` was the launcher's mechanical sighting and is absorbed
    # by the solution; guided rounds keep it.
    zero = None
    if kind in (0, 1):
        pl = world.players.get(int(e["owner"][eid]))
        if pl is not None:
            zero = ballistics.zero_at(origin, yaw, pitch, wd, pl.zero_range)
        else:
            zero = ballistics.zero_pitch(world, origin, yaw, pitch, wd, ignore=eid,
                                         faction=int(e["faction"][eid]))

    for k in range(n):
        sp = float(wd["spread"]) * (1.0 if k == 0 else 0.6)
        dy = yaw + world.rng.gauss(0.0, sp)
        # `bias` is the mechanical elevation the launcher is sighted at: the
        # rocket pod is tilted up so its slow round meets the cannon at 1 km
        dp = (pitch + float(wd["bias"]) if zero is None else zero) + world.rng.gauss(0.0, sp)
        d = mu.dir_from(dy, dp)
        # a missile is its own archetype: it carries a pool of health and its
        # own armour, so it can be fused, holed and killed (components.MISSILE_COMPS)
        if defs.is_interceptable(wid):
            pid = e.create(components.MISSILE_COMPS)
            hp = float(wd["aim_hp"])
            e["hp"][pid] = hp
            e["hpmax"][pid] = hp
            e["armor"][pid] = (float(wd["aim_dp"]), float(wd["aim_de"]))
            # the seeker leaves the launcher already knowing what it is looking
            # at, and how fast that thing was going: from here on the round owns
            # the lock (`seeker.py`), and its memory is what carries it through
            # a lock cut before the first tick of flight.  Only the guided kind
            # has a seeker; a beam rider and a rocket ride their own guidance.
            if kind == 3 and target >= 0 and e.alive[target]:
                e["tgt_mem"][pid] = seeker.roof(world, target)
                e["tgt_vel"][pid] = e["vel"][target]
        else:
            pid = e.create(components.PROJ_COMPS)
        e["pos"][pid] = origin
        e["vel"][pid] = (d[0] * wd["vel"], d[1] * wd["vel"], d[2] * wd["vel"])
        e["faction"][pid] = e["faction"][eid]
        e["proj_kind"][pid] = kind
        e["proj_wid"][pid] = wid
        e["proj_life"][pid] = life
        e["proj_src"][pid] = eid
        # the credit travels with the round, not with the hull that fired it:
        # a cannon round can outlive the machine by a second and a rocket by
        # several, and a kill it lands is still the pilot's
        e["proj_pid"][pid] = int(e["owner"][eid])
        e["proj_name"][pid] = e["name_id"][eid]
        e["proj_etype"][pid] = e["etype"][eid]
        e["proj_age"][pid] = 0.0
        e["proj_seed"][pid] = world.rng.random()
        e["proj_trav"][pid] = 0.0
        e["target"][pid] = target
    world.emit("shot", x=origin[0], y=origin[1], z=origin[2], wid=int(wid), eid=int(eid), arm=int(arm))
