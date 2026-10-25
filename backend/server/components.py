"""Component registration.

Every component is a numpy array with one row per entity slot.  Presence is
tracked through the entity's component mask (see ``ecs.py``), so a component
value of zero is a perfectly valid value.

Groups below are convenience lists used by the spawning factories.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import numpy as np

if TYPE_CHECKING:
    from .ecs import ECS


def register(ecs: ECS) -> None:
    # ---- transform ------------------------------------------------------
    ecs.register("pos", np.float32, (3,), 0.0)
    ecs.register("vel", np.float32, (3,), 0.0)       # commanded velocity
    ecs.register("drive", np.float32, (2,), 0.0)     # mech hull: the speed it is really rolling
    ecs.register("yaw", np.float32)
    ecs.register("pitch", np.float32)
    ecs.register("leg_yaw", np.float32)
    ecs.register("turret_yaw", np.float32)

    # ---- identity -------------------------------------------------------
    # `etype` is the entity's data (hull geometry, hp slots, which arm fires
    # what).  What an entity *is* - what a system may do with it - lives in the
    # marker components below: an entity carries the tags of its type
    # (`defs.UNIT_DEFS[...]["tags"]`) and every system queries the tag it can
    # handle instead of switching on the type.  A smoke puff has none of them,
    # so no weapon can hit it and no AI can pick it as a target.
    ecs.register("etype", np.uint8)
    ecs.register("faction", np.uint8)                # units and rounds only
    ecs.register("owner", np.int32, (), -1)          # player id, -1 = AI
    ecs.register("unit", np.uint8)                   # a hull on the battlefield
    ecs.register("combat", np.uint8)                 # fights, may be fought, holds towns
    ecs.register("lockable", np.uint8)               # an IR seeker can track it
    ecs.register("moves", np.uint8)                  # has locomotion
    ecs.register("invulnerable", np.uint8)           # rounds stop, damage does nothing

    # ---- health ---------------------------------------------------------
    ecs.register("hp", np.float32, (4,), 0.0)
    ecs.register("hpmax", np.float32, (4,), 0.0)

    # ---- weapons --------------------------------------------------------
    ecs.register("weapon", np.uint8, (2,), 0)        # 0 = left arm, 1 = right arm
    ecs.register("ammo", np.int32, (2,), 0)
    ecs.register("mag", np.int32, (2,), 0)
    ecs.register("cool", np.float32, (2,), 0.0)
    ecs.register("reload_t", np.float32, (2,), 0.0)
    ecs.register("trigger", np.uint8, (), 0)         # bit0 left, bit1 right
    ecs.register("trigger_prev", np.uint8, (), 0)    # last tick's bits, for semi auto

    # ---- ai -------------------------------------------------------------
    ecs.register("target", np.int32, (), -1)
    ecs.register("ai_state", np.uint8)
    ecs.register("ai_timer", np.float32)
    ecs.register("path_i", np.int32)
    ecs.register("lane", np.uint8)
    # where an AI last *saw* its target: with the sight broken (smoke, a crest,
    # the hull ducking behind a building) it keeps shooting at that spot for a
    # few seconds instead of giving up
    ecs.register("spot_x", np.float32)
    ecs.register("spot_y", np.float32)
    ecs.register("spot_z", np.float32)
    ecs.register("spot_t", np.float32)

    # ---- misc -----------------------------------------------------------
    ecs.register("flags", np.uint8)
    ecs.register("home_x", np.float32)               # wandering civilians
    ecs.register("home_z", np.float32)

    # ---- debris ---------------------------------------------------------
    ecs.register("wreck_t", np.float32)              # seconds left before it fades
    ecs.register("wreck_kind", np.uint8)             # 0 infantry 1 apc 2 mech

    # ---- smoke ----------------------------------------------------------
    # A pull throws `canister` entities out of the hull; each one flies, then
    # trades the `canister` tag for `smoke` and becomes an oblate ellipsoid that
    # eats missile lock-on.  Neither has `unit` or `faction`, so nothing can
    # target them and rounds fly straight through - that is a property of the
    # queries, not a rule each system has to remember.
    ecs.register("canister", np.uint8)              # in flight, not yet a cloud
    ecs.register("smoke", np.uint8)                  # deployed puff
    ecs.register("smoke_r", np.float32, (3,), 0.0)  # ellipsoid semiaxes, metres
    ecs.register("smoke_t", np.float32, 0.0)        # seconds to pop / of life left

    # ---- projectile -----------------------------------------------------
    ecs.register("proj_kind", np.uint8)              # 0 bullet 1 rocket 2 saclos 3 top
    ecs.register("proj_wid", np.uint8)
    ecs.register("proj_life", np.float32)
    ecs.register("proj_src", np.int32, (), -1)
    ecs.register("proj_age", np.float32)
    ecs.register("proj_seed", np.float32)            # per round, drives the thrust jitter


UNIT_COMPS = ["pos", "vel", "drive", "yaw", "pitch", "leg_yaw", "turret_yaw", "etype", "faction",
              "owner", "hp", "hpmax", "weapon", "ammo", "mag", "cool", "reload_t",
              "trigger", "trigger_prev", "target", "ai_state", "ai_timer", "path_i", "lane",
              "spot_x", "spot_y", "spot_z", "spot_t", "flags"]

CIV_COMPS = UNIT_COMPS + ["home_x", "home_z"]

DEBRIS_COMPS = ["pos", "vel", "yaw", "pitch", "leg_yaw", "turret_yaw", "etype", "faction",
                "owner", "hp", "hpmax", "flags", "wreck_t", "wreck_kind"]

PROJ_COMPS = ["pos", "vel", "faction", "proj_kind", "proj_wid", "proj_life", "proj_src",
              "proj_age", "proj_seed", "target"]

# a canister in flight and a deployed puff share their shape so one snapshot
# section and one client pass can draw both
SMOKE_COMPS = ["pos", "vel", "smoke_r", "smoke_t"]
