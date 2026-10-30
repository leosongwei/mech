"""AI for everything that is not player controlled.

Units acquire targets on a slow timer (0.45 s, LOS validated), engage with
lead & ballistic compensation and otherwise push along their lane towards the
enemy command base.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu
from . import sight

if TYPE_CHECKING:
    from ..world import World

# weapon data as arrays, by `wid`: the radar cross-section, the collision sphere
# and the fuse radius.  A round only ever gets a table row through
# `world.interceptable()`, so a bullet's zeros are never read.
RCS = np.asarray([w.get("rcs", 0.0) for w in defs.WEAPONS])
HIT_R = np.asarray([w.get("hit_r", 0.0) for w in defs.WEAPONS])
PROX = np.asarray([w.get("prox", 0.0) for w in defs.WEAPONS])

ACQUIRE = 0.45
SPOT_MEMORY = 4.0              # seconds an AI keeps firing at a lost target's spot

# ---- missile defence ----------------------------------------------------
# A hull with a radar (`defs.UNIT_DEFS[...]["radar_strength"]`) sees a missile
# coming long before it is danger, and the crew's first reaction is to shoot at
# it - but only with a weapon that can actually bring it down, i.e. one whose
# fuse (`prox`) covers the miss distance a burst of cannon fire will have.  The
# rule is data driven on purpose: a new fused weapon joins in with no change
# here, and a 12.7 mm carrier (2.25 mrad of spread, no fuse) stays out of it.
INTERCEPT_RANGE = 1500.0       # m: inside the gun's reach it is worth the ammo
CPA_HIT = 40.0                 # m: closer than this and the round is coming for me
# m: a guided round with no lock flies at the point it last saw its target, and
# that point is inside this much of the hull it is still coming for (`_threat`)
CPA_MEM = 100.0


def run(world: World, dt: float, hulls=None) -> None:
    """Drive every AI crew of the world - or only the ones in `hulls`.

    The filter is what a mode whose fixtures must stay passive uses: the range
    runs the ground AI for the hulls a pilot dropped there and for nothing else
    (`modes/range/test_ai.py`), while its targets and its defence post are
    driven by their own systems.
    """
    e = world.ecs
    # player and AI hulls alike, civilians included
    ids = world.movable() if hulls is None else np.asarray(hulls, np.int64)
    if ids.size == 0:
        return

    # the two views the per-entity loop below needs by id: everything else it
    # takes from `ecs.slot()` and indexes by row
    pos = e["pos"]
    vel = e["vel"]

    terrain = world.terrain
    # who may be shot at is a query, not a type switch: `combat` hulls only
    # (wrecks, corpses, civilians and smoke are not in here by construction).
    # One id-indexed array rather than a list per faction: `_acquire` reads it
    # for the cells around the shooter, not to filter a roster.  Built from the
    # archetypes so it costs no per-id gather.
    combat_fac = np.full(e.alive.size, -1, np.int8)
    for g in e.groups("combat", "faction"):
        combat_fac[g.ids] = g.bufs["faction"][:g.n]

    # the rounds in the air, once: any crew with a radar looks this over before
    # it goes back to its ground fight
    mis = _missiles(world)

    # per entity: one `slot` lookup, then every component is `bufs["x"][row]`.
    # The order is the query order (ascending ids) because this loop draws the
    # random numbers, so `groups` would reshuffle the whole battle.
    for eid in ids:
        b, row = e.slot(eid)
        if b.get("owner") is not None and int(b["owner"][row]) >= 0:
            continue                                   # player driven
        if "combat" not in b:
            _civ(world, eid, b, row, dt)
            continue

        t = int(b["etype"][row])
        fac = int(b["faction"][row])
        d = defs.UNIT_DEFS[t]
        speed = d["speed"]
        px, py, pz = float(b["pos"][row, 0]), float(b["pos"][row, 1]), float(b["pos"][row, 2])
        if t == defs.ETYPE_MECH:
            # a mech looks through its sight, on the roof, not out of its chest
            eye = mu.sight_eye(b["pos"][row], float(b["yaw"][row]))
        else:
            eye = (px, py + d["height"] * 0.75, pz)

        timer = b["ai_timer"]
        target = b["target"]
        spot_t = b["spot_t"]
        timer[row] -= dt
        tid = int(target[row])
        if timer[row] <= 0.0:
            timer[row] = ACQUIRE * world.rng.uniform(0.75, 1.35)
            tid = _acquire(world, fac, combat_fac, eye, d)
            target[row] = tid
        if tid >= 0:
            if not e.alive[tid] or int(e["faction"][tid]) == fac:
                target[row] = -1
                tid = -1

        # a round that is going to hit this hull outranks whatever it was
        # shooting at: the crew turns the gun on it and keeps driving
        covering = mis is not None and _missile_step(world, eid, b, row, mis, dt)

        moving = True
        seen = False
        if not covering and tid >= 0:
            tpos = pos[tid]
            tvel = vel[tid]
            dist = math.dist((eye[0], eye[2]), (float(tpos[0]), float(tpos[2])))
            if dist < d["ai_range"] and _sees(world, eye, (float(tpos[0]), float(tpos[1]) + d["height"] * 0.6, float(tpos[2]))):
                moving = dist > d["ai_range"] * 0.55
                seen = True
                # remember the spot: if the sight breaks (smoke, a crest) the
                # crew keeps shooting there for a few seconds
                b["spot_x"][row] = float(tpos[0])
                b["spot_y"][row] = float(tpos[1])
                b["spot_z"][row] = float(tpos[2])
                spot_t[row] = SPOT_MEMORY
                _engage(world, eid, b, row, tid, eye, d, dist, tpos, tvel, dt)
        if not covering and not seen and float(spot_t[row]) > 0.0:
            # lost it: hose the spot where it was last seen
            spot_t[row] = float(spot_t[row]) - dt
            dist = math.dist((eye[0], eye[2]),
                             (float(b["spot_x"][row]), float(b["spot_z"][row])))
            if dist < d["ai_range"]:
                _suppress(world, eid, b, row, eye, d, dist, dt)
                moving = False

        if moving:
            if not covering:                  # `_missile_step` owns the trigger
                b["trigger"][row] = 0
            if "raider" in b:
                # a raiding party has a route of its own, off the lanes: the
                # mode's raid system (modes/battle/raids.py) writes its
                # velocity.  Everything above - acquire, lead, fire - it shoots
                # with the same crew as any other hull.
                pass
            elif "vehicle" in b:
                _advance_route(world, b, row, speed)   # a car follows the lane
            else:
                _advance(world, eid, b, row, speed, dt)
        elif "vehicle" in b:
            # close enough to shoot: a carrier brakes for that, it does not
            # roll on at the last commanded speed the way a walking hull's
            # leftover velocity does
            b["vel"][row, 0] = b["vel"][row, 2] = 0.0


def _missiles(world: World) -> dict | None:
    """The rounds in the air that a radar can see, as flat arrays.

    Built once a tick: nothing here is per-shooter, and at 30 Hz a battle has a
    few dozen of them at most.  `rcs` and the fuse radius come off the weapon
    table by `wid`, so a mission that fits a new missile needs no edit here.
    """
    ids = world.interceptable()
    if ids.size == 0:
        return None
    e = world.ecs
    wid = np.asarray(e["proj_wid"][ids], np.int64)
    return dict(
        ids=np.asarray(ids, np.int64),
        pos=np.asarray(e["pos"][ids], np.float64),
        vel=np.asarray(e["vel"][ids], np.float64),
        fac=np.asarray(e["faction"][ids], np.int64),
        kind=np.asarray(e["proj_kind"][ids], np.int64),
        target=np.asarray(e["target"][ids], np.int64),
        # where an unlocked round is staring: its target's last known position,
        # still walking at the target's last known velocity (`seeker.py`)
        mem=np.asarray(e["tgt_mem"][ids], np.float64),
        rcs=RCS[wid],
        hit_r=HIT_R[wid],
        prox=PROX[wid],
    )


def intercept(world: World, dt: float) -> None:
    """Missile defence on its own, for a mode whose hulls do not fight.

    `ai.run` calls the same step inline; this is the version a test range wants
    - a single AI hull that shoots down what is fired at it and never picks a
    ground fight.

    The trigger is cleared here, because in a mode without `ai.run` nothing else
    owns it - and it is cleared *before* the empty-sky test, not after: a round
    shot down last tick leaves the gun laid on the patch of air it came apart
    in, and a trigger that is never dropped would go on firing at it.
    """
    mis = _missiles(world)
    e = world.ecs
    for eid in world.movable():
        b, row = e.slot(eid)
        if b.get("owner") is None or int(b["owner"][row]) >= 0:
            continue                                # player driven
        if "combat" not in b:
            continue
        b["trigger"][row] = 0
        if mis is not None:
            _missile_step(world, eid, b, row, mis, dt)


def _missile_step(world: World, eid: int, b: dict, row: int, mis: dict,
                  dt: float) -> bool:
    """Turn the gun on the round that is coming, if there is one.  True if the
    crew took the shot - the caller then leaves its ground target alone."""
    d = defs.UNIT_DEFS[int(b["etype"][row])]
    strength = float(d.get("radar_strength", 0.0))
    if strength <= 0.0:
        return False
    arm = _fused_arm(b, row)
    if arm < 0:
        return False                                # nothing aboard can down it
    etype = int(b["etype"][row])
    px, py, pz = (float(b["pos"][row, 0]), float(b["pos"][row, 1]),
                  float(b["pos"][row, 2]))
    if etype == defs.ETYPE_MECH:
        eye = mu.sight_eye(b["pos"][row], float(b["yaw"][row]))
    else:
        eye = (px, py + d["height"] * 0.75, pz)
    k = _threat(world, mis, strength, eye, eid,
                np.asarray(b["vel"][row], np.float64), int(b["faction"][row]))
    if k < 0:
        return False
    tpos = mis["pos"][k]
    tvel = mis["vel"][k]
    dist = float(math.dist(eye, tpos))
    wd = defs.WEAPONS[int(b["weapon"][row, arm])]
    # lead the intercept exactly as a hull is led: the same solver, the same
    # "fire control zeroes the drop at shot time" (so no lift here)
    aim = mu.solve_lead(eye, tpos, tvel, float(wd["vel"]), 0.0)
    want_yaw, want_pitch = mu.angles_to(aim[0] - eye[0], aim[1] - eye[1],
                                        aim[2] - eye[2])
    trigger = b["trigger"]
    if etype == defs.ETYPE_MECH:
        want_pitch = max(defs.MECH_PITCH_MIN, min(defs.MECH_PITCH_MAX, want_pitch))
        b["sight_yaw"][row] = want_yaw
        b["sight_pitch"][row] = want_pitch
        aim_yaw = float(b["yaw"][row])
        laid = abs(want_pitch - float(b["pitch"][row])) < 0.02
    elif etype == defs.ETYPE_APC:
        b["turret_yaw"][row] = mu.turn_toward(float(b["turret_yaw"][row]),
                                              want_yaw, 2.2 * dt)
        b["pitch"][row] = want_pitch
        aim_yaw = float(b["turret_yaw"][row])
        laid = True
    else:
        b["yaw"][row] = mu.turn_toward(float(b["yaw"][row]), want_yaw, 3.2 * dt)
        b["pitch"][row] = want_pitch
        aim_yaw = float(b["yaw"][row])
        laid = True
    # a burst weapon is aimed at an *area*, not a point: the tolerance is the
    # angle the fuse covers at this range, ~0.004 rad at 1 km, not the 0.14 rad
    # a hull gets (a shell that misses a hull's 1.45 m body by a metre still
    # counts, and a round that misses a missile by a metre is a miss)
    tol = math.atan((float(wd["prox"]) + float(mis["hit_r"][k])) / max(dist, 1.0))
    err = abs(mu.wrap_pi(want_yaw - aim_yaw))
    if err < tol and laid:
        trigger[row] = 1 << arm
    else:
        trigger[row] = 0
    return True


def _fused_arm(b: dict, row: int) -> int:
    """Which arm can bring a missile down: the first that carries a fuse and
    still has rounds and metal.  -1 if none can."""
    weapon = b["weapon"]
    mag = b["mag"]
    arm_hp = b.get("arm_hp")
    for arm in (0, 1):
        wid = int(weapon[row, arm])
        if not wid or PROX[wid] <= 0.0:
            continue
        if arm_hp is not None and float(arm_hp[row, arm]) <= 0.0:
            continue
        if int(mag[row, arm]) <= 0:
            continue
        return arm
    return -1


def _threat(world: World, mis: dict, strength: float, eye, eid: int,
            ovel: np.ndarray, fac: int) -> int:
    """Index of the round worth shooting at, or -1.

    Two questions, both of them `plan.md`'s:

    * **can I see it** - `d^2 <= K * v_closing` with `K = sqrt(S * rcs) / V_REF`
      (`defs.radar_k`).  A round that is opening out or crossing gives a
      non-positive closing speed, so the right-hand side is <= 0 and the test
      fails by itself: no separate "is it getting closer" clause, and no
      `RADAR_SPEED` threshold.
    * **is it for me** - a top-attack round carries the id of the hull its seeker
      locked, so it is asked directly; everything else is judged by geometry,
      the closest point of its straight-line approach.  A `t_cpa` gate would say
      nothing: the moment a round is first *seen* it is 1.1-2.6 s from its
      closest approach whatever it is doing, so any threshold either always
      passes or throws away the genuinely dangerous ones.  A guided round whose
      lock has been cut has no id to ask, but it is not harmless - it is flying
      at its memory, and that memory is a point near this hull or it is not
      (`CPA_MEM`), so the crew keeps shooting at the screen it is coming
      through instead of letting go the moment the smoke goes up.
    """
    r = mis["pos"] - np.asarray(eye, np.float64)
    vrel = mis["vel"] - ovel
    d2 = np.einsum("ij,ij->i", r, r)
    vc = -np.einsum("ij,ij->i", r, vrel) / np.sqrt(np.maximum(d2, 1e-9))
    kk = np.sqrt(np.maximum(strength * mis["rcs"], 0.0)) / defs.RADAR_V_REF
    want = (d2 <= kk * vc) & (mis["fac"] != fac) & (d2 <= INTERCEPT_RANGE ** 2)
    if not want.any():
        return -1
    w2 = np.maximum(np.einsum("ij,ij->i", vrel, vrel), 1e-9)
    tcpa = np.clip(-np.einsum("ij,ij->i", r, vrel) / w2, 0.0, None)
    cpa = np.linalg.norm(r + vrel * tcpa[:, None], axis=1)
    # top attack: it climbs 150 m over its victim and dives on the roof, so its
    # straight line passes nowhere near him - the seeker's own lock is the truth,
    # and with the lock cut, the point it is still staring at is the other half
    # of the same answer
    mine = (mis["target"] == eid) | (
        np.linalg.norm(mis["mem"] - np.asarray(eye, np.float64), axis=1) < CPA_MEM)
    want &= np.where(mis["kind"] == 3, mine, cpa < CPA_HIT)
    if not want.any():
        return -1
    return int(np.argmin(np.where(want, d2, np.inf)))


def _sees(world: World, eye: tuple[float, float, float],
          point: tuple[float, float, float]) -> bool:
    """Line of sight: whatever the shared sight rule says (ground, buildings,
    tree canopies and smoke)."""
    return sight.clear(world, eye, point)


def _acquire(world: World, fac: int, combat_fac: np.ndarray,
             eye: tuple[float, float, float], d: dict) -> int:
    """Nearest hostile unit with line of sight.

    A hull may declare a restricted target list in its definition (`targets`):
    a rifleman only picks a fight with other infantry and leaves armour alone,
    because 15 hp in the open is not a threat to a tank and the crew knows it.

    The candidates come from the grid, not from a roster: a 5000-unit battle
    puts a few dozen ids in the box around the shooter instead of a few
    thousand in the array to sort.  The box covers the whole sight radius, so
    the answer is the same one the full scan gave.  No position is gathered
    here - the grid carries its own copy, in the same order as its ids.
    """
    grid = world.grid
    ci, cj = grid.cell_of(eye[0], eye[2])
    ids, p = grid.box(ci, cj, int(d["sight"] * grid.inv) + 1)
    if ids.size == 0:
        return -1
    cf = combat_fac[ids]
    keep = (cf >= 0) & (cf != fac)
    want = d.get("targets")
    if want:
        keep &= np.isin(world.ecs["etype"][ids], np.asarray(want, np.uint8))
    ds = np.hypot(p[:, 0] - eye[0], p[:, 2] - eye[2])
    keep &= ds <= d["sight"]
    if not keep.all():
        ids, p, ds = ids[keep], p[keep], ds[keep]
        if ids.size == 0:
            return -1
    for k in np.argsort(ds)[:6]:
        tid = int(ids[k])
        tp = p[k]
        if _sees(world, eye, (float(tp[0]), float(tp[1]) + 1.5, float(tp[2]))):
            return tid
    return -1


def _engage(world: World, eid: int, b: dict, row: int, tid: int,
            eye: tuple[float, float, float], d: dict, dist: float,
            tpos: np.ndarray, tvel: np.ndarray, dt: float) -> None:
    # aim at the intercept point of the *slowest round that can reach*.  The
    # sight is one line whatever the arms carry, and a guided round is aimed
    # with it (a beam rider rides it), so leading for a 1080 m/s cannon shell
    # while the right arm is a missile that takes seconds to arrive put the
    # missile consistently short of a moving hull.  no gravity lift here: the
    # fire control zeroes the gun for the drop at shot time, so lifting the
    # aim as well would double compensate
    speed = 0.0
    for arm in (0, 1):
        wid = int(b["weapon"][row, arm])
        if not wid:
            continue
        wd = defs.WEAPONS[wid]
        if dist > float(wd["range"]):
            continue
        v = mu.mean_speed(wd, dist)
        speed = v if speed <= 0.0 else min(speed, v)
    if speed <= 0.0:
        speed = float(defs.WEAPONS[int(b["weapon"][row, 0]) or defs.WID_AC30]["vel"])
    aim = mu.solve_lead(eye, (float(tpos[0]), float(tpos[1]) + 2.0, float(tpos[2])),
                        tvel, speed, 0.0)
    _aim_and_fire(world, eid, b, row, eye, d, dist, aim, dt)


def _suppress(world: World, eid: int, b: dict, row: int,
              eye: tuple[float, float, float], d: dict, dist: float,
              dt: float) -> None:
    """Keep firing at the spot where the target was last seen."""
    aim = (float(b["spot_x"][row]), float(b["spot_y"][row]) + 1.5,
           float(b["spot_z"][row]))
    _aim_and_fire(world, eid, b, row, eye, d, dist, aim, dt)


def _aim_and_fire(world: World, eid: int, b: dict, row: int,
                  eye: tuple[float, float, float], d: dict, dist: float,
                  aim: tuple[float, float, float], dt: float) -> None:
    yaw = b["yaw"]
    pitch = b["pitch"]
    turret = b["turret_yaw"]
    sight_yaw = b["sight_yaw"]
    sight_pitch = b["sight_pitch"]
    trigger = b["trigger"]
    etype = int(b["etype"][row])
    hp = b["hp"]
    arm_hp = b.get("arm_hp")
    weapon = b["weapon"]

    tx = aim[0] - eye[0]
    ty = aim[1] - eye[1]
    tz = aim[2] - eye[2]
    want_yaw, want_pitch = mu.angles_to(tx, ty, tz)

    if etype == defs.ETYPE_MECH:
        # the crew lays the sight on the intercept; the hull and the arms slew
        # after it at their own rates (systems/gunnery.py), which is why the
        # trigger below waits for the *bore*, not for the sight.  A shot the
        # mounts cannot elevate to is one they never take: hold the sight at
        # the stop instead of driving the arms against it for ever
        want_pitch = max(defs.MECH_PITCH_MIN, min(defs.MECH_PITCH_MAX, want_pitch))
        sight_yaw[row] = want_yaw
        sight_pitch[row] = want_pitch
    elif etype == defs.ETYPE_APC:
        # the hull of a wheeled carrier belongs to the driver: movement points
        # it where it rolls, and the crew turns nothing but the turret
        turret[row] = mu.turn_toward(float(turret[row]), want_yaw, 2.2 * dt)
        pitch[row] = max(-0.4, min(0.7, float(pitch[row]) + (want_pitch - float(pitch[row])) * min(1.0, 6.0 * dt)))
    else:
        # a rifleman turns his whole body onto the target: no sight to lead
        yaw[row] = mu.turn_toward(float(yaw[row]), want_yaw, 3.2 * dt)
        pitch[row] = max(-0.4, min(0.7, float(pitch[row]) + (want_pitch - float(pitch[row])) * min(1.0, 6.0 * dt)))

    aim_yaw = float(turret[row]) if etype == defs.ETYPE_APC else float(yaw[row])
    err = abs(mu.wrap_pi(want_yaw - aim_yaw))
    # a mech's barrels are laid by the gunnery slew, not by this loop: the gun
    # opens up when the bore is actually on the intercept, so a hull still
    # coming round (or an arm still climbing) does not hose the dirt
    laid = etype != defs.ETYPE_MECH or abs(float(want_pitch) - float(pitch[row])) < 0.02
    t = 0
    if err < 0.14 and laid:
        if hp[row] > 0.0:
            t |= 1
        # the second arm exists only on a hull that carries arm pools, and
        # only fires while that arm is still there (`combat.arm_alive`, but
        # the buffers are already in hand here, so no entity lookup)
        has_arm = arm_hp is not None and float(arm_hp[row, 1]) > 0.0
        if has_arm and dist < d["ai_range"]:
            # the right arm fires whatever it carries, as far as that round
            # reaches: 2.2 km was the rocket nest's hand-off, but a racked
            # launcher or a 105 mm barrel is no less ready at the crew's own
            # engagement range (`ai_range`), so only the two old arms keep it
            w1 = int(weapon[row, 1])
            if w1:
                wd1 = defs.WEAPONS[w1]
                reach = 2200.0 if wd1["kind"] in ("rocket", "saclos") else d["ai_range"]
                if dist < min(reach, float(wd1["range"])):
                    t |= 2
        if etype == defs.ETYPE_APC and dist < 3500:
            t |= 2
    # a semi automatic arm only fires on the rising edge of the trigger, so
    # the AI has to let go of it every other tick
    if (t & 2) and (world.tick & 1) and defs.WEAPONS[int(weapon[row, 1])]["semi"]:
        t &= ~2
    trigger[row] = t


def _advance(world: World, eid: int, b: dict, row: int, speed: float,
             dt: float) -> None:
    pos = b["pos"]
    vel = b["vel"]
    terrain = world.terrain
    fac = int(b["faction"][row])
    lane_i = int(b["lane"][row])
    px, pz = float(pos[row, 0]), float(pos[row, 2])
    bx, bz = terrain.bases[1 - fac]
    if not world.lanes:
        # a map with no lanes at all (the range): there is nothing to follow,
        # so the hull walks straight at the enemy post
        dx, dz = bx - px, bz - pz
    else:
        pts = world.lanes[lane_i % len(world.lanes)]
        if fac == defs.FACTION_BLUE:
            pts = world.lanes_rev[lane_i % len(world.lanes)]
        i = int(b["path_i"][row])
        if i >= len(pts):
            i = len(pts) - 1
        wx, wz = pts[i]
        dx = wx - px
        dz = wz - pz
        if dx * dx + dz * dz < 55.0 * 55.0:
            if i < len(pts) - 1:
                i += 1
                b["path_i"][row] = i
                wx, wz = pts[i]
                dx = wx - px
                dz = wz - pz
            elif i >= len(pts) - 1:
                dx, dz = bx - px, bz - pz
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[row, 0] = vel[row, 2] = 0.0
        return
    dx, dz = dx / d, dz / d
    # simple obstacle avoidance: fan out when the step ahead is blocked
    step = 14.0
    if terrain.blocked(px + dx * step, pz + dz * step):
        base = math.atan2(dz, dx)
        for off in (0.5, -0.5, 1.0, -1.0, 1.6, -1.6, 2.2, -2.2):
            a = base + off
            nx, nz = math.cos(a), math.sin(a)
            if not terrain.blocked(px + nx * step, pz + nz * step):
                dx, dz = nx, nz
                break
    speed *= 1.0
    vel[row, 0] = dx * speed
    vel[row, 2] = dz * speed


# ---------------------------------------------------------------------------
# wheeled route following
# ---------------------------------------------------------------------------
# wheeled route following
# ---------------------------------------------------------------------------
# A hull with an 11 m turning radius cannot be steered at a point: it has to
# follow a *line of way points*, and the line the lanes hand it is dense and full
# of sharp corners.  The rule that makes such a line drivable is the acceptance
# radius: a way point that comes within `_arrive_radius(speed)` of the hull
# counts as reached and is dropped, so the hull is already heading for the one
# after it while it is still tens of metres short of the first.  That is what a driver does at a
# right angle - aim for the far side and swing across the inside - and it means
# the hull cheerfully leaves the road on the way through, which is fine: the
# road is not the constraint, the hull's turning radius is.
#
# Locomotion (systems/movement.py) turns the aim into throttle and steering;
# nothing here sets a velocity of its own.

def _seg(pts: list, k: int) -> float:
    (ax, az), (bx, bz) = pts[k], pts[k + 1]
    return math.hypot(bx - ax, bz - az)


def _corner_speed(ang: float, l1: float, l2: float, top: float) -> float:
    """The fastest a hull can take a corner that turns by ``ang`` radians.

    How much room the legs give is irrelevant - a kink is a kink however long
    the straight either side of it - so the corner is defined by how far the
    hull is allowed to cut inside it (APC_CUT, metres).  A fillet of radius R
    misses the vertex by ``R * (1 / cos(ang / 2) - 1)``, so the cut budget caps
    R and the hull's own turning radius floors it; the bend is then driven at
    ``sqrt(APC_LAT_ACCEL * R)``.  A right angle comes out at ~11 m/s cutting
    8 m off the inside - the turn anyone makes at a street corner - and a 20
    degree kink costs nothing at all, as it should.
    """
    if ang < 1e-3:
        return top
    over = 1.0 / math.cos(0.5 * min(ang, math.pi * 0.999)) - 1.0
    radius = defs.APC_CUT / over if over > 1e-6 else 1e9
    radius = max(defs.APC_TURN_R, radius)
    return min(top, math.sqrt(defs.APC_LAT_ACCEL * radius))


def _route_speed(pts: list, i: int, px: float, pz: float, top: float) -> float:
    """How fast the hull may be rolling now and still make the corners ahead.

    Every corner inside APC_CORNER_HORIZON sets a ceiling that rises with the
    distance to it (``v^2 = v_corner^2 + 2 a d``), so the car brakes coming in
    and rolls out of the bend instead of arriving at full speed with a turning
    radius that cannot hold it.
    """
    limit = top
    # start at the way point *before* the one being aimed at: the acceptance
    # radius drops a corner as soon as the hull is within ~12 m of it, and the
    # braking for it has to have happened well before that
    k = max(1, i - 1)
    d = math.dist((px, pz), pts[k])           # metres to the corner itself
    while k + 1 < len(pts) and d < defs.APC_CORNER_HORIZON:
        (ax, az), (bx, bz), (cx, cz) = pts[k - 1], pts[k], pts[k + 1]
        ang = abs(mu.wrap_pi(math.atan2(cz - bz, cx - bx) -
                             math.atan2(bz - az, bx - ax)))
        vb = _corner_speed(ang, _seg(pts, k - 1), _seg(pts, k), top)
        if vb < top:
            limit = min(limit, math.sqrt(vb * vb + 2.0 * defs.APC_BRAKE * d))
        k += 1
        d += _seg(pts, k - 1)
    return limit


def _arrive_radius(speed: float) -> float:
    """How close a way point has to come to count as reached, for this speed.

    The radius has to grow with the speed: a hull at 100 km/h needs ~30 m of arc
    to turn at all, so aiming at a point 10 m away only makes it overshoot and
    come back, while a hull crawling through a tight corner can and should
    follow the line.  ``radius = clamp(speed * time, floor, cap)`` is the
    lookahead rule of a pure-pursuit tracker, and it keeps the same behaviour at
    both ends of the speed range.
    """
    r = abs(speed) * defs.APC_ARRIVE_LOOK
    return max(defs.APC_ARRIVE_MIN, min(defs.APC_ARRIVE_MAX, r))


def _passed(pts: list, i: int, px: float, pz: float, radius: float) -> bool:
    """True when way point ``i`` is behind the hull of a given acceptance radius.

    Either it is inside that radius, or the hull has driven past the line through
    it that is square to the leg arriving from the previous point.  The first is
    what lets a car carry speed into a corner and cut across the inside of it;
    the second is what keeps a car that cut *wide* of a corner - or was shoved
    off the line - from turning back to collect it.
    """
    dx, dz = px - pts[i][0], pz - pts[i][1]
    if dx * dx + dz * dz <= radius * radius:
        return True
    if i <= 0:
        return False
    lx, lz = pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]
    return (lx * lx + lz * lz) > 1e-9 and (dx * lx + dz * lz) > 0.0


def _advance_route(world: World, b: dict, row: int, speed: float) -> None:
    """Aim a wheeled hull at its next way point - the direction, not the throttle."""
    pos = b["pos"]
    vel = b["vel"]
    fac = int(b["faction"][row])
    px, pz = float(pos[row, 0]), float(pos[row, 2])
    if world.lanes:
        lane = int(b["lane"][row]) % len(world.lanes)
        pts = world.lanes_rev[lane] if fac == defs.FACTION_BLUE else world.lanes[lane]
        # `path_i` runs to len(pts), one past the end: that is the "lane
        # finished" mark, and it has to be sticky - deciding per tick between
        # the last way point and the base made a hull orbit the circle between
        # them for ever
        i = max(0, min(int(b["path_i"][row]), len(pts)))
        radius = _arrive_radius(float(b["drive"][row, 0]))
        while i < len(pts) - 1 and _passed(pts, i, px, pz, radius):
            i += 1
        if i == len(pts) - 1 and _passed(pts, i, px, pz, radius):
            i = len(pts)
        b["path_i"][row] = i
    else:
        # a map with no lanes (the range): the car heads straight for the
        # enemy post, which is where a lane would have led it anyway
        i = 0
        pts = []
    if i >= len(pts):
        # the end of the lane is the enemy side of the map: the last stretch
        # runs at the command base, which is what a lane actually leads to
        ax, az = world.terrain.bases[1 - fac]
        v = speed
    else:
        ax, az = pts[i]
        v = _route_speed(pts, i, px, pz, speed)
    dx, dz = ax - px, az - pz
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[row, 0] = vel[row, 2] = 0.0
        return
    vel[row, 0] = dx / d * v
    vel[row, 2] = dz / d * v


def _civ(world: World, eid: int, b: dict, row: int, dt: float) -> None:
    pos = b["pos"]
    vel = b["vel"]
    timer = b["ai_timer"]
    timer[row] -= dt
    if timer[row] > 0.0:
        return
    timer[row] = world.rng.uniform(1.5, 4.5)
    dx = float(b["home_x"][row]) - float(pos[row, 0])
    dz = float(b["home_z"][row]) - float(pos[row, 2])
    if dx * dx + dz * dz > 240.0 * 240.0:
        a = math.atan2(dz, dx)
    else:
        a = world.rng.random() * math.tau
    vel[row, 0] = math.cos(a) * 2.0
    vel[row, 2] = math.sin(a) * 2.0
    b["yaw"][row] = a
