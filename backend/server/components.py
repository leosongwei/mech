"""Component registration.

Every component is a dense buffer over the entities that own it; which
components an entity owns is its *type* and is fixed when it spawns (see
``ecs.py``).  A component value of zero is therefore a perfectly valid value.

Groups below are the component sets the spawning factories hand to
``ECS.create``: one group per archetype.
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
    # wheeled hull locomotion state: 0 the speed it rolls along its own axis
    # (negative while backing up), 1 seconds of back-out left, 2 how long the
    # nose has been against something (see systems/movement.py)
    ecs.register("veh", np.float32, (3,), 0.0)
    ecs.register("yaw", np.float32)
    ecs.register("pitch", np.float32)
    ecs.register("leg_yaw", np.float32)
    ecs.register("turret_yaw", np.float32)
    # Where the gunner is *looking*, as opposed to where the metal points.  For
    # a mech the sight is a free turret on the roof: it answers the view (a
    # pilot's mouse, a crew's intent) instantly, and `yaw` / `pitch` slew
    # towards it at the machine's own rates (`systems/gunnery.py`), never the
    # other way round.  Every unit owns the pair; only a mech's is ever at an
    # angle to its hull.
    ecs.register("sight_yaw", np.float32)
    ecs.register("sight_pitch", np.float32)

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
    ecs.register("vehicle", np.uint8)                # a wheeled hull: steers, does not strafe
    ecs.register("person", np.uint8)                 # a man on foot: a prox fuse goes off on him
    ecs.register("invulnerable", np.uint8)           # rounds stop, damage does nothing
    # a round in the air that can itself be fought: a missile carries a radar
    # cross section, a collision sphere and ten hit points, so a proximity fuse
    # goes off on it and a passing bullet can hole it (`systems/proximity.py`,
    # `systems/mortality.py`).  A plain bullet does not carry the tag, which is
    # what keeps the hit-sphere table (and the fuse, and the AI) costing exactly
    # nothing on the ten thousand rounds of an ordinary firefight.
    ecs.register("interceptable", np.uint8)

    # ---- health ---------------------------------------------------------
    # One hull, one pool: `hp` is *the* health of the entity and `hp <= 0` is
    # what kills it - there is no per-slot mask and no "fatal slot" table,
    # because nothing on the battlefield has a second way to die.
    ecs.register("hp", np.float32, (), 0.0)
    ecs.register("hpmax", np.float32, (), 0.0)
    # A hull built out of separately damaged modules carries one extra pool per
    # arm, and *only* it does: `arm_hp` exists on the archetypes that declare the
    # `arms` tag (the mech), so an APC or a rifleman owns no such buffer at all.
    # The index is the weapon arm - left 0, right 1 - which is what makes
    # `combat.arm_alive` a straight read instead of a lookup table.
    ecs.register("arm_hp", np.float32, (2,), 0.0)
    ecs.register("arm_hpmax", np.float32, (2,), 0.0)
    # `dp` / `de`: penetration and blast armour, copied off the type at spawn so
    # the damage path never reads a definition dict.
    ecs.register("armor", np.float32, (2,), 0.0)
    # who put the last *lethal* hit in - the credit for a kill.  A component,
    # not a dict on the world, so it dies with the entity that owns it.
    ecs.register("last_hit", np.int32, (), -1)

    # ---- weapons --------------------------------------------------------
    ecs.register("weapon", np.uint8, (2,), 0)        # 0 = left arm, 1 = right arm
    ecs.register("ammo", np.int32, (2,), 0)
    ecs.register("mag", np.int32, (2,), 0)
    ecs.register("cool", np.float32, (2,), 0.0)
    ecs.register("reload_t", np.float32, (2,), 0.0)
    ecs.register("trigger", np.uint8, (), 0)         # bit0 left, bit1 right
    ecs.register("trigger_prev", np.uint8, (), 0)    # last tick's bits, for semi auto
    # rearm work: seconds left at the wrench.  A pilot's mech carries it while a
    # bought job (systems/rearm.py) runs; `player_input` and every firing path
    # read it, so the job holds the machine still and silent until it is back to
    # zero.  Idle is 0.0 - every unit gets the slot, only a player's mech uses it.
    ecs.register("work_remaining_time", np.float32, (), 0.0)

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

    # ---- raid (battle mode) ---------------------------------------------
    # A raider is a *mission*, not a type: the hulls are ordinary mechs that
    # carry the `raider` tag, which is what takes them out of the lane AI's
    # hands (modes/battle/raids.py drives them instead) and what `raids.run`
    # queries.  `raid_goal` and `raid_i` are the state of the plan being
    # executed - which town, and how far along its own way point list - and
    # `raid_party` which launch it belongs to.  The list itself lives on the
    # world, keyed by (faction, party, lane), and the `lane` component is the
    # one the raider was sent to cut.  Only an entity carrying `raider` owns any
    # of these at all.
    ecs.register("raider", np.uint8)
    ecs.register("raid_goal", np.uint8, (), 255)     # index into world.towns, 255 = the enemy base
    ecs.register("raid_i", np.int32)
    ecs.register("raid_party", np.uint8)             # which launch a hull belongs to
    ecs.register("raid_stuck", np.float32)           # seconds spent pushing at something
    ecs.register("raid_t", np.float32)               # seconds left of the HOLD phase

    # ---- misc -----------------------------------------------------------
    # a hull a pilot dropped from the console (`debug_spawn`) on a mode whose own
    # units stay passive.  The tag is what puts it under the range's ground AI
    # (`modes/range/test_ai.py`) without ever catching the mode's passive targets
    # or a tool's own scripted shooter - a mission marker, not a type, exactly
    # like `raider`.
    ecs.register("range_drone", np.uint8)
    ecs.register("flags", np.uint8)
    ecs.register("home_x", np.float32)               # wandering civilians
    ecs.register("home_z", np.float32)

    # ---- debris ---------------------------------------------------------
    ecs.register("wreck_t", np.float32)              # seconds left before it fades
    ecs.register("wreck_kind", np.uint8)             # 0 infantry 1 apc 2 mech

    # ---- blast ----------------------------------------------------------
    # An explosion is an entity (systems/blasts.py): whoever makes one - a round
    # going off, a proximity fuse, a wreck - only has to create it, and the one
    # implementation of area damage reads it a stage later.  Nothing else owns
    # these buffers, so the cost is paid by explosions and by nobody else.
    ecs.register("blast", np.uint8)                  # a burst in the air
    ecs.register("blast_power", np.float32, (2,), 0.0)   # [ed, er]
    ecs.register("blast_t", np.float32)              # seconds left (<= dt: one tick)
    ecs.register("blast_friendly", np.uint8)         # 0 = enemies only, 1 = no sides
    ecs.register("blast_skip", np.int32, (), -1)     # already hit directly
    ecs.register("blast_pid", np.int32, (), -1)      # credit, resolved at creation

    # ---- smoke ----------------------------------------------------------
    # A pull throws `canister` entities out of the hull; each one flies, then
    # trades the `canister` tag for `smoke` and becomes an oblate ellipsoid that
    # eats missile lock-on.  Neither has `unit` or `faction`, so nothing can
    # target them and rounds fly straight through - that is a property of the
    # queries, not a rule each system has to remember.
    ecs.register("canister", np.uint8)              # in flight, not yet a cloud
    ecs.register("smoke", np.uint8)                  # deployed puff
    ecs.register("smoke_r", np.float32, (3,), 0.0)  # ellipsoid semiaxes, metres
    ecs.register("smoke_t", np.float32)             # seconds to pop / of life left
    ecs.register("smoke_s", np.float32, (), 1.0)    # this canister's size factor

    # ---- projectile -----------------------------------------------------
    ecs.register("proj_kind", np.uint8)              # 0 bullet 1 rocket 2 saclos 3 top
    ecs.register("proj_wid", np.uint8)
    ecs.register("proj_life", np.float32)
    ecs.register("proj_src", np.int32, (), -1)
    ecs.register("proj_age", np.float32)
    ecs.register("proj_seed", np.float32)            # per round, drives the thrust jitter
    # where the round started this tick: the prox fuse solves the leg it just
    # flew (`proj_prev` -> `pos`), because at 36 m per tick a distance check at
    # the tick boundaries would step clean over a man
    ecs.register("proj_prev", np.float32, (3,), 0.0)
    # metres flown since the muzzle: a wire-guided round has a maximum flight
    # distance and cooks off where it runs out (`defs.WEAPONS[...]["maxdist"]`)
    ecs.register("proj_trav", np.float32)

    # ---- seeker ---------------------------------------------------------
    # What the IR seeker is looking at, kept on the round rather than derived
    # from a live target id, because the target does not have to survive the
    # flight: `tgt_mem` is the point it stares at (the deck of the hull it will
    # dive on) and `tgt_vel` the velocity that hull had when it was last seen.
    # While the lock holds both are refreshed every tick; once the lock is gone
    # the stare point keeps walking at that velocity and guidance follows it
    # exactly as it followed the hull (`seeker.py`).  A round fired without a
    # lock owns (0, 0, 0) in `tgt_mem`, which is what says it has nothing to
    # stare at and is a rock with a warhead.
    ecs.register("tgt_mem", np.float32, (3,), 0.0)
    ecs.register("tgt_vel", np.float32, (3,), 0.0)


UNIT_COMPS = ["pos", "vel", "drive", "veh", "yaw", "pitch", "leg_yaw", "turret_yaw",
              "sight_yaw", "sight_pitch", "etype", "faction",
              "owner", "hp", "hpmax", "armor", "last_hit",
              "weapon", "ammo", "mag", "cool", "reload_t",
              "trigger", "trigger_prev", "work_remaining_time", "target", "ai_state", "ai_timer",
              "path_i", "lane", "spot_x", "spot_y", "spot_z", "spot_t", "flags"]

# the extra pair of pools a modular hull (the mech) carries, added by the factory
# when the type declares the `arms` tag
ARM_COMPS = ("arm_hp", "arm_hpmax")

CIV_COMPS = UNIT_COMPS + ["home_x", "home_z"]

DEBRIS_COMPS = ["pos", "vel", "yaw", "pitch", "leg_yaw", "turret_yaw", "etype", "faction",
                "owner", "hp", "hpmax", "flags", "wreck_t", "wreck_kind"]

BLAST_COMPS = ["pos", "faction", "blast_power", "blast_t", "blast_friendly",
               "blast_skip", "blast_pid", "blast"]

PROJ_COMPS = ["pos", "vel", "faction", "proj_kind", "proj_wid", "proj_life", "proj_src",
              "proj_age", "proj_seed", "proj_prev", "proj_trav", "target"]

# A missile is a *different archetype* from a bullet - the same rule the smoke
# canister and the puff obey: the component set is the type, and it is decided
# once at spawn.  It owns a pool of health and its own armour, so `combat.apply`
# and `combat.hit`/`splash` work on it with no special case anywhere, and the
# `interceptable` tag is what puts it in the hit-sphere table and the fuse's
# target set.  A 5.56 mm round never pays for any of it.  The seeker's memory
# (`tgt_mem` / `tgt_vel`) rides along with the pool: a guided round is the only
# thing on the field that has to remember where something it can no longer see
# was going.
MISSILE_COMPS = PROJ_COMPS + ["interceptable", "hp", "hpmax", "armor", "last_hit",
                              "tgt_mem", "tgt_vel"]

# A canister in flight and the cloud it bursts into are two different things, so
# they are two archetypes: the flying charge carries a fuse and a velocity, the
# deployed puff carries neither and is not going anywhere.  `smoke_t` is the
# fuse for the one and the remaining life for the other.
CANISTER_COMPS = ["pos", "vel", "smoke_r", "smoke_t", "smoke_s", "canister"]
PUFF_COMPS = ["pos", "smoke_r", "smoke_t", "smoke_s", "smoke"]
