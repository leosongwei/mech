"""FastAPI server: static client + websocket game loop."""

from __future__ import annotations

import asyncio
import json
import math
import os
import time
import traceback
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles

import numpy as np

from . import defs, netcode
from .world import World

def _json_safe(obj):
    """numpy scalars are not JSON serialisable - unwrap them."""
    if isinstance(obj, np.generic):
        return obj.item()
    raise TypeError(f"Object of type {obj.__class__.__name__} is not JSON serializable")

ASSETS = Path(__file__).resolve().parent.parent / "assets"

app = FastAPI(title="MECH")
world = World()

class Client:
    """Per connection outbound queue.

    Sends are decoupled from the simulation loop: a slow or half dead socket
    can never stall the 30 Hz tick for everybody else, frames are simply
    dropped when the socket falls behind.
    """

    def __init__(self, ws, pid: int):
        self.ws = ws
        self.pid = pid
        self.q: asyncio.Queue = asyncio.Queue(maxsize=24)
        self.dropped = 0

    def offer(self, kind: str, data):
        try:
            self.q.put_nowait((kind, data))
        except asyncio.QueueFull:
            self.dropped += 1

    async def writer(self):
        try:
            while True:
                kind, data = await self.q.get()
                if kind == "b":
                    await self.ws.send_bytes(data)
                else:
                    await self.ws.send_text(data)
        except Exception:
            pass


CONNECTIONS: dict[int, Client] = {}
STATIC = netcode.static_payload(world)

TICK_DT = 1.0 / 30.0
SNAP_EVERY = 2          # 15 Hz snapshots
META_EVERY = 3          # 10 Hz meta


async def broadcast(tick: int):
    if not CONNECTIONS:
        return
    snap = None
    if tick % SNAP_EVERY == 0:
        snap = netcode.snapshot_bytes(world)
    fx = None
    if world.events:
        fx = list(world.events)
        world.events.clear()
    fx_msg = json.dumps(dict(t="fx", events=fx), default=_json_safe) if fx else None
    for pid, client in list(CONNECTIONS.items()):
        pl = world.players.get(pid)
        if pl is None:
            continue
        if snap is not None:
            client.offer("b", snap)
        if fx_msg is not None:
            client.offer("t", fx_msg)
        if tick % META_EVERY == 0:
            client.offer("t", json.dumps(netcode.meta_payload(world, pl), default=_json_safe))


async def game_loop():
    await asyncio.sleep(0.2)
    next_t = time.perf_counter()
    while True:
        try:
            tick = world.tick + 1
            world.update(TICK_DT)
            await broadcast(tick)
        except Exception:
            traceback.print_exc()
            world.events.clear()
        next_t += TICK_DT
        delay = next_t - time.perf_counter()
        if delay < 0:
            next_t = time.perf_counter()
            delay = 0.0
        await asyncio.sleep(delay)


@app.on_event("startup")
async def _startup():
    asyncio.get_event_loop().create_task(game_loop())


@app.get("/")
async def index():
    f = ASSETS / "index.html"
    if f.exists():
        return FileResponse(f)
    return RedirectResponse("/static/index.html")


# NOTE: the client mount must stay below the websocket route
app.mount("/static", StaticFiles(directory=str(ASSETS)), name="static")


def _at_base(world: World, pl) -> bool:
    if pl.mech < 0 or not world.ecs.alive[pl.mech]:
        return False
    p = world.ecs["pos"][pl.mech]
    bx, bz = world.terrain.bases[pl.faction]
    if math.dist((p[0], p[2]), (bx, bz)) < 220.0:
        return True
    for t in world.towns:
        if t["owner"] == pl.faction and math.dist((p[0], p[2]), (t["x"], t["z"])) < 220.0:
            return True
    return False


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await ws.accept()
    pl = world.add_player("PILOT")
    client = Client(ws, pl.pid)
    CONNECTIONS[pl.pid] = client
    writer = asyncio.create_task(client.writer())
    try:
        init = dict(STATIC)
        init.update(t="init", pid=pl.pid, faction=int(pl.faction), loadout=pl.loadout,
                    name=pl.name, respawn=defs.RESPAWN_TIME)
        await ws.send_text(json.dumps(init, default=_json_safe))
        while True:
            raw = await ws.receive_text()
            msg = json.loads(raw)
            t = msg.get("t")
            if t == "in":
                pl.in_yaw = float(msg.get("yaw", 0.0))
                pl.in_pitch = float(msg.get("pitch", 0.0))
                pl.in_move = (float(msg.get("mx", 0.0)), float(msg.get("mz", 0.0)))
                pl.fire = (bool(msg.get("fl", False)), bool(msg.get("fr", False)))
                pl.name = str(msg.get("name", pl.name))[:16]
            elif t == "join":
                # lobby: callsign, side and initial weapon loadout
                fac = int(msg.get("faction", -1))
                if fac in (defs.FACTION_RED, defs.FACTION_BLUE):
                    pl.faction = fac
                name = str(msg.get("name", pl.name)).strip()
                if name:
                    pl.name = name[:16]
                lo = msg.get("loadout")
                if isinstance(lo, list) and len(lo) == 2:
                    clean = []
                    for arm in range(2):
                        wid = int(lo[arm])
                        clean.append(wid if wid in defs.BUYABLE else defs.WID_AC30)
                    pl.loadout = clean
                if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                    world.ecs.destroy(pl.mech)
                pl.mech = -1
                pl.respawn = 0.0
                world.spawn_mech(pl)
                client.offer("t", json.dumps(dict(t="joined", pid=pl.pid, faction=int(pl.faction),
                                                  name=pl.name, loadout=pl.loadout),
                                             default=_json_safe))
            elif t == "buy":
                arm = int(msg.get("arm", 0)) & 1
                wid = int(msg.get("weapon", 0))
                wd = defs.WEAPONS[wid] if 0 <= wid < len(defs.WEAPONS) else None
                if wd and wid in defs.BUYABLE and _at_base(world, pl) and pl.money >= wd["cost"]:
                    pl.money -= wd["cost"]
                    pl.loadout[arm] = wid
                    if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                        world.ecs["weapon"][pl.mech, arm] = wid
                        world.ecs["mag"][pl.mech, arm] = wd["mag"]
                        world.ecs["ammo"][pl.mech, arm] = wd["reserve"]
                        world.ecs["reload_t"][pl.mech, arm] = 0.0
                    client.offer("t", json.dumps(dict(t="info", msg=f"FITTED {wd['name']}"), default=_json_safe))
                else:
                    client.offer("t", json.dumps(dict(t="info", msg="PURCHASE DENIED - NEED MONEY / BE AT BASE"), default=_json_safe))
            elif t == "debug_spawn" and os.environ.get("MECH_DEBUG") == "1":
                # development helper: drop a target in front of the player
                if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                    etype = int(msg.get("etype", defs.ETYPE_APC))
                    dist = float(msg.get("dist", 800.0))
                    ang = float(msg.get("angle", 0.0)) + pl.in_yaw
                    pos = world.ecs["pos"][pl.mech]
                    x = float(pos[0]) + math.cos(ang) * dist
                    z = float(pos[2]) + math.sin(ang) * dist
                    faction = int(msg.get("faction", 1 - pl.faction))
                    eid = world.spawn_unit(etype, faction, x, z,
                                           weapons=msg.get("weapons"), lane=0)
                    client.offer("t", json.dumps(dict(t="info", msg=f"DEBUG SPAWN {etype}"), default=_json_safe))
            elif t == "debug_tp" and os.environ.get("MECH_DEBUG") == "1":
                # development helper: teleport the mech along its yaw
                if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                    dist = float(msg.get("dist", 300.0))
                    ang = float(msg.get("angle", 0.0)) + pl.in_yaw
                    pos = world.ecs["pos"][pl.mech]
                    x = float(pos[0]) + math.cos(ang) * dist
                    z = float(pos[2]) + math.sin(ang) * dist
                    world.ecs["pos"][pl.mech, 0] = x
                    world.ecs["pos"][pl.mech, 2] = z
                    world.ecs["pos"][pl.mech, 1] = world.terrain.height_at(x, z)
            elif t == "debug_hurt" and os.environ.get("MECH_DEBUG") == "1":
                # development helper: wound the player or the enemy base
                if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                    world.damage(pl.mech, int(msg.get("module", 1)),
                                 float(msg.get("amount", 100)), -1)
                if msg.get("base"):
                    base = world.base_entity.get(1 - pl.faction)
                    if base is not None and world.ecs.alive[base]:
                        world.damage(base, 0, float(msg.get("base", 5000)), -1)
            elif t == "debug_god" and os.environ.get("MECH_DEBUG") == "1":
                # development helper: stop the AI from interrupting a test
                pl.god = bool(msg.get("on", True))
                client.offer("t", json.dumps(dict(t="info", msg="GOD MODE " + ("ON" if pl.god else "OFF")),
                                             default=_json_safe))
            elif t == "respawn":
                if pl.mech < 0:
                    pl.respawn = min(pl.respawn, 0.05)
            elif t == "debug_restart" and os.environ.get("MECH_DEBUG") == "1":
                # development helper: wipe the field and start a fresh round
                world.reset_match()
                client.offer("t", json.dumps(dict(t="info", msg="MATCH RESTARTED"),
                                             default=_json_safe))
    except WebSocketDisconnect:
        pass
    except Exception:
        pass
    finally:
        CONNECTIONS.pop(pl.pid, None)
        writer.cancel()
        world.remove_player(pl.pid)
