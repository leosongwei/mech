"""FastAPI server: static client + websocket game loop."""

from __future__ import annotations

import asyncio
import json
import logging
import math
import os
import time
import traceback
from pathlib import Path
from typing import Awaitable, Callable

from fastapi import FastAPI, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse, RedirectResponse, Response
from fastapi.staticfiles import StaticFiles

import numpy as np

from . import combat, defs, netcode, shop
from .world import Player, World

# uvicorn only configures its own loggers; ours ("mech.*") need a handler of
# their own to land in the server log.  The default is INFO, i.e. no per-weapon
# line: a round bursting, a seeker losing its lock and a missile taking a hit
# are all DEBUG (see AGENTS.md, "Logging"), and `MECH_LOG_LEVEL` is the one knob
# that turns them on.  `MECH_DEBUG` is unrelated - it unlocks the websocket
# development messages.
logging.basicConfig(level=os.environ.get("MECH_LOG_LEVEL", "INFO").upper(),
                    format="%(asctime)s %(levelname)s %(name)s: %(message)s")


def _json_safe(obj: object) -> object:
    """numpy scalars are not JSON serialisable - unwrap them."""
    if isinstance(obj, np.generic):
        return obj.item()
    raise TypeError(f"Object of type {obj.__class__.__name__} is not JSON serializable")

ASSETS = Path(__file__).resolve().parent.parent / "assets"

app = FastAPI(title="MECH")


@app.middleware("http")
async def revalidate_assets(request: Request,
                           call_next: Callable[[Request], Awaitable[Response]]) -> Response:
    """Make the browser revalidate the bundle instead of heuristically caching it.

    A rebuild changes the file but not its URL, and without a Cache-Control
    header browsers happily keep serving the old JavaScript for minutes - with
    its own snapshot layout.  ``no-cache`` still allows a 304, so it costs one
    round trip per file and the client is never stale.
    """
    response = await call_next(request)
    path = request.url.path
    if path == "/" or path.startswith("/static"):
        response.headers["Cache-Control"] = "no-cache"
    return response
MODE: str = os.environ.get("MECH_MODE") or (
    "range" if os.environ.get("MECH_RANGE") == "1" else "battle")
world: World = World(mode=MODE)

class Client:
    """Per connection outbound queue.

    Sends are decoupled from the simulation loop: a slow or half dead socket
    can never stall the 30 Hz tick for everybody else, frames are simply
    dropped when the socket falls behind.
    """

    def __init__(self, ws: WebSocket, pid: int) -> None:
        self.ws: WebSocket = ws
        self.pid: int = pid
        self.q: asyncio.Queue = asyncio.Queue(maxsize=24)
        self.dropped: int = 0

    def offer(self, kind: str, data: bytes | str) -> None:
        try:
            self.q.put_nowait((kind, data))
        except asyncio.QueueFull:
            self.dropped += 1

    async def writer(self) -> None:
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


def _ear(world: World, pl: Player | None) -> tuple[float, float, float] | None:
    """Where a pilot's ear is, or None while he has no hull to sit in."""
    if pl is None or pl.mech < 0 or not world.ecs.alive[pl.mech]:
        return None
    p = world.ecs["pos"][pl.mech]
    return (float(p[0]), float(p[1]), float(p[2]))


async def broadcast(tick: int) -> None:
    if not CONNECTIONS:
        return
    snap = None
    if tick % SNAP_EVERY == 0:
        snap = netcode.snapshot_bytes(world)
    fx = None
    if world.events:
        fx = list(world.events)
        world.events.clear()
    fx_all = json.dumps(dict(t="fx", events=fx), default=_json_safe) if fx else None
    for pid, client in list(CONNECTIONS.items()):
        pl = world.players.get(pid)
        if pl is None:
            continue
        if snap is not None:
            client.offer("b", snap)
        if fx_all is not None:
            # sound is per ear: a bang nobody could hear is not put on the wire
            mine = netcode.hearable(fx, _ear(world, pl))
            if len(mine) == len(fx):
                client.offer("t", fx_all)   # nothing dropped: the shared message
            elif mine:
                client.offer("t", json.dumps(dict(t="fx", events=mine), default=_json_safe))
        if tick % META_EVERY == 0:
            client.offer("t", json.dumps(netcode.meta_payload(world, pl), default=_json_safe))


async def game_loop() -> None:
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
async def _startup() -> None:
    asyncio.get_event_loop().create_task(game_loop())


@app.get("/")
async def index() -> Response:
    f = ASSETS / "index.html"
    if f.exists():
        return FileResponse(f)
    return RedirectResponse("/static/index.html")


# NOTE: the client mount must stay below the websocket route
app.mount("/static", StaticFiles(directory=str(ASSETS)), name="static")


def _at_base(world: World, pl: Player) -> bool:
    if world.mode_mod.rearm_anywhere:
        return True                 # range mode: rearm anywhere
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
async def ws_endpoint(ws: WebSocket) -> None:
    await ws.accept()
    pl = world.add_player("PILOT")
    client = Client(ws, pl.pid)
    CONNECTIONS[pl.pid] = client
    writer = asyncio.create_task(client.writer())
    try:
        init = dict(STATIC)
        init.update(t="init", pid=pl.pid, faction=int(pl.faction), loadout=pl.loadout,
                    name=pl.name)
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
                # optics + viewport aspect drive the IR lock circle
                pl.zoom = max(1.0, min(20.0, float(msg.get("z", pl.zoom))))
                pl.aspect = max(0.4, min(4.0, float(msg.get("a", pl.aspect))))
                # sight setting: a fixed range (CTRL+wheel), not a solved zero
                zr = float(msg.get("zr", pl.zero_range))
                pl.zero_range = max(defs.ZERO_STEPS[0], min(defs.ZERO_STEPS[-1], zr))
                # Q: one-shot hot smoke, latched until systems/smoke.py eats it
                if msg.get("q"):
                    pl.smoke_q = True
                pl.name = str(msg.get("name", pl.name))[:16]
            elif t == "join":
                # lobby: callsign and side.  The machine always comes out on the
                # default pair - a loadout is bought at the wrench, not picked
                # in a menu, and `spawn_mech` hands out the standard one anyway
                fac = int(msg.get("faction", -1))
                if fac in (defs.FACTION_RED, defs.FACTION_BLUE):
                    pl.faction = fac
                if world.mode_mod.fixed_faction is not None:
                    pl.faction = world.mode_mod.fixed_faction
                name = str(msg.get("name", pl.name)).strip()
                if name:
                    pl.name = name[:16]
                if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                    world.ecs.destroy(pl.mech)
                pl.mech = -1
                pl.respawn = 0.0
                pl.death_pos = None
                world.spawn_mech(pl)
                client.offer("t", json.dumps(dict(t="joined", pid=pl.pid, faction=int(pl.faction),
                                                  name=pl.name, loadout=pl.loadout),
                                             default=_json_safe))
            elif t == "shop":
                # the rearm panel: price the whole draft, buy it or refuse it
                if not _at_base(world, pl):
                    client.offer("t", json.dumps(dict(t="info", msg="SHOP DENIED - BE AT BASE / TOWN"),
                                                 default=_json_safe))
                else:
                    req = shop.parse_request(msg)
                    ok, note, _q = shop.deal(world, pl, req)
                    client.offer("t", json.dumps(dict(t="info", msg=note if ok else
                                                      f"SHOP DENIED - {note}"), default=_json_safe))
                    if ok:
                        client.offer("t", json.dumps(dict(t="shop", loadout=pl.loadout),
                                                     default=_json_safe))
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
                    # the range has no lanes at all: a spawned hull just
                    # stands where it was dropped
                    eid = world.spawn_unit(etype, faction, x, z,
                                           weapons=msg.get("weapons"),
                                           lane=0 if world.lanes else -1,
                                           extra=world.mode_mod.console_spawn_extra)
                    client.offer("t", json.dumps(dict(t="info", msg=f"DEBUG SPAWN {etype}"), default=_json_safe))
            elif t == "debug_trees" and os.environ.get("MECH_DEBUG") == "1":
                # development helper: grow a tree stand in front of the player.
                # A tree hides what stands behind it but stops no round, so the
                # sight rules are tested by putting one between the sights and
                # a target - and the client has to draw the same stand it is
                # hiding behind, hence the painted rectangle goes out with it.
                if pl.mech >= 0 and world.ecs.alive[pl.mech]:
                    dist = float(msg.get("dist", 200.0))
                    ang = float(msg.get("angle", 0.0)) + pl.in_yaw
                    pos = world.ecs["pos"][pl.mech]
                    x = float(pos[0]) + math.cos(ang) * dist
                    z = float(pos[2]) + math.sin(ang) * dist
                    i0, j0, rows, cols, patch = world.terrain.plant(
                        x, z, int(msg.get("radius", 2)), bool(msg.get("forest", True)))
                    world.emit("tiles", i0=i0, j0=j0, rows=rows, cols=cols,
                               data=patch.ravel().tolist())
                    client.offer("t", json.dumps(dict(t="info", msg="DEBUG TREES"),
                                                default=_json_safe))
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
                    combat.apply(world, pl.mech, int(msg.get("module", 1)),
                                 float(msg.get("amount", 100)), -1)
                if msg.get("base"):
                    base = world.base_entity.get(1 - pl.faction)
                    if base is not None and world.ecs.alive[base]:
                        combat.apply(world, base, defs.MOD_CHASSIS,
                                     float(msg.get("base", 5000)), -1)
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
