"""Hot smoke: a fan of canisters thrown out of the hull, then a wall of smoke.

A pull (``Q``) throws ``defs.SMOKE_COUNT`` canisters out of the hull in a fan
that closes the front hemisphere.  Each one flies ``SMOKE_RANGE`` metres and
pops into one big puff that stays up for ``SMOKE_LIFE`` seconds.

The smoke is a set of ordinary entities - ``pos`` + ``smoke`` + ``smoke_r`` +
``smoke_t`` - and *only* that: no ``unit``, no ``faction``, no ``etype``.  Every
system that cares about units asks for the ``unit``/``combat`` tag, so a puff is
invisible to weapons, AI targeting, town capture and projectile collision
without a single exclusion rule.  What it does affect is missile guidance,
which asks ``world.smoke_blocks()`` directly.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import components, defs, mathutil as mu

if TYPE_CHECKING:
    from ..world import World


def run(world: World, dt: float) -> None:
    _launch(world, dt)
    _fly(world, dt)
    _fade(world, dt)


def _launch(world: World, dt: float) -> None:
    """Turn a pilot's Q press into a fan of canisters."""
    e = world.ecs
    for pl in world.players.values():
        pl.smoke_cd = max(0.0, float(pl.smoke_cd) - dt)
        fired = bool(pl.smoke_q)
        pl.smoke_q = False
        eid = pl.mech
        if not fired or eid < 0 or not e.alive[eid]:
            continue
        if pl.smoke_charges <= 0 or pl.smoke_cd > 0.0:
            continue
        if not e.has(eid, "unit"):
            continue
        pl.smoke_charges -= 1
        cd = world.mode_mod.smoke_cooldown
        pl.smoke_cd = defs.SMOKE_COOLDOWN if cd is None else float(cd)
        throw(world, eid)
        world.emit("smoke", eid=int(eid), charges=int(pl.smoke_charges))


def throw(world: World, eid: int) -> None:
    """Spawn the canister fan of the hull `eid`."""
    e = world.ecs
    px = float(e["pos"][eid, 0])
    py = float(e["pos"][eid, 1])
    pz = float(e["pos"][eid, 2])
    yaw = float(e["yaw"][eid])
    launcher_h = float(defs.UNIT_DEFS[int(e["etype"][eid])]["height"]) * 0.55
    for az, el in defs.SMOKE_FAN:
        a = yaw + math.radians(az)
        p = math.radians(el)
        cp = math.cos(p)
        d = (cp * math.cos(a), math.sin(p), cp * math.sin(a))
        cid = e.create()
        for c in components.SMOKE_COMPS:
            e.add_comp(cid, c)
        e.add_comp(cid, "canister")
        e["pos"][cid] = (px, py + launcher_h, pz)
        e["vel"][cid] = (d[0] * defs.SMOKE_SPEED, d[1] * defs.SMOKE_SPEED,
                         d[2] * defs.SMOKE_SPEED)
        e["smoke_t"][cid] = defs.SMOKE_RANGE / defs.SMOKE_SPEED   # time to pop
        e["smoke_r"][cid] = _radii(0.0)


def _radii(age: float) -> tuple[float, float, float]:
    """Puff semiaxes `age` seconds after the pop.

    A canister is a small charge: it bursts the smoke out fast and the puff
    then keeps creeping for the rest of its life, so the growth is an
    ease-out towards ``SMOKE_FLAT`` rather than a constant spread.
    """
    k = 1.0 - math.exp(-defs.SMOKE_BURST * age)
    f = defs.SMOKE_START + (1.0 - defs.SMOKE_START) * k
    return (defs.SMOKE_FLAT[0] * f, defs.SMOKE_FLAT[1] * f, defs.SMOKE_FLAT[0] * f)


def _fly(world: World, dt: float) -> None:
    """Canisters: a short ballistic hop, then pop into smoke."""
    e = world.ecs
    ids = e.query("canister")
    if ids.size == 0:
        return
    pos = e["pos"]
    vel = e["vel"]
    t = e["smoke_t"]
    for cid in ids:
        cid = int(cid)
        t[cid] = float(t[cid]) - dt
        if t[cid] <= 0.0:
            _pop(world, cid)
            continue
        vel[cid, 1] -= 9.81 * dt
        pos[cid, 0] += vel[cid, 0] * dt
        pos[cid, 1] += vel[cid, 1] * dt
        pos[cid, 2] += vel[cid, 2] * dt


def _pop(world: World, cid: int) -> None:
    """The canister stops where it is and starts making smoke."""
    e = world.ecs
    e.remove_comp(cid, "canister")
    e.add_comp(cid, "smoke")
    e["vel"][cid] = (0.0, 0.0, 0.0)
    # a low canister would bury half its puff in the terrain, so the centre
    # sits half a radius up: what is left underground does not matter
    e["pos"][cid, 1] = max(float(e["pos"][cid, 1]), defs.SMOKE_FLAT[1] * 0.5)
    e["smoke_t"][cid] = defs.SMOKE_LIFE
    e["smoke_r"][cid] = _radii(0.0)


def _fade(world: World, dt: float) -> None:
    """Deployed puffs burst open, creep for a while, then go away."""
    e = world.ecs
    ids = e.query("smoke")
    if ids.size == 0:
        return
    t = e["smoke_t"]
    rad = e["smoke_r"]
    for sid in ids:
        sid = int(sid)
        t[sid] = float(t[sid]) - dt
        if t[sid] <= 0.0:
            e.destroy(sid)
            continue
        rad[sid] = _radii(defs.SMOKE_LIFE - float(t[sid]))


def blocks(world: World, a: tuple[float, float, float],
           b: tuple[float, float, float]) -> bool:
    """True when the segment a->b passes through any deployed puff.

    The puff is an ellipsoid, so the segment is scaled into the unit sphere
    of each puff and tested there: one dot product and a closest point.
    """
    e = world.ecs
    ids = e.query("smoke")
    if ids.size == 0:
        return False
    pos = e["pos"]
    rad = e["smoke_r"]
    ax, ay, az = float(a[0]), float(a[1]), float(a[2])
    bx, by, bz = float(b[0]), float(b[1]), float(b[2])
    for sid in ids:
        sid = int(sid)
        r = rad[sid]
        if r[0] <= 0.0 or r[1] <= 0.0 or r[2] <= 0.0:
            continue
        c = pos[sid]
        pax = (ax - float(c[0])) / float(r[0])
        pay = (ay - float(c[1])) / float(r[1])
        paz = (az - float(c[2])) / float(r[2])
        dx = (bx - float(c[0])) / float(r[0]) - pax
        dy = (by - float(c[1])) / float(r[1]) - pay
        dz = (bz - float(c[2])) / float(r[2]) - paz
        L = dx * dx + dy * dy + dz * dz
        u = 0.0 if L < 1e-9 else -(pax * dx + pay * dy + paz * dz) / L
        u = 0.0 if u < 0.0 else (1.0 if u > 1.0 else u)
        qx = pax + dx * u
        qy = pay + dy * u
        qz = paz + dz * u
        if qx * qx + qy * qy + qz * qz <= 1.0:
            return True
    return False
