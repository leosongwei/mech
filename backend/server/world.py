"""The world: owns the ECS, the terrain, the players and the system pipeline."""

from __future__ import annotations

import math
import random
from typing import TYPE_CHECKING, Callable

import numpy as np

from . import components, defs, modes
from .ecs import ECS
from .grid import Grid
from .terrain import TILE

if TYPE_CHECKING:
    from .modes import Mode
    from .terrain import Terrain


class Player:
    def __init__(self, pid: int, name: str, faction: int) -> None:
        self.pid: int = pid
        self.name: str = name
        self.faction: int = faction
        self.mech: int = -1                  # entity id, -1 while dead
        self.money: float = 600.0
        self.kills: int = 0
        self.deaths: int = 0
        self.respawn: float = 0.0
        self.death_pos: tuple[float, float] | None = None   # where the hull died
        self.in_yaw: float = 0.0
        self.in_pitch: float = 0.0
        self.in_move: tuple[float, float] = (0.0, 0.0)
        self.fire: tuple[bool, bool] = (False, False)
        self.lock_target: int = -1
        self.lock_progress: float = 0.0
        self.loadout: list[int] = list(defs.DEFAULT_LOADOUT)
        self.last_seq: int = 0
        self.score: int = 0
        self.zoom: float = 1.0         # optics magnification, mirrored for the IR seeker
        self.zero_range: float = defs.ZERO_DEFAULT   # sight setting, metres
        self.aspect: float = 16.0 / 9.0
        self.smoke_q: bool = False     # one press of Q, consumed by systems/smoke.py
        self.smoke_cd: float = 0.0     # seconds until the next pull is allowed
        self.smoke_charges: int = defs.SMOKE_CHARGES
        self.god: bool = False         # debug: ignore all damage to this pilot
        # the rearm job the pilot paid for: the priced lines, and how long the
        # whole thing took.  The countdown itself lives in the `work_remaining_time`
        # component on the mech (systems/rearm.py); `work_total` is only what the
        # progress bar needs to draw a fraction against.
        self.work: list[dict] | None = None
        self.work_total: float = 0.0

    def end_work(self) -> None:
        """Drop the rearm job - it finished, the mech died, or a round restarted."""
        self.work = None
        self.work_total = 0.0


class World:
    def __init__(self, seed: int = 11, mode: str = "battle") -> None:
        self.mode_mod: Mode = modes.get(mode)
        self.mode: str = self.mode_mod.name
        self.terrain: Terrain = self.mode_mod.terrain(seed)
        self.ecs: ECS = ECS()
        components.register(self.ecs)
        self.grid: Grid = Grid(self.terrain.w * TILE, self.terrain.d * TILE)
        self.rng: random.Random = random.Random(2024)
        self.players: dict[int, Player] = {}
        self.next_pid: int = 1
        self.time: float = 0.0
        self.tick: int = 0
        self.wave: int = 0
        self.wave_timer: float = 8.0
        self.winner: int = -1
        self.end_timer: float = 0.0
        self.events: list[dict] = []
        self._sph: tuple | None = None      # this tick's hit-sphere table
        self._sph_tick: int = -1
        self.towns: list[dict] = self.terrain.towns
        self.lanes: list[list[tuple[float, float]]] = self.terrain.lanes
        self.lanes_rev: list[list[tuple[float, float]]] = [
            list(reversed(l)) for l in self.terrain.lanes]
        self.base_entity: dict[int, int] = {}
        self.targets: object | None = None      # range mode only
        self.interceptor: object | None = None  # range mode only: the missile post
        self.mode_mod.setup(self)
        self._build_pipeline()

    # ------------------------------------------------------------------
    # setup
    # ------------------------------------------------------------------
    def _build_pipeline(self) -> None:
        from .systems import (blasts, debris, economy, gunnery, locking, mortality,
                              movement, player_input, projectiles, proximity, rearm,
                              smoke, weapons)
        self.systems: list[Callable[[World, float], None]] = [rearm.run, player_input.run]
        self.systems += self.mode_mod.pre_systems()
        # the crews have said where they are looking (player_input, the mode's
        # own AI); the machines now slew towards it, so `yaw` / `pitch` are the
        # laid gun by the time anything fires or draws
        self.systems += [gunnery.run, locking.run, smoke.run, weapons.run, movement.run,
                         projectiles.run, proximity.run]
        # the damage chain: a round and a burst both *write* hp where they land
        # (combat.apply), the bursts are judged one system after the rounds that
        # made them, and only `mortality` decides that a hull has died - so no
        # system ever destroys an entity while walking its own list
        self.systems += [blasts.run, mortality.run, economy.run, debris.run]
        self.systems += self.mode_mod.post_systems()

    # ------------------------------------------------------------------
    # entity factories
    # ------------------------------------------------------------------
    def spawn_unit(self, etype: int, faction: int, x: float, z: float, owner: int = -1,
                   weapons: list[int] | None = None, hp_scale: float = 1.0,
                   lane: int = -1, extra: tuple[str, ...] = ()) -> int:
        e = self.ecs
        d = defs.UNIT_DEFS[etype]
        # the component set *is* the entity's type, and it is decided once, here:
        # the shared bundle plus the tags this etype declares (`unit`, `combat`,
        # `moves`...), which is what every system queries instead of switching
        # on `etype`.  `extra` is what a *mission* adds on top of the type - the
        # `raider` tag and the plan it is executing (modes/battle/raids.py).
        comps = components.CIV_COMPS if etype == defs.ETYPE_CIV else components.UNIT_COMPS
        if d.get("arms"):
            comps = (*comps, *components.ARM_COMPS)
        eid = e.create((*comps, *d["tags"], *extra))
        yaw = self.rng.random() * math.tau
        e["pos"][eid] = (x, self.terrain.height_at(x, z), z)
        e["vel"][eid] = (0.0, 0.0, 0.0)
        e["yaw"][eid] = yaw
        e["turret_yaw"][eid] = yaw
        e["leg_yaw"][eid] = yaw
        e["pitch"][eid] = 0.0
        # the sight starts on the hull's own line of sight, or a fresh mech
        # would swing its hull round to nothing on its first tick
        e["sight_yaw"][eid] = yaw
        e["sight_pitch"][eid] = 0.0
        e["etype"][eid] = etype
        e["faction"][eid] = faction
        e["owner"][eid] = owner
        # one pool, and `hp <= 0` is the whole of "dead"; a hull with arms gets
        # one pool more per arm, which is the only difference there is
        hp = float(d["hp"]) * hp_scale
        e["hp"][eid] = hp
        e["hpmax"][eid] = hp
        if d.get("arms"):
            arms = np.asarray(d["arms"], np.float32) * hp_scale
            e["arm_hp"][eid] = arms
            e["arm_hpmax"][eid] = arms
        e["armor"][eid] = (float(d.get("dp", 0.0)), float(d.get("de", 0.0)))
        ws = weapons if weapons is not None else d["weapons"]
        for arm in (0, 1):
            wid = int(ws[arm])
            wd = defs.WEAPONS[wid]
            e["weapon"][eid, arm] = wid
            e["mag"][eid, arm] = wd["mag"]
            e["ammo"][eid, arm] = wd["reserve"]
        e["lane"][eid] = lane if lane >= 0 else 255
        # a unit that was given a lane starts on the stretch it spawned nearest
        # to - a car told to follow the lane from its first way point would
        # drive back to the base before setting off
        e["path_i"][eid] = self.lane_index_near(lane, x, z, faction) if lane >= 0 else 0
        e["ai_state"][eid] = 0
        if etype == defs.ETYPE_CIV:
            e["home_x"][eid] = x
            e["home_z"][eid] = z
        return eid

    def spawn_mech(self, player: Player) -> int:
        player.smoke_charges = defs.SMOKE_CHARGES
        player.smoke_cd = 0.0
        player.smoke_q = False
        player.end_work()
        spawn = self.mode_mod.mech_spawn(self, player)
        if spawn is None:
            x, z = self.base_spawn(player.faction)
            yaw = self.rng.random() * math.tau
        else:
            x, z, yaw = spawn
        eid = self.spawn_unit(defs.ETYPE_MECH, player.faction, x, z, owner=player.pid,
                              weapons=player.loadout)
        self.ecs["yaw"][eid] = yaw
        self.ecs["leg_yaw"][eid] = yaw
        self.ecs["sight_yaw"][eid] = yaw
        player.mech = eid
        player.respawn = 0.0
        player.death_pos = None     # spent: the next death is its own
        return eid

    # ------------------------------------------------------------------
    # players
    # ------------------------------------------------------------------
    def add_player(self, name: str) -> Player:
        factions = {defs.FACTION_RED: 0, defs.FACTION_BLUE: 0}
        for p in self.players.values():
            if p.faction in factions:
                factions[p.faction] += 1
        faction = min(factions, key=lambda f: factions[f])
        pid = self.next_pid
        self.next_pid += 1
        pl = Player(pid, name or f"PILOT-{pid}", faction)
        if self.mode_mod.fixed_faction is not None:
            pl.faction = self.mode_mod.fixed_faction
        pl.money = self.mode_mod.start_money
        self.players[pid] = pl
        self.spawn_mech(pl)
        return pl

    def remove_player(self, pid: int) -> None:
        pl = self.players.pop(pid, None)
        if not pl:
            return
        if pl.mech >= 0 and self.ecs.alive[pl.mech]:
            self.ecs.destroy(pl.mech)
        pl.mech = -1

    # ------------------------------------------------------------------
    # queries
    # ------------------------------------------------------------------
    def all_units(self) -> np.ndarray:
        """Every entity the client draws as a unit row: hulls, wrecks, corpses."""
        return self.ecs.query("etype", "hp")

    def units(self) -> np.ndarray:
        """Hulls: everything a round can hit and everything that can be damaged.

        Wrecks and corpses dropped their `unit` tag when they died, so they are
        not in here - no exclusion list needed.
        """
        return self.ecs.query("unit")

    def combat_units(self) -> np.ndarray:
        """Fighters: the only things AI may target and the only things that hold a town."""
        return self.ecs.query("combat")

    def lockable_units(self) -> np.ndarray:
        return self.ecs.query("lockable")

    def raiders(self) -> np.ndarray:
        """Hulls on a raiding mission: the ones the raid AI drives off the lanes."""
        return self.ecs.query("raider")

    def movable(self) -> np.ndarray:
        return self.ecs.query("moves")

    def projectiles(self) -> np.ndarray:
        return self.ecs.query("proj_kind")

    def smoke_puffs(self) -> np.ndarray:
        return self.ecs.query("smoke")

    def blasts(self) -> np.ndarray:
        return self.ecs.query("blast")

    def interceptable(self) -> np.ndarray:
        """Rounds in the air that can be fought: the missiles."""
        return self.ecs.query("interceptable")

    def sphere_table(self, dt: float = 1.0 / 30.0) -> tuple:
        """Every hit sphere on the field, built once per tick and shared.

        The unit blocks come first, then one sphere per missile in the air.
        `systems/projectiles.py` sweeps it, `systems/blasts.py` measures against
        it and `systems/proximity.py` fuses on it; the table is the same for all
        three, and it is only asked for before anything is destroyed (mortality
        runs last of the damage chain).  `dt` is what turns a hull's velocity
        into the ground it covered this tick - the sweep solves against a
        moving target (see `projectiles.sphere_table`).
        """
        if self._sph is None or self._sph_tick != self.tick:
            from .systems.projectiles import sphere_table
            self._sph = sphere_table(self, dt)
            self._sph_tick = self.tick
        return self._sph

    def emit(self, kind: str, **data: object) -> None:
        # json.dumps cannot handle numpy scalars, so every payload is
        # normalised to plain python types on the way out
        msg = {"t": "fx", "k": kind}
        for key, value in data.items():
            if isinstance(value, np.generic):
                value = value.item()
            msg[key] = value
        self.events.append(msg)
        if len(self.events) > 400:
            del self.events[:100]

    # ------------------------------------------------------------------
    # helpers used by systems
    # ------------------------------------------------------------------
    def _clear_spot(self, x: float, z: float, radius: float = 4.0) -> bool:
        """True when a whole unit fits at (x, z) - the point test alone would
        let a 1.9 m mech spawn on a tile corner and jam against a tree."""
        t = self.terrain
        if t.blocked(x, z):
            return False
        for k in range(6):
            a = k * math.tau / 6.0
            if t.blocked(x + math.cos(a) * radius, z + math.sin(a) * radius):
                return False
        # the command bases are drawn from primitives, not from tiles, so keep
        # a margin around them as well
        for bx, bz in t.bases.values():
            if math.hypot(x - bx, z - bz) < 55.0:
                return False
        return True

    def base_spawn(self, faction: int) -> tuple[float, float]:
        """A free spot in the ring around the faction's own command base."""
        bx, bz = self.terrain.bases[faction]
        # stand off the command base: its bunker and pylons are solid and a
        # mech that materialises inside one never gets free
        for _ in range(24):
            ang = self.rng.random() * math.tau
            r = self.rng.uniform(70.0, 130.0)
            x = bx + math.cos(ang) * r
            z = bz + math.sin(ang) * r
            if self._clear_spot(x, z):
                return x, z
        # nothing random worked: walk outwards until the ground is free
        for r in range(70, 420, 10):
            for k in range(12):
                ang = k * math.tau / 12.0
                x = bx + math.cos(ang) * r
                z = bz + math.sin(ang) * r
                if self._clear_spot(x, z):
                    return x, z
        return bx, bz

    def lane_index_near(self, lane: int, x: float, z: float, faction: int = defs.FACTION_RED) -> int:
        """Index into the lane as that faction walks it (blue's copy is
        reversed, so its indices are measured from its own base)."""
        pts = self.lanes[lane % len(self.lanes)]
        if faction == defs.FACTION_BLUE:
            pts = self.lanes_rev[lane % len(self.lanes)]
        best_i, best_d = 0, 1e18
        for i, (px, pz) in enumerate(pts):
            d = (px - x) ** 2 + (pz - z) ** 2
            if d < best_d:
                best_d, best_i = d, i
        return best_i

    # ------------------------------------------------------------------
    # tick
    # ------------------------------------------------------------------
    def update(self, dt: float) -> None:
        self.time += dt
        self.tick += 1
        # per-tick scratch: a system that spawns the replacement of a dead slot
        # must not see last tick's deaths
        self.ecs.destroyed.clear()
        self._sph = None
        # refile every hull under the cell it stands in before anyone queries:
        # the build is one argsort, the systems below ask "who is near me"
        # hundreds of times a tick.  Movers *and* combatants: a command base
        # never moves, and the AI has to find it to shoot it
        self.grid.build(self.ecs, ("moves", "combat", "unit"))
        for system in self.systems:
            system(self, dt)
        if self.winner >= 0:
            self.end_timer -= dt
            if self.end_timer <= 0.0:
                self.reset_match()

    def reset_match(self) -> None:
        """Wipe the battlefield and start a fresh round on the same map."""
        e = self.ecs
        for eid in list(e.query()):
            e.destroy(eid)
        self.events.clear()
        self.winner = -1
        self.end_timer = 0.0
        self.mode_mod.setup(self)
        for pl in self.players.values():
            pl.mech = -1
            pl.respawn = self.mode_mod.restart_respawn
            pl.death_pos = None
            pl.lock_target = -1
            pl.lock_progress = 0.0
            pl.end_work()
        self.emit("newmatch")
