"""Wire protocol: compressed world init, binary snapshots, JSON meta."""

from __future__ import annotations

import base64
import math
import os
import struct
import zlib
from typing import TYPE_CHECKING

import numpy as np

from . import defs, shop
from . import terrain as terrain_mod

if TYPE_CHECKING:
    from .world import Player, World

MAGIC = 0x4D454348          # "MECH"
SNAP_HZ = 15.0

UNIT_DTYPE = np.dtype([
    ("id", "<u2"), ("type", "u1"), ("faction", "u1"), ("owner", "u1"), ("flags", "u1"),
    ("target", "<u2"),
    ("x", "<f4"), ("y", "<f4"), ("z", "<f4"),
    ("yaw", "<f4"), ("pitch", "<f4"), ("turret", "<f4"), ("leg", "<f4"),
    # the velocity the hull is *actually* moving at (`vel`, not `vel_cmd`), all
    # three axes: a hull following a slope has a vy, and the lead the client
    # solves is the same one the server's AI solves
    ("vx", "<f4"), ("vy", "<f4"), ("vz", "<f4"),
    ("hp", "<u2", 4),
    ("ammo", "<u2", 2), ("mag", "<u2", 2), ("weapon", "<u2", 2),
    # the sight: where the crew is looking, which is where the hull and the arms
    # are slewing to.  `yaw` / `pitch` above are the laid gun; a mech's head is
    # drawn on these (the client's own head is its camera, so it uses its view)
    ("syaw", "<f4"), ("spitch", "<f4"),
    ("pad3", "<u4"),                # keeps the row 80 B, 4-byte aligned
])
PROJ_DTYPE = np.dtype([
    ("id", "<u2"), ("kind", "u1"), ("wid", "u1"), ("faction", "u1"), ("pad", "u1"),
    ("pad2", "<u2"),
    ("x", "<f4"), ("y", "<f4"), ("z", "<f4"),
    ("vx", "<f4"), ("vy", "<f4"), ("vz", "<f4"),
])
# a canister in flight and a deployed puff travel in the same block; bit0 of
# `flags` says which one it is
SMOKE_DTYPE = np.dtype([
    ("id", "<u2"), ("flags", "u1"), ("pad", "u1"),
    ("x", "<f4"), ("y", "<f4"), ("z", "<f4"),
    ("rx", "<f4"), ("ry", "<f4"), ("rz", "<f4"),
    ("t", "<f4"),
])
assert UNIT_DTYPE.itemsize == 80, UNIT_DTYPE.itemsize
assert PROJ_DTYPE.itemsize == 32, PROJ_DTYPE.itemsize
assert SMOKE_DTYPE.itemsize == 32, SMOKE_DTYPE.itemsize


def _b64(arr: np.ndarray) -> str:
    return base64.b64encode(zlib.compress(arr.tobytes(), 6)).decode("ascii")


def static_payload(world: World) -> dict:
    """Everything the client needs once (map, definitions)."""
    t = world.terrain
    heights = np.round(t.h).astype(np.int16)
    return dict(
        map=dict(w=int(t.w), h=int(t.d), tile=float(terrain_mod.TILE),
                 heights=_b64(heights), tiles=_b64(t.tiles),
                 roads=_b64(t.roads), walls=_b64(t.walls)),
        weapons=defs.WEAPONS,
        units={int(k): v for k, v in defs.UNIT_DEFS.items()},
        factions=dict(colors={int(k): v for k, v in defs.FACTION_COLORS.items()},
                      names={int(k): v for k, v in defs.FACTION_NAMES.items()}),
        bases={int(k): v for k, v in t.bases.items()},
        lanes=t.lanes,
        towns=t.towns,
        mech=dict(hp=mech_hp_slots(),
                  hitspheres=defs.MECH_HITSPHERES, eye=defs.MECH_EYE,
                  sight_z=defs.MECH_SIGHT_Z,
                  muzzles={int(k): v for k, v in defs.MECH_ARM_MUZZLE.items()},
                  reach=dict(defs.MECH_ARM_REACH),
                  modules=defs.MODULE_NAMES),
        buyable=defs.BUYABLE,
        rearm=dict(smoke_cost=defs.SMOKE_REFILL_COST,
                   smoke_charges=defs.SMOKE_REFILL_CHARGES,
                   sell_refund=defs.SELL_REFUND,
                   repair_cost=defs.REPAIR_COST_PER_HP,
                   swap_s=defs.REARM_SWAP_S, ammo_s=defs.REARM_AMMO_S,
                   repair_s=defs.REARM_REPAIR_PER_100HP_S),
        debug=os.environ.get("MECH_DEBUG") == "1",
        mode=world.mode,
        # the radar equation's constants (`defs.radar_k`): the client solves the
        # very same `d^2 <= K * v_closing` for the RWR, so `V_REF` and the
        # largest cross-section in the game (the outer ring of the dial) travel
        # rather than being written twice
        radar=dict(v_ref=defs.RADAR_V_REF,
                   rcs=max(w.get("rcs", 0.0) for w in defs.WEAPONS)),
        lock=dict(frac=defs.LOCK_FRAC, fov=defs.BASE_FOV),
        zero=dict(range=defs.ZERO_RANGE, max_elev=defs.ZERO_MAX_ELEV,
                  steps=list(defs.ZERO_STEPS), default=defs.ZERO_DEFAULT),
    )


def mech_hp_slots() -> list[int]:
    """The mech's four hp columns as the client indexes them (`MOD_*`)."""
    d = defs.UNIT_DEFS[defs.ETYPE_MECH]
    arms = list(d.get("arms") or ())
    slots = [0, 0, 0, 0]
    slots[defs.MOD_CHASSIS] = int(d["hp"])
    for a, hp in enumerate(arms[:2]):
        slots[defs.MOD_LEFT + a] = int(hp)
    return slots


def hp_columns(world: World, eids: np.ndarray,
               cols: dict | None = None) -> np.ndarray:
    """``(n, 4)`` uint16 - the hull pool in column 0, the arm pools in 2 and 3.

    Column 1 (`MOD_TORSO`) is always zero: the torso is hit geometry, not a
    health bar.  The layout is the client's (`net.js` reads four u16 per unit),
    so it is built here rather than stored per entity.  ``cols`` is the block
    :meth:`ECS.columns` already gathered for these ids (`hp`, `arm_hp`,
    `armed`), so a snapshot does not resolve the archetypes twice.
    """
    e = world.ecs
    out = np.zeros((eids.size, 4), np.uint16)
    if eids.size == 0:
        return out
    if cols is None:
        cols = e.columns(eids, ("hp", "arm_hp"))
        cols["armed"] = e.has_arr(eids, "arm_hp")
    out[:, defs.MOD_CHASSIS] = np.clip(cols["hp"], 0, 65535).astype(np.uint16)
    armed = cols["armed"]
    if armed.any():
        out[armed, defs.MOD_LEFT:defs.MOD_RIGHT + 1] = np.clip(
            cols["arm_hp"][armed], 0, 65535).astype(np.uint16)
    return out


# every component the unit block carries, gathered in one pass
UNIT_COLUMNS = ("pos", "vel", "owner", "etype", "faction", "target", "hp",
                "arm_hp", "ammo", "mag", "weapon", "yaw", "pitch", "turret_yaw",
                "leg_yaw", "sight_yaw", "sight_pitch", "trigger", "reload_t",
                "wreck_kind")


def snapshot_bytes(world: World) -> bytes:
    e = world.ecs
    uids = world.all_units()
    pids = world.projectiles()
    arr = np.zeros(len(uids), UNIT_DTYPE)
    if len(uids):
        col = e.columns(uids, UNIT_COLUMNS)
        col["armed"] = e.has_arr(uids, "arm_hp")
        owner = col["owner"]
        hp = hp_columns(world, uids, col)
        ammo = np.clip(col["ammo"], 0, 65535).astype(np.uint16)
        mag = np.clip(col["mag"], 0, 65535).astype(np.uint16)
        trig = col["trigger"]
        rel = col["reload_t"] > 0.0
        flags = np.zeros(len(uids), np.uint8)
        flags |= (trig & 1).astype(np.uint8)
        flags |= ((trig >> 1) & 1).astype(np.uint8) << 1
        flags |= rel[:, 0].astype(np.uint8) << 2
        flags |= rel[:, 1].astype(np.uint8) << 3
        # bits 4-5: what a wreck used to be (0 infantry, 1 apc, 2 mech)
        flags |= (col["wreck_kind"] & 3).astype(np.uint8) << 4
        arr["id"] = uids
        arr["type"] = col["etype"]
        arr["faction"] = col["faction"]
        arr["owner"] = np.where(owner < 0, 255, np.minimum(owner, 254)).astype(np.uint8)
        arr["flags"] = flags
        arr["target"] = np.where(col["target"] < 0, 65535,
                                 np.minimum(col["target"], 65534)).astype(np.uint16)
        arr["x"] = col["pos"][:, 0]
        arr["y"] = col["pos"][:, 1]
        arr["z"] = col["pos"][:, 2]
        arr["yaw"] = col["yaw"]
        arr["pitch"] = col["pitch"]
        arr["turret"] = col["turret_yaw"]
        arr["leg"] = col["leg_yaw"]
        arr["vx"] = col["vel"][:, 0]
        arr["vy"] = col["vel"][:, 1]
        arr["vz"] = col["vel"][:, 2]
        arr["hp"] = hp
        arr["ammo"] = ammo
        arr["mag"] = mag
        arr["weapon"] = col["weapon"].astype(np.uint16)
        arr["syaw"] = col["sight_yaw"]
        arr["spitch"] = col["sight_pitch"]

    parr = np.zeros(len(pids), PROJ_DTYPE)
    if len(pids):
        pcol = e.columns(pids, ("proj_kind", "proj_wid", "faction", "pos", "vel"))
        parr["id"] = pids
        parr["kind"] = pcol["proj_kind"]
        parr["wid"] = pcol["proj_wid"]
        parr["faction"] = pcol["faction"]
        parr["x"] = pcol["pos"][:, 0]
        parr["y"] = pcol["pos"][:, 1]
        parr["z"] = pcol["pos"][:, 2]
        parr["vx"] = pcol["vel"][:, 0]
        parr["vy"] = pcol["vel"][:, 1]
        parr["vz"] = pcol["vel"][:, 2]

    sids = world.ecs.query("smoke_r")          # canisters and puffs alike
    sarr = np.zeros(len(sids), SMOKE_DTYPE)
    if len(sids):
        scol = e.columns(sids, ("pos", "smoke_r", "smoke_t"))
        sarr["id"] = sids
        flags = np.zeros(len(sids), np.uint8)
        flags |= world.ecs.has_arr(sids, "canister").astype(np.uint8)
        sarr["flags"] = flags
        sarr["x"] = scol["pos"][:, 0]
        sarr["y"] = scol["pos"][:, 1]
        sarr["z"] = scol["pos"][:, 2]
        sarr["rx"] = scol["smoke_r"][:, 0]
        sarr["ry"] = scol["smoke_r"][:, 1]
        sarr["rz"] = scol["smoke_r"][:, 2]
        sarr["t"] = scol["smoke_t"]

    header = struct.pack("<IIHHH", MAGIC, world.tick, len(uids), len(pids), len(sids))
    return header + arr.tobytes() + parr.tobytes() + sarr.tobytes()


def _arm_meta(world: World, pl: Player, arm: int) -> dict:
    """One arm as the rearm panel needs it: hardware, rounds, and its hp."""
    s = shop.arm_state(world, pl, arm)
    return dict(wid=s["wid"], hp=round(s["hp"]), hpmax=round(s["hpmax"]),
                mag=s["mag"], reserve=s["reserve"])


def meta_payload(world: World, pl: Player) -> dict:
    e = world.ecs
    mech_ok = pl.mech >= 0 and e.alive[pl.mech]
    hp = hp_columns(world, np.asarray([pl.mech])).ravel().tolist() if mech_ok else [0, 0, 0, 0]
    # the job at the wrench, for the progress bar at the bottom of the screen
    work = None
    if mech_ok and pl.work is not None:
        work = dict(t=round(max(0.0, float(e["work_remaining_time"][pl.mech])), 2),
                    total=round(pl.work_total, 2), label=shop.work_label(pl.work))
    return dict(
        t="meta",
        pid=pl.pid,
        mech=pl.mech if mech_ok else -1,
        faction=int(pl.faction),
        money=round(pl.money),
        kills=pl.kills,
        deaths=pl.deaths,
        score=pl.score,
        respawn=round(max(0.0, pl.respawn), 2),
        god=bool(pl.god),
        hp=hp,
        lock_target=int(pl.lock_target) if pl.lock_progress > 0 else -1,
        lock_progress=round(float(pl.lock_progress), 3),
        smoke=int(pl.smoke_charges),
        arms=[_arm_meta(world, pl, arm) for arm in (0, 1)],
        work=work,
        smoke_cd=round(max(0.0, float(pl.smoke_cd)), 1),
        wave=world.wave,
        time=round(world.time, 1),
        winner=int(world.winner),
        end_timer=round(max(0.0, world.end_timer), 1),
        players=[dict(id=p.pid, name=p.name, faction=int(p.faction), kills=p.kills,
                      deaths=p.deaths, money=round(p.money), alive=p.mech >= 0)
                 for p in world.players.values()],
        towns=[dict(x=t["x"], z=t["z"], owner=int(t["owner"]), progress=round(t["progress"], 2),
                    name=t["name"]) for t in world.towns],
    )


# The cues that are carried by the air.  Everything else on the event list is a
# message or a light (a kill, a capture, the rearm label, new tiles) and is not
# gated by distance.
SOUND_KINDS = ("shot", "boom")


def _source_level(ev: dict) -> float | None:
    """How loud this event is at its own muzzle, or None if it makes no sound."""
    k = ev.get("k")
    if k not in SOUND_KINDS:
        return None
    if k == "shot":
        return defs.SOUND_LEVEL["shot"]
    if ev.get("blast"):
        return defs.SOUND_LEVEL["blast"]
    return defs.SOUND_LEVEL["big" if ev.get("big") else "boom"]


def hearable(events: list[dict], ear: tuple[float, float, float] | None) -> list[dict]:
    """The events whose wavefront still arrives loud enough to be heard.

    `ear` is the pilot's mech *now*, not where it will be when the wavefront
    gets there: at 8 km the curve is nearly flat (~3 % per km), so a hull that
    covered another 300 m in the meantime hears a level within a few per cent -
    the gate is not sensitive to it.  `None` (no mech, no ear) drops every sound
    but keeps every message.
    """
    if not events:
        return events
    reach = defs.SOUND_SPEED * defs.SOUND_TTL      # nobody could still hear it
    out = []
    for ev in events:
        level = _source_level(ev)
        if level is None:
            out.append(ev)
            continue
        if ear is None:
            continue
        dist = math.sqrt((ev["x"] - ear[0]) ** 2 + (ev.get("y", 0.0) - ear[1]) ** 2
                         + (ev["z"] - ear[2]) ** 2)
        if dist <= reach and defs.sound_gain(dist, level) > defs.SOUND_FLOOR:
            out.append(ev)
    return out
