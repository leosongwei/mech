"""The rearm shop: one quote, one deal.

A player at a base or an owned town opens the panel (``B``) and edits a draft
loadout; nothing happens until it is confirmed, at which point the whole draft
is priced and either bought or refused as a unit.  Both sides of a trade are
priced from the same tables in :mod:`defs`:

* hardware  ``WEAPONS[wid]["cost"]``
* rounds    ``WEAPONS[wid]["ammo_cost"]`` per round, a full load is
  ``mag + reserve`` rounds
* selling back pays ``SELL_REFUND`` of the hardware, scaled by the fraction of
  that arm still standing (an arm shot down to half is worth half as much),
  plus half of the rounds still in it
* welding hp back on costs ``REPAIR_COST_PER_HP`` per point, over all modules

The client runs the same arithmetic for the live preview, so the numbers here
have to stay in step with ``frontend/src/shop.js``.

A deal is not applied on the spot: the lines are booked on the pilot as a work
order (``Player.work``) and ``systems/rearm.py`` does the job over the seconds
the draft cost, with the mech held still and its triggers dead.  Money is
charged when the job is booked.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Any

from . import defs

if TYPE_CHECKING:
    from .world import Player, World

ARM_NAMES = ("L-ARM", "R-ARM")


def _r(v: float) -> int:
    """Round money the way the browser's Math.round does, so a preview and the
    bill never differ by a dollar."""
    return int(math.floor(float(v) + 0.5))
MODULE_OF_ARM = (defs.MOD_LEFT, defs.MOD_RIGHT)


def repair_time(need: float) -> float:
    """Seconds of welding for `need` points of missing armour."""
    return max(0.0, float(need)) * defs.REARM_REPAIR_PER_100HP_S / 100.0


def work_label(lines: list[dict[str, Any]]) -> str:
    """What the progress bar calls the job: the operations it is made of."""
    kinds = {line["kind"] for line in lines}
    parts = [name for kind, name in (("swap", "WEAPON CHANGE"), ("rearm", "REARM"),
                                     ("repair", "REPAIR"), ("smoke", "SMOKE"))
             if kind in kinds]
    return " + ".join(parts) if parts else "REARM"


def empty_request() -> dict[str, Any]:
    return dict(arms=[-1, -1], rearm=[False, False], repair=False, smoke=False)


def parse_request(msg: dict[str, Any]) -> dict[str, Any]:
    """Take the client's draft and keep only what it is allowed to ask for."""
    req = empty_request()
    arms = msg.get("arms")
    if isinstance(arms, list):
        for arm in range(2):
            try:
                wid = int(arms[arm])
            except (IndexError, TypeError, ValueError):
                continue
            if wid in defs.BUYABLE:
                req["arms"][arm] = wid
    rearm = msg.get("rearm")
    if isinstance(rearm, list):
        for arm in range(2):
            req["rearm"][arm] = bool(rearm[arm]) if arm < len(rearm) else False
    req["repair"] = bool(msg.get("repair", False))
    req["smoke"] = bool(msg.get("smoke", False))
    return req


def arm_state(world: World, pl: Player, arm: int) -> dict[str, Any]:
    """What one arm holds right now: hardware, rounds and how much of it is left."""
    e = world.ecs
    wid = int(pl.loadout[arm])
    wd = defs.WEAPONS[wid] if 0 <= wid < len(defs.WEAPONS) else defs.WEAPONS[defs.WID_NONE]
    mid = pl.mech
    if mid >= 0 and e.alive[mid]:
        slot = MODULE_OF_ARM[arm]
        hp = float(e["hp"][mid, slot])
        hpmax = max(1.0, float(e["hpmax"][mid, slot]))
        mag = int(e["mag"][mid, arm])
        reserve = int(e["ammo"][mid, arm])
    else:
        hp = hpmax = 1.0
        mag = reserve = 0
    return dict(arm=arm, wid=wid, wd=wd, hp=hp, hpmax=hpmax,
                mag=mag, reserve=reserve, rounds=mag + reserve)


def quote(world: World, pl: Player, req: dict[str, Any]) -> dict[str, Any]:
    """Price a whole draft.  ``delta`` per line is what it adds to the bill,
    ``dur`` how many seconds of work it books (they add up: a weapon change and
    the racks it brings are two jobs, done one after the other)."""
    mid = pl.mech
    if mid < 0 or not world.ecs.alive[mid]:
        return dict(ok=False, error="NO MECH", lines=[], total=0, dur=0.0)

    e = world.ecs
    lines = []
    total = 0.0
    dur = 0.0

    # hardware: swap one arm for another
    for arm in range(2):
        cur = arm_state(world, pl, arm)
        wid = int(req["arms"][arm])
        if wid < 0 or wid == cur["wid"]:
            continue
        nwd = defs.WEAPONS[wid]
        hardware = _r(cur["wd"]["cost"] * defs.SELL_REFUND * cur["hp"] / cur["hpmax"])
        rounds = _r(cur["rounds"] * cur["wd"]["ammo_cost"] * defs.SELL_REFUND)
        load = _r((nwd["mag"] + nwd["reserve"]) * nwd["ammo_cost"])
        delta = nwd["cost"] + load - hardware - rounds
        # the weapon comes out, the new one goes in, and its racks are loaded:
        # a swap is a fit *and* a rearm
        t = defs.REARM_SWAP_S + defs.REARM_AMMO_S
        total += delta
        dur += t
        lines.append(dict(kind="swap", arm=arm, wid=wid, delta=delta, dur=t,
                          hp=round(cur["hp"] / cur["hpmax"], 2),
                          trade_in=hardware + rounds,
                          text=f"{ARM_NAMES[arm]}: {cur['wd']['name']} ({cur['rounds']})"
                               f" -> {nwd['name']}"))

    # rounds: top the current arm back up to a full load
    for arm in range(2):
        if int(req["arms"][arm]) >= 0:
            continue                       # a swap already brings full racks
        cur = arm_state(world, pl, arm)
        if not req["rearm"][arm]:
            continue
        full = cur["wd"]["mag"] + cur["wd"]["reserve"]
        missing = full - cur["rounds"]
        if missing <= 0:
            continue
        delta = _r(missing * cur["wd"]["ammo_cost"])
        t = defs.REARM_AMMO_S
        total += delta
        dur += t
        lines.append(dict(kind="rearm", arm=arm, wid=cur["wid"], delta=delta, dur=t,
                          text=f"{ARM_NAMES[arm]}: {cur['wd']['name']} REARM {missing} rds"))

    # armour: weld every module back to full
    if req["repair"]:
        hp = e["hp"][mid]
        hpmax = e["hpmax"][mid]
        need = sum(max(0.0, float(hpmax[i]) - float(hp[i])) for i in range(4))
        cost = _r(need * defs.REPAIR_COST_PER_HP)
        if cost > 0:
            t = repair_time(need)
            total += cost
            dur += t
            lines.append(dict(kind="repair", delta=cost, dur=t,
                              text=f"REPAIR {int(round(need))} hp"))

    # smoke: a fresh rack, full and off cooldown
    if req["smoke"]:
        if pl.smoke_charges < defs.SMOKE_REFILL_CHARGES or pl.smoke_cd > 0.0:
            cost = defs.SMOKE_REFILL_COST
            total += cost
            lines.append(dict(kind="smoke", delta=cost, dur=0.0,
                              text=f"REARM SMOKE x{defs.SMOKE_REFILL_CHARGES}"))

    if not lines:
        return dict(ok=False, error="NOTHING TO DO", lines=[], total=0, dur=0.0)
    if pl.work is not None:
        return dict(ok=False, error="ALREADY REARMING", lines=lines, total=int(total), dur=dur)
    if total > _r(pl.money):
        return dict(ok=False, error="NOT ENOUGH MONEY", lines=lines, total=int(total), dur=dur)
    return dict(ok=True, error="", lines=lines, total=int(total), dur=round(dur, 2))


def apply_line(world: World, pl: Player, line: dict[str, Any]) -> None:
    """Carry out one priced line on the mech that is standing in the bay."""
    e = world.ecs
    mid = pl.mech
    kind = line["kind"]
    if kind == "swap":
        arm, wid = line["arm"], line["wid"]
        wd = defs.WEAPONS[wid]
        pl.loadout[arm] = wid
        e["weapon"][mid, arm] = wid
        e["mag"][mid, arm] = wd["mag"]
        e["ammo"][mid, arm] = wd["reserve"]
        e["reload_t"][mid, arm] = 0.0
        e["cool"][mid, arm] = 0.0
    elif kind == "rearm":
        arm = line["arm"]
        wd = defs.WEAPONS[int(e["weapon"][mid, arm])]
        e["mag"][mid, arm] = wd["mag"]
        e["ammo"][mid, arm] = wd["reserve"]
        e["reload_t"][mid, arm] = 0.0
    elif kind == "repair":
        e["hp"][mid] = e["hpmax"][mid]
    elif kind == "smoke":
        pl.smoke_charges = defs.SMOKE_REFILL_CHARGES
        pl.smoke_cd = 0.0


def deal(world: World, pl: Player, req: dict[str, Any]) -> tuple[bool, str, dict[str, Any]]:
    """Quote the draft, charge it, and book the work it costs.

    The lines are applied by ``systems/rearm.py`` when the job has run its
    seconds - nothing lands on the mech here, except a job with no duration at
    all (a smoke rack), which is done on the spot.
    """
    q = quote(world, pl, req)
    if not q["ok"]:
        return False, q["error"], q
    pl.money -= q["total"]
    spent = int(q["total"])
    dur = float(q["dur"])
    if dur <= 0.0:
        for line in q["lines"]:
            apply_line(world, pl, line)
        note = f"DEAL DONE -${spent}" if spent > 0 else f"DEAL DONE +${-spent}"
        return True, note, q
    # the seconds go on the mech: systems/rearm.py counts them down, holds the
    # machine still for as long as they last and applies the lines at zero
    pl.work = list(q["lines"])
    pl.work_total = dur
    world.ecs["work_remaining_time"][pl.mech] = dur
    return True, f"REARM STARTED -${spent} / {dur:.1f}s", q

