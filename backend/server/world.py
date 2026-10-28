"""The world: owns the ECS, the terrain, the players and the system pipeline."""

from __future__ import annotations

import math
import random
from typing import TYPE_CHECKING, Callable

import numpy as np

from . import components, defs, mathutil as mu, modes
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
        self.in_yaw: float = 0.0
        self.in_pitch: float = 0.0
        self.in_move: tuple[float, float] = (0.0, 0.0)
        self.fire: tuple[bool, bool] = (False, False)
        self.lock_target: int = -1
        self.lock_progress: float = 0.0
        self.loadout: list[int] = [defs.WID_AC30, defs.WID_ROCKET]
        self.last_seq: int = 0
        self.score: int = 0
        self.zoom: float = 1.0         # optics magnification, mirrored for the IR seeker
        self.zero_range: float = defs.ZERO_DEFAULT   # sight setting, metres
        self.aspect: float = 16.0 / 9.0
        self.smoke_q: bool = False     # one press of Q, consumed by systems/smoke.py
        self.smoke_cd: float = 0.0     # seconds until the next pull is allowed
        self.smoke_charges: int = defs.SMOKE_CHARGES
        self.god: bool = False         # debug: ignore all damage to this pilot


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
        self.last_hit: dict[int, int] = {}
        self.towns: list[dict] = self.terrain.towns
        self.lanes: list[list[tuple[float, float]]] = self.terrain.lanes
        self.lanes_rev: list[list[tuple[float, float]]] = [
            list(reversed(l)) for l in self.terrain.lanes]
        self.base_entity: dict[int, int] = {}
        self.targets: object | None = None      # range mode only
        self.mode_mod.setup(self)
        self._build_pipeline()

    # ------------------------------------------------------------------
    # setup
    # ------------------------------------------------------------------
    def _build_pipeline(self) -> None:
        from .systems import (debris, economy, locking, movement, player_input, projectiles,
                              proximity, smoke, weapons)
        self.systems: list[Callable[[World, float], None]] = [player_input.run]
        self.systems += self.mode_mod.pre_systems()
        self.systems += [locking.run, smoke.run, weapons.run, movement.run, projectiles.run,
                         proximity.run, economy.run, debris.run]
        self.systems += self.mode_mod.post_systems()

    # ------------------------------------------------------------------
    # entity factories
    # ------------------------------------------------------------------
    def spawn_unit(self, etype: int, faction: int, x: float, z: float, owner: int = -1,
                   weapons: list[int] | None = None, hp_scale: float = 1.0,
                   lane: int = -1) -> int:
        e = self.ecs
        d = defs.UNIT_DEFS[etype]
        # the component set *is* the entity's type, and it is decided once, here:
        # the shared bundle plus the tags this etype declares (`unit`, `combat`,
        # `moves`...), which is what every system queries instead of switching
        # on `etype`
        comps = components.CIV_COMPS if etype == defs.ETYPE_CIV else components.UNIT_COMPS
        eid = e.create((*comps, *d["tags"]))
        yaw = self.rng.random() * math.tau
        e["pos"][eid] = (x, self.terrain.height_at(x, z), z)
        e["vel"][eid] = (0.0, 0.0, 0.0)
        e["yaw"][eid] = yaw
        e["turret_yaw"][eid] = yaw
        e["leg_yaw"][eid] = yaw
        e["pitch"][eid] = 0.0
        e["etype"][eid] = etype
        e["faction"][eid] = faction
        e["owner"][eid] = owner
        hp = np.asarray(d["hp"], np.float32) * hp_scale
        e["hp"][eid] = hp
        e["hpmax"][eid] = hp
        ws = weapons if weapons is not None else d["weapons"]
        for arm in (0, 1):
            wid = int(ws[arm])
            wd = defs.WEAPONS[wid]
            e["weapon"][eid, arm] = wid
            e["mag"][eid, arm] = wd["mag"]
            e["ammo"][eid, arm] = wd["reserve"]
        e["lane"][eid] = lane if lane >= 0 else 255
        e["path_i"][eid] = 0
        e["ai_state"][eid] = 0
        if etype == defs.ETYPE_CIV:
            e["home_x"][eid] = x
            e["home_z"][eid] = z
        return eid

    def spawn_mech(self, player: Player) -> int:
        player.smoke_charges = defs.SMOKE_CHARGES
        player.smoke_cd = 0.0
        player.smoke_q = False
        spawn = self.mode_mod.mech_spawn(self, player)
        if spawn is None:
            base = self.terrain.bases[player.faction]
            x, z = self._spawn_point(player.faction, base)
            yaw = self.rng.random() * math.tau
        else:
            x, z, yaw = spawn
        eid = self.spawn_unit(defs.ETYPE_MECH, player.faction, x, z, owner=player.pid,
                              weapons=player.loadout)
        self.ecs["yaw"][eid] = yaw
        self.ecs["leg_yaw"][eid] = yaw
        player.mech = eid
        player.respawn = 0.0
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

    def movable(self) -> np.ndarray:
        return self.ecs.query("moves")

    def projectiles(self) -> np.ndarray:
        return self.ecs.query("proj_kind")

    def smoke_puffs(self) -> np.ndarray:
        return self.ecs.query("smoke")

    # ------------------------------------------------------------------
    # damage
    # ------------------------------------------------------------------
    def hit(self, eid: int, module: int, wd: dict, dist: float = 0.0, pid: int = -1) -> None:
        """Resolve one round landing on a unit: penetration plus blast.

        `dist` is how far the target's nearest surface is from the burst, in
        metres - 0 for a direct hit - and the blast half falls off to nothing
        over the weapon's `er`.
        """
        e = self.ecs
        if not e.alive[eid] or (wd["ad"] <= 0.0 and wd["ed"] <= 0.0):
            return
        amount = defs.hit_damage(wd, defs.UNIT_DEFS[int(e["etype"][eid])], dist)
        self.damage(eid, module, amount, pid)

    def splash(self, eid: int, module: int, wd: dict, dist: float, pid: int = -1) -> None:
        """Blast-only damage for a unit near (not under) a burst.

        A charge going off a few metres away cannot punch a plate, so the
        penetration half of `hit` does not apply here.
        """
        e = self.ecs
        if not e.alive[eid] or wd["ed"] <= 0.0:
            return
        amount = defs.blast_damage(float(wd["ed"]), float(defs.UNIT_DEFS[int(e["etype"][eid])]["de"]),
                                   float(wd["er"]), dist)
        self.damage(eid, module, amount, pid)

    def damage(self, eid: int, module: int, amount: float, pid: int = -1) -> None:
        e = self.ecs
        if not e.alive[eid] or amount <= 0:
            return
        if e.has(eid, "invulnerable"):
            return                      # concrete: rounds hit it, nothing breaks
        pilot = self.players.get(int(e["owner"][eid]))
        if pilot is not None and pilot.god:
            return
        hp = e["hp"]
        etype = int(e["etype"][eid])
        if pid >= 0:
            self.last_hit[eid] = pid
        if etype == defs.ETYPE_MECH:
            # one hull pool: the torso charges the chassis, and so does a round
            # that hits an arm that is already gone
            if module == defs.MOD_TORSO or hp[eid, module] <= 0.0:
                module = defs.MOD_CHASSIS
            hp[eid, module] = max(0.0, float(hp[eid, module]) - amount)
            dead = hp[eid, defs.MOD_CHASSIS] <= 0.0
            if hp[eid, defs.MOD_LEFT] <= 0.0:
                e["mag"][eid, 0] = 0
                e["ammo"][eid, 0] = 0
            if hp[eid, defs.MOD_RIGHT] <= 0.0:
                e["mag"][eid, 1] = 0
                e["ammo"][eid, 1] = 0
        else:
            hp[eid, 0] = max(0.0, float(hp[eid, 0]) - amount)
            dead = hp[eid, 0] <= 0.0
        if dead:
            self.kill(eid, pid)

    def kill(self, eid: int, pid: int = -1) -> None:
        e = self.ecs
        if not e.alive[eid]:
            return
        etype = int(e["etype"][eid])
        faction = int(e["faction"][eid])
        pos = e["pos"][eid].copy()
        d = defs.UNIT_DEFS.get(etype, {})
        self.emit("boom", x=float(pos[0]), y=float(pos[1]), z=float(pos[2]),
                  r=float(d.get("splat", 2.0)), big=1 if etype in (defs.ETYPE_MECH, defs.ETYPE_BASE) else 0,
                  faction=faction)

        if pid >= 0 and pid in self.players:
            killer = self.players[pid]
            if killer.faction != faction and faction in (defs.FACTION_RED, defs.FACTION_BLUE):
                reward = int(d.get("reward", 0))
                killer.money += reward
                killer.kills += 1
                killer.score += reward
                self.emit("kill", pid=pid, victim=int(e["owner"][eid]) if e["owner"][eid] >= 0 else -1,
                          reward=reward, etype=etype)

        owner = int(e["owner"][eid])
        if owner >= 0:
            pl = self.players.get(owner)
            if pl:
                pl.deaths += 1
                pl.mech = -1
                pl.respawn = defs.RESPAWN_TIME

        if etype == defs.ETYPE_BASE:
            winner = 1 - faction if faction in (0, 1) else -1
            self.winner = winner
            self.end_timer = 40.0
            self.emit("gameover", winner=winner)

        self._leave_debris(etype, faction, pos, float(e["yaw"][eid]))
        e.destroy(eid)
        self.last_hit.pop(eid, None)

    def _leave_debris(self, etype: int, faction: int, pos: np.ndarray, yaw: float) -> None:
        """Corpse for infantry, smoking hulk for vehicles - purely visual."""
        if etype in (defs.ETYPE_INF_RIFLE, defs.ETYPE_INF_ROCKET, defs.ETYPE_CIV):
            kind, life = 0, defs.CORPSE_TIME
            dtype = defs.ETYPE_CORPSE
        elif etype == defs.ETYPE_APC:
            kind, life = 1, defs.WRECK_TIME
            dtype = defs.ETYPE_DEBRIS
        elif etype == defs.ETYPE_MECH:
            kind, life = 2, defs.WRECK_TIME
            dtype = defs.ETYPE_DEBRIS
        else:
            return
        e = self.ecs
        eid = e.create(components.DEBRIS_COMPS)
        e["pos"][eid] = (float(pos[0]), self.terrain.height_at(float(pos[0]), float(pos[2])),
                         float(pos[2]))
        e["yaw"][eid] = yaw
        e["leg_yaw"][eid] = yaw
        e["turret_yaw"][eid] = yaw
        e["etype"][eid] = dtype
        e["faction"][eid] = faction
        e["wreck_t"][eid] = life
        e["wreck_kind"][eid] = kind

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

    def _spawn_point(self, faction: int, fallback: tuple[float, float]) -> tuple[float, float]:
        towns = self.towns
        enemy_base = self.terrain.bases[1 - faction]
        best = None
        best_d = 1e12
        for t in towns:
            if t["owner"] != faction:
                continue
            d = math.dist((t["x"], t["z"]), enemy_base)
            if d < best_d:
                best_d = d
                best = t
        # forward spawn: a ring around the town, never its centre - the middle
        # of a town is exactly where the buildings are
        if best is not None and best_d > 900.0:
            for _ in range(20):
                ang = self.rng.random() * math.tau
                r = self.rng.uniform(40.0, 70.0)
                x = best["x"] + math.cos(ang) * r
                z = best["z"] + math.sin(ang) * r
                if self._clear_spot(x, z):
                    return x, z
        # home spawn: stand off the command base, its bunker and pylons are
        # solid and a mech that materialises inside one never gets free
        bx, bz = fallback
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
        # refile every hull under the cell it stands in before anyone queries:
        # the build is one argsort, the systems below ask "who is near me"
        # hundreds of times a tick
        self.grid.build(self.ecs)
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
        self.last_hit.clear()
        self.events.clear()
        self.winner = -1
        self.end_timer = 0.0
        self.mode_mod.setup(self)
        for pl in self.players.values():
            pl.mech = -1
            pl.respawn = self.mode_mod.restart_respawn
            pl.lock_target = -1
            pl.lock_progress = 0.0
        self.emit("newmatch")
