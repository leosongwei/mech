"""Area damage, and the lifetime of the flash that does it.

An explosion is an entity.  Whoever wants one - a round landing, a proximity
fuse, a wreck of either hull - only creates it (`spawn`); the single
implementation of "everything inside `er` takes blast" reads it here.

That buys four things the old in-line loop could not:

* one place that solves area damage, so a mine or a fuel dump is a `spawn` call;
* a chain reaction is a tick apart *by construction*.  A hull that dies this
  tick leaves a burst that goes off on the next one, so it cannot damage the
  hulk that hull has already become - no "solve every distance first, then
  apply" discipline needed;
* the credit is frozen at creation (`blast_pid`), so a burst whose shooter has
  since died still pays out to the pilot who fired it;
* `blast_t` is a fuse.  A one-tick burst is `0.0`; anything longer is a burning
  area that goes on taking pieces off whatever stands in it.

Costs nothing when nothing is exploding, and nothing else owns these buffers.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import numpy as np

from .. import combat, components

if TYPE_CHECKING:
    from ..world import World


def spawn(world: World, pos, power, friendly: bool = False, skip: int = -1,
          pid: int = -1, faction: int = -1, life: float = 0.0) -> int:
    """Create a burst at `pos`.  `power` is `(ed, er)`; `skip` is spared.

    `friendly` False means "enemies only" (a weapon burst), True means "no sides"
    (a wreck going up).  `life` is how many seconds it keeps burning - 0 is a
    single tick, which is every explosion in the game today.
    """
    ed = float(power[0])
    if ed <= 0.0:
        return -1
    e = world.ecs
    bid = e.create(components.BLAST_COMPS)
    e["pos"][bid] = (float(pos[0]), float(pos[1]), float(pos[2]))
    e["faction"][bid] = 255 if faction < 0 else int(faction)
    e["blast_power"][bid] = (ed, float(power[1]))
    e["blast_t"][bid] = float(life)
    e["blast_friendly"][bid] = 1 if friendly else 0
    e["blast_skip"][bid] = int(skip)
    e["blast_pid"][bid] = int(pid)
    return bid


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.blasts()
    if ids.size == 0:
        return
    t = e["blast_t"]
    sph = world.sphere_table()
    for bid in ids:
        bid = int(bid)
        _burst(world, bid, sph)
        t[bid] = float(t[bid]) - dt
        if t[bid] <= 0.0:
            e.destroy(bid)


def _burst(world: World, bid: int, sph: tuple) -> None:
    """One burst against the whole battlefield: one reading per hull."""
    e = world.ecs
    ed, er = (float(v) for v in e["blast_power"][bid])
    if ed <= 0.0:
        return
    centres, radii, units, modules, factions = sph[:5]
    if centres.shape[0] == 0:
        return
    p = np.asarray(e["pos"][bid], np.float64)
    wd = {"ed": ed, "er": er, "ad": 0.0}
    skip = int(e["blast_skip"][bid])
    fac = int(e["faction"][bid])
    d = np.sqrt(((centres - p) ** 2).sum(axis=1)) - radii
    hit = d <= er
    if skip >= 0:
        hit &= units != skip
    if not int(e["blast_friendly"][bid]):
        hit &= factions != fac
    if not hit.any():
        return
    # one splash per unit, off its closest sphere - the same rule (and the same
    # lexsort) the impact burst always used
    idx = np.nonzero(hit)[0]
    order = idx[np.lexsort((d[idx], units[idx]))]
    first = np.ones(order.size, bool)
    first[1:] = units[order][1:] != units[order][:-1]
    pid = int(e["blast_pid"][bid])
    for k in order[first]:
        combat.splash(world, int(units[k]), int(modules[k]), wd,
                      max(float(d[k]), 0.0), pid)
