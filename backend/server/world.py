"""The world: owns the ECS, the terrain, the players and the system pipeline."""

from __future__ import annotations

import math
import random

import numpy as np

from . import components, defs, mathutil as mu
from . import terrain as terrain_mod
from .ecs import ECS


class Player:
    def __init__(self, pid: int, name: str, faction: int):
        self.pid = pid
        self.name = name
        self.faction = faction
        self.mech = -1
        self.money = 600
        self.kills = 0
        self.deaths = 0
        self.respawn = 0.0
        self.in_yaw = 0.0
        self.in_pitch = 0.0
        self.in_move = (0.0, 0.0)
        self.fire = (False, False)
        self.lock_target = -1
        self.lock_progress = 0.0
        self.loadout = [defs.WID_AC30, defs.WID_ROCKET]
        self.last_seq = 0
        self.score = 0


class World:
    def __init__(self, seed: int = 11):
        self.terrain = terrain_mod.generate(seed)
        self.ecs = ECS(8192)
        components.register(self.ecs)
        self.rng = random.Random(2024)
        self.players: dict[int, Player] = {}
        self.next_pid = 1
        self.time = 0.0
        self.tick = 0
        self.wave = 0
        self.wave_timer = 8.0
        self.winner = -1
        self.end_timer = 0.0
        self.events: list[dict] = []
        self.last_hit: dict[int, int] = {}
        self.towns = self.terrain.towns
        self.lanes = self.terrain.lanes
        self.lanes_rev = [list(reversed(l)) for l in self.terrain.lanes]
        self.base_entity = {}
        self.spawn_bases()
        self.spawn_civilians()
        self.assign_home_towns()
        self._build_pipeline()

    # ------------------------------------------------------------------
    # setup
    # ------------------------------------------------------------------
    def _build_pipeline(self):
        from .systems import ai, locking, movement, player_input, projectiles, spawner, weapons
        self.systems = [player_input.run, ai.run, locking.run, weapons.run, movement.run,
                        projectiles.run, spawner.run]

    def spawn_bases(self):
        for faction, (bx, bz) in self.terrain.bases.items():
            eid = self.spawn_unit(defs.ETYPE_BASE, faction, bx, bz)
            self.base_entity[faction] = eid
            # decorative guard turret mechs stay at home
            for k in range(2):
                ang = self.rng.random() * math.tau
                self.spawn_unit(defs.ETYPE_MECH, faction,
                                bx + math.cos(ang) * 55.0, bz + math.sin(ang) * 55.0,
                                hp_scale=1.0, lane=0)

    def assign_home_towns(self):
        """Both sides start holding their closest towns: staging areas for
        reinforcements and forward respawn points."""
        for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
            base = self.terrain.bases[faction]
            nearest = sorted(self.towns, key=lambda t: math.dist((t["x"], t["z"]), base))
            for town in nearest[:3]:
                town["owner"] = faction
                town["progress"] = 0.0

    def spawn_civilians(self):
        for town in self.towns:
            for _ in range(self.rng.randint(1, 3)):
                ang = self.rng.random() * math.tau
                r = self.rng.uniform(15.0, 70.0)
                self.spawn_unit(defs.ETYPE_CIV, defs.FACTION_CIV,
                                town["x"] + math.cos(ang) * r, town["z"] + math.sin(ang) * r)

    # ------------------------------------------------------------------
    # entity factories
    # ------------------------------------------------------------------
    def spawn_unit(self, etype: int, faction: int, x: float, z: float, owner: int = -1,
                   weapons=None, hp_scale: float = 1.0, lane: int = -1):
        e = self.ecs
        d = defs.UNIT_DEFS[etype]
        eid = e.create()
        comps = components.CIV_COMPS if etype == defs.ETYPE_CIV else components.UNIT_COMPS
        for c in comps:
            e.add_comp(eid, c)
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

    def spawn_mech(self, player: Player):
        base = self.terrain.bases[player.faction]
        x, z = self._spawn_point(player.faction, base)
        eid = self.spawn_unit(defs.ETYPE_MECH, player.faction, x, z, owner=player.pid,
                              weapons=player.loadout)
        self.ecs["yaw"][eid] = self.rng.random() * math.tau
        self.ecs["leg_yaw"][eid] = self.ecs["yaw"][eid]
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
        self.players[pid] = pl
        self.spawn_mech(pl)
        return pl

    def remove_player(self, pid: int):
        pl = self.players.pop(pid, None)
        if not pl:
            return
        if pl.mech >= 0 and self.ecs.alive[pl.mech]:
            self.ecs.destroy(pl.mech)
        pl.mech = -1

    # ------------------------------------------------------------------
    # queries
    # ------------------------------------------------------------------
    def units(self) -> np.ndarray:
        return self.ecs.query("etype", "hp")

    def combat_units(self) -> np.ndarray:
        ids = self.units()
        t = self.ecs["etype"][ids]
        keep = (t != defs.ETYPE_BASE) & (t != defs.ETYPE_CIV)
        return ids[keep]

    def projectiles(self) -> np.ndarray:
        return self.ecs.query("proj_kind")

    # ------------------------------------------------------------------
    # damage
    # ------------------------------------------------------------------
    def damage(self, eid: int, module: int, amount: float, pid: int = -1):
        e = self.ecs
        if not e.alive[eid] or amount <= 0:
            return
        hp = e["hp"]
        etype = int(e["etype"][eid])
        if pid >= 0:
            self.last_hit[eid] = pid
        if etype == defs.ETYPE_MECH:
            if hp[eid, module] <= 0.0:
                module = defs.MOD_TORSO
            hp[eid, module] = max(0.0, float(hp[eid, module]) - amount)
            dead = hp[eid, defs.MOD_TORSO] <= 0.0 or hp[eid, defs.MOD_CHASSIS] <= 0.0
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

    def kill(self, eid: int, pid: int = -1):
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

        e.destroy(eid)
        self.last_hit.pop(eid, None)

    def emit(self, kind: str, **data):
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
    def _spawn_point(self, faction: int, fallback):
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
        if best is not None and best_d > 900.0:
            return best["x"], best["z"]
        bx, bz = fallback
        for _ in range(12):
            ang = self.rng.random() * math.tau
            r = self.rng.uniform(46.0, 90.0)
            x = bx + math.cos(ang) * r
            z = bz + math.sin(ang) * r
            if not self.terrain.blocked(x, z):
                return x, z
        return bx + 60.0, bz + 60.0

    def lane_index_near(self, lane: int, x: float, z: float) -> int:
        pts = self.lanes[lane % len(self.lanes)]
        best_i, best_d = 0, 1e18
        for i, (px, pz) in enumerate(pts):
            d = (px - x) ** 2 + (pz - z) ** 2
            if d < best_d:
                best_d, best_i = d, i
        return best_i

    # ------------------------------------------------------------------
    # tick
    # ------------------------------------------------------------------
    def update(self, dt: float):
        self.time += dt
        self.tick += 1
        for system in self.systems:
            system(self, dt)
        if self.winner >= 0:
            self.end_timer -= dt
            if self.end_timer <= 0.0:
                self.reset_match()

    def reset_match(self):
        """Wipe the battlefield and start a fresh round on the same map."""
        e = self.ecs
        for eid in list(e.query()):
            e.destroy(eid)
        self.last_hit.clear()
        self.events.clear()
        self.winner = -1
        self.end_timer = 0.0
        self.wave = 0
        self.wave_timer = 8.0
        for town in self.towns:
            town["owner"] = -1
            town["progress"] = 0.0
        self.base_entity = {}
        self.spawn_bases()
        self.spawn_civilians()
        self.assign_home_towns()
        for pl in self.players.values():
            pl.mech = -1
            pl.respawn = 3.0
            pl.lock_target = -1
            pl.lock_progress = 0.0
        self.emit("newmatch")
