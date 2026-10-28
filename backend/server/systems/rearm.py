"""Rearm work: the wrench, not just the bill.

A trade in ``shop.py`` is booked on the pilot instead of landing on the mech on
the spot.  Fitting a weapon, loading its racks and welding armour back on all
take seconds, and while the job runs the machine is held still with its
triggers dead - that is what stops a repair inside an owned town from doubling
as a mid-fight reload.  The order is charged when it is booked.

``work_remaining_time`` is the timer, a plain float on the mech: the shop writes
the seconds the draft cost into it, this system counts it down every tick and,
when it reaches zero, applies the bought lines.  Everything else that cares
(``player_input`` zeroing the movement and fire bits) just asks whether it is
still above zero.  A pilot whose mech is destroyed mid-job loses the job - and
the money, it was spent when the deal was booked.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from .. import shop

if TYPE_CHECKING:
    from ..world import World


def run(world: World, dt: float) -> None:
    e = world.ecs
    rem = e["work_remaining_time"]
    for pl in world.players.values():
        eid = pl.mech
        if pl.work is None:
            if eid >= 0 and e.alive[eid] and rem[eid] > 0.0:
                rem[eid] = 0.0          # a timer with no order behind it
            continue
        if eid < 0 or not e.alive[eid]:
            pl.end_work()               # the hull went up with the wrench out
            continue
        # the last chunk of work is the one that finishes the job, whether the
        # tick had a whole second of it or a hundredth
        rem[eid] = max(0.0, float(rem[eid]) - dt)
        if rem[eid] > 0.0:
            continue
        for line in pl.work:
            shop.apply_line(world, pl, line)
        label = shop.work_label(pl.work)
        pl.end_work()
        world.emit("rearm", pid=pl.pid, label=label)
