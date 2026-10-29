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
    ("vx", "<f4"), ("vz", "<f4"),
    ("hp", "<u2", 4),
    ("ammo", "<u2", 2), ("mag", "<u2", 2), ("weapon", "<u2", 2),
    # the sight: where the crew is looking, which is where the hull and the arms
    # are slewing to.  `yaw` / `pitch` above are the laid gun; a mech's head is
    # drawn on these (the client's own head is its camera, so it uses its view)
    ("syaw", "<f4"), ("spitch", "<f4"),
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
assert UNIT_DTYPE.itemsize == 72, UNIT_DTYPE.itemsize
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
        mech=dict(hp=list(defs.UNIT_DEFS[defs.ETYPE_MECH]["hp"]),
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
        lock=dict(frac=defs.LOCK_FRAC, fov=defs.BASE_FOV),
        zero=dict(range=defs.ZERO_RANGE, max_elev=defs.ZERO_MAX_ELEV,
                  steps=list(defs.ZERO_STEPS), default=defs.ZERO_DEFAULT),
    )


def snapshot_bytes(world: World) -> bytes:
    e = world.ecs
    uids = world.all_units()
    pids = world.projectiles()
    arr = np.zeros(len(uids), UNIT_DTYPE)
    if len(uids):
        owner = e["owner"][uids]
        hp = np.clip(e["hp"][uids], 0, 65535).astype(np.uint16)
        ammo = np.clip(e["ammo"][uids], 0, 65535).astype(np.uint16)
        mag = np.clip(e["mag"][uids], 0, 65535).astype(np.uint16)
        trig = e["trigger"][uids]
        rel = e["reload_t"][uids] > 0.0
        flags = np.zeros(len(uids), np.uint8)
        flags |= (trig & 1).astype(np.uint8)
        flags |= ((trig >> 1) & 1).astype(np.uint8) << 1
        flags |= rel[:, 0].astype(np.uint8) << 2
        flags |= rel[:, 1].astype(np.uint8) << 3
        # bits 4-5: what a wreck used to be (0 infantry, 1 apc, 2 mech)
        flags |= (e["wreck_kind"][uids] & 3).astype(np.uint8) << 4
        arr["id"] = uids
        arr["type"] = e["etype"][uids]
        arr["faction"] = e["faction"][uids]
        arr["owner"] = np.where(owner < 0, 255, np.minimum(owner, 254)).astype(np.uint8)
        arr["flags"] = flags
        arr["target"] = np.where(e["target"][uids] < 0, 65535,
                                 np.minimum(e["target"][uids], 65534)).astype(np.uint16)
        arr["x"] = e["pos"][uids, 0]
        arr["y"] = e["pos"][uids, 1]
        arr["z"] = e["pos"][uids, 2]
        arr["yaw"] = e["yaw"][uids]
        arr["pitch"] = e["pitch"][uids]
        arr["turret"] = e["turret_yaw"][uids]
        arr["leg"] = e["leg_yaw"][uids]
        arr["vx"] = e["vel"][uids, 0]
        arr["vz"] = e["vel"][uids, 2]
        arr["hp"] = hp
        arr["ammo"] = ammo
        arr["mag"] = mag
        arr["weapon"] = e["weapon"][uids].astype(np.uint16)
        arr["syaw"] = e["sight_yaw"][uids]
        arr["spitch"] = e["sight_pitch"][uids]

    parr = np.zeros(len(pids), PROJ_DTYPE)
    if len(pids):
        parr["id"] = pids
        parr["kind"] = e["proj_kind"][pids]
        parr["wid"] = e["proj_wid"][pids]
        parr["faction"] = e["faction"][pids]
        parr["x"] = e["pos"][pids, 0]
        parr["y"] = e["pos"][pids, 1]
        parr["z"] = e["pos"][pids, 2]
        parr["vx"] = e["vel"][pids, 0]
        parr["vy"] = e["vel"][pids, 1]
        parr["vz"] = e["vel"][pids, 2]

    sids = world.ecs.query("smoke_r")          # canisters and puffs alike
    sarr = np.zeros(len(sids), SMOKE_DTYPE)
    if len(sids):
        sarr["id"] = sids
        flags = np.zeros(len(sids), np.uint8)
        flags |= world.ecs.has_arr(sids, "canister").astype(np.uint8)
        sarr["flags"] = flags
        sarr["x"] = e["pos"][sids, 0]
        sarr["y"] = e["pos"][sids, 1]
        sarr["z"] = e["pos"][sids, 2]
        sarr["rx"] = e["smoke_r"][sids, 0]
        sarr["ry"] = e["smoke_r"][sids, 1]
        sarr["rz"] = e["smoke_r"][sids, 2]
        sarr["t"] = e["smoke_t"][sids]

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
    hp = e["hp"][pl.mech].tolist() if mech_ok else [0, 0, 0, 0]
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
