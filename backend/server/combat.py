"""Damage resolution: the one place `hp` is written.

Not a system - a library the systems call (`ballistics`, `shop`, `grid` are the
same kind of module).  A damage is not a thing that happens once a tick to the
whole field, it is something a round does the moment it lands, so it lands
there: forward, splash and the fuse all go through `apply` in their own loop.

    apply   <- hit    <- projectiles._impact   (a round landing on a hull)
            <- splash <- systems/blasts        (a hull inside a burst)
            <- debug_hurt                      (a raw number, for testing)

Two rules hold this together:

* **`hp` is the health and `hp <= 0` is the death.**  Nothing here decides that
  a unit died - `systems/mortality.py` does, one stage later, so this module
  never destroys anything and no loop ever has an entity vanish underneath it.
* **A hull with modules carries the extra pools itself.**  Only the archetypes
  that declare the `arms` tag own `arm_hp`; everyone else has one number and
  one `else` branch.  That is the whole of the "mech is special" story, and it
  is why `_mech_hit` below is the only code in the game that knows a hit can
  land on something other than the hull.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from . import defs

if TYPE_CHECKING:
    from .world import World

log = logging.getLogger("mech.intercept")


def apply(world: World, eid: int, module: int, amount: float, pid: int = -1,
          src: str = "") -> None:
    """Take `amount` off the armour `module` names.  The only writer of `hp`."""
    e = world.ecs
    if not e.alive[eid] or amount <= 0.0:
        return
    if e.has(eid, "invulnerable"):
        return                      # concrete: rounds stop, nothing breaks
    # a missile owns no pilot, so the god check has nothing to ask about; it is
    # a round in the air and always takes what it is given
    if e.has(eid, "owner"):
        owner = int(e["owner"][eid])
        if owner >= 0:
            pilot = world.players.get(owner)
            if pilot is not None and pilot.god:
                return
    bufs, row = e.slot(eid)
    # a round in the air is worth a line of its own: it starts with 10 hp, so
    # the numbers say whether it was a graze or a kill (`tools/intercept_check`
    # reads them straight out of the log)
    watch = "interceptable" in bufs
    before = float(bufs["hp"][row]) if watch else 0.0
    if "arm_hp" in bufs:
        _mech_hit(bufs, row, module, amount)
    else:
        bufs["hp"][row] = max(0.0, float(bufs["hp"][row]) - amount)
    if watch:
        log.info("missile %d took %.1f (%s) - hp %.1f -> %.1f",
                 eid, amount, src or "hit", before, float(bufs["hp"][row]))
    if float(bufs["hp"][row]) > 0.0:
        # credit only while the hull is still standing: the death is judged a
        # stage later, and the pilot who put the *lethal* round in earns it
        if pid >= 0 and "last_hit" in bufs:
            bufs["last_hit"][row] = pid
    else:
        _stop(bufs, row)


def _stop(bufs: dict, row: int) -> None:
    """A hull at zero hp stops dead where it is.

    The kill itself is judged a stage later (`systems/mortality.py`), so for the
    rest of this tick the entity is still in the record - and without this it
    would go on driving, because movement and the AI run before the damage that
    finished it.  `movement` reads `vel`, the wheeled model reads `drive`.
    """
    if "vel" in bufs:
        bufs["vel"][row] = 0.0
    if "drive" in bufs:
        bufs["drive"][row] = 0.0


def hit(world: World, eid: int, module: int, wd: dict, dist: float = 0.0,
        pid: int = -1, src: str = "") -> None:
    """One round landing on a unit: penetration plus blast, both halves.

    `dist` is how far the target's nearest surface is from the burst - 0 for a
    direct hit - and the blast half falls off to nothing over the weapon's `er`.
    """
    if float(wd["ad"]) <= 0.0 and float(wd["ed"]) <= 0.0:
        return
    dp, de = _armor(world, eid)
    apply(world, eid, module, defs.hit_damage(wd, dp, de, dist), pid, src)


def splash(world: World, eid: int, module: int, wd: dict, dist: float,
           pid: int = -1, src: str = "") -> None:
    """Blast-only damage for a unit near (not under) a burst.

    A charge going off a few metres away cannot punch a plate, so the
    penetration half of `hit` does not apply here.
    """
    if float(wd["ed"]) <= 0.0:
        return
    _, de = _armor(world, eid)
    apply(world, eid, module,
          defs.blast_damage(float(wd["ed"]), de, float(wd["er"]), dist), pid, src)


def arm_alive(world: World, eid: int, arm: int) -> bool:
    """Is the weapon in `arm` (0 left, 1 right) still there to be fired?"""
    e = world.ecs
    if not e.has(eid, "arm_hp"):
        return True                 # a single-pool hull has no arm to lose
    return float(e["arm_hp"][eid, arm]) > 0.0


def _armor(world: World, eid: int) -> tuple[float, float]:
    e = world.ecs
    if not e.alive[eid]:
        return 0.0, 0.0
    a = e["armor"][eid]
    return float(a[0]), float(a[1])


def _mech_hit(bufs: dict, row: int, module: int, amount: float) -> None:
    """A hit on a hull that has arms: the only multi-pool code in the game."""
    hp = bufs["hp"]
    arm = bufs["arm_hp"]
    if module == defs.MOD_LEFT or module == defs.MOD_RIGHT:
        a = module - defs.MOD_LEFT          # the arm index *is* the weapon index
        if float(arm[row, a]) > 0.0:
            arm[row, a] = max(0.0, float(arm[row, a]) - amount)
            if float(arm[row, a]) <= 0.0:   # that arm just came off
                bufs["mag"][row, a] = 0
                bufs["ammo"][row, a] = 0
            return
        # an arm that is already gone is not a shield: the round goes inwards
    hp[row] = max(0.0, float(hp[row]) - amount)
