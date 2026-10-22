"""Component registration.

Every component is a numpy array with one row per entity slot.  Presence is
tracked through the entity's component mask (see ``ecs.py``), so a component
value of zero is a perfectly valid value.

Groups below are convenience lists used by the spawning factories.
"""

from __future__ import annotations

import numpy as np


def register(ecs):
    # ---- transform ------------------------------------------------------
    ecs.register("pos", np.float32, (3,), 0.0)
    ecs.register("vel", np.float32, (3,), 0.0)
    ecs.register("yaw", np.float32)
    ecs.register("pitch", np.float32)
    ecs.register("leg_yaw", np.float32)
    ecs.register("turret_yaw", np.float32)

    # ---- identity -------------------------------------------------------
    ecs.register("etype", np.uint8)
    ecs.register("faction", np.uint8)
    ecs.register("owner", np.int32, (), -1)          # player id, -1 = AI

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

    # ---- ai -------------------------------------------------------------
    ecs.register("target", np.int32, (), -1)
    ecs.register("ai_state", np.uint8)
    ecs.register("ai_timer", np.float32)
    ecs.register("path_i", np.int32)
    ecs.register("lane", np.uint8)

    # ---- misc -----------------------------------------------------------
    ecs.register("flags", np.uint8)
    ecs.register("home_x", np.float32)               # wandering civilians
    ecs.register("home_z", np.float32)

    # ---- projectile -----------------------------------------------------
    ecs.register("proj_kind", np.uint8)              # 0 bullet 1 rocket 2 saclos 3 top
    ecs.register("proj_wid", np.uint8)
    ecs.register("proj_life", np.float32)
    ecs.register("proj_src", np.int32, (), -1)
    ecs.register("proj_age", np.float32)


UNIT_COMPS = ["pos", "vel", "yaw", "pitch", "leg_yaw", "turret_yaw", "etype", "faction",
              "owner", "hp", "hpmax", "weapon", "ammo", "mag", "cool", "reload_t",
              "trigger", "target", "ai_state", "ai_timer", "path_i", "lane", "flags"]

CIV_COMPS = UNIT_COMPS + ["home_x", "home_z"]

PROJ_COMPS = ["pos", "vel", "faction", "proj_kind", "proj_wid", "proj_life", "proj_src",
              "proj_age", "target"]
