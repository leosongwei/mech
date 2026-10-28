"""Line of sight: the one rule every eye in the game obeys.

What cuts a line of sight is a single question, and the answer has two halves:

* the **field** - the ground, a building up to its roof, a tree stand up to its
  canopy (``Terrain.los``; a trunk is what stops a round, the leaves only hide
  what stands behind them);
* the **screen** - the puffs a pilot lobbed into the air (``smoke.blocks``).

The two are the same thing to an eye: a tree hides what stands behind it for
good, a puff hides it for five seconds and then it is gone.  So they are
answered here, together, and every eye comes through this module - the IR
seeker's lock, the AI's lookout, a missile's guidance.  Nobody pairs the two
checks up on its own: the pilot's lock used to ask only for the field, which
let a hull be locked straight through a screen of smoke and kept a lock box
standing on a target the missile could no longer see.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from . import smoke

if TYPE_CHECKING:
    from ..world import World

LOS_STEP = 8.0      # metres between sight samples: half a tile, so a 16 m
                    # block can never slip between two of them


def why(world: World, a, b, step: float = LOS_STEP) -> str:
    """What cuts the sight line ``a`` -> ``b``: ``""``, ``"smoke"`` or
    ``"terrain"``.

    The smoke is tested first: it is the nearer, temporary answer, and it is
    what a seeker losing its lock reports (the range smoke test reads the
    reason straight out of the log).
    """
    if smoke.blocks(world, a, b):
        return "smoke"
    if not world.terrain.los(a, b, step):
        return "terrain"
    return ""


def clear(world: World, a, b, step: float = LOS_STEP) -> bool:
    """Whether nothing cuts the sight line ``a`` -> ``b``."""
    return why(world, a, b, step) == ""
