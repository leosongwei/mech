# AGENTS.md

Working notes for coding agents touching **MECH**, a browser-based mech
shooter: numpy-ECS Python server, WebGL2 client, 16 km tile map, three lanes,
RED vs BLUE.

## Read `doc/product` first

`doc/product/` is the Chinese product documentation and the source of truth for
everything a player can see: interaction flow, every screen, and the full unit
and weapon parameter tables (mirrors of `defs.py` and the mode systems).

* **Before changing anything, read `doc/product/`** and check the change fits
  the documented rules.
* **Every product-facing change must be registered there**: update the relevant
  page (`flow.md`, `screens.md`, `units.md`, `weapons.md`) and add a dated entry
  at the top of `doc/product/CHANGELOG.md` in the same commit. That covers UI
  elements, copy, keybindings, unit/weapon numbers, spawn and rule timers,
  win conditions and debug UI.
* Tuning changes in `defs.py` / mode systems without the matching table update
  count as unfinished work. Internal refactors with no player-visible effect do
  not need an entry.

## Architecture

```
dev.sh                     # build / start / attach / stop helper (byobu or tmux)
doc/product/               # Chinese product docs: flow, screens, units, weapons, changelog
backend/
  run.py                   # dev entry point: uvicorn server.main:app
  pyproject.toml           # fastapi, uvicorn[standard], numpy
  server/
    ecs.py                 # archetype ECS: component set is the entity's type
    components.py          # every component the game declares
    defs.py                # entity types, weapons, unit stats, mech geometry
    world.py               # World: terrain, players, spawning, damage, tick
    terrain.py             # 1000x1000 tiles of 16 m -> 16 km square map
    mathutil.py            # angles, lead solving, terrain helpers
    grid.py                # 128 m cell index over the units, refiled every tick (AI broadphase)
    ballistics.py          # fire control: sight setting elevation (player) / sight-point solve (AI)
    netcode.py             # init payload, binary snapshots, JSON meta
    main.py                # FastAPI: websockets, static files, 30 Hz game loop
    systems/               # mechanics shared by every mode, each exposes run(world, dt)
                         #   sight.py: the one line of sight (terrain + smoke), clear/why
                         #   smoke.py: hot smoke canisters, opaque puffs, blocks(a, b)
                         #   proximity.py: 30mm airburst fuse, bursts on people only
                         #   rearm.py: the bought job at the base, `work_remaining_time`
    modes/                 # per-mode rule sets: map, setup, pipeline, spawn rules
      base.py              #   Mode interface every mode implements
      battle/              #   waves.py: town capture + reinforcement waves
                           #   raids.py: the raiding parties - one mech per lane,
                           #   force-gradient + two-level (128 m / 16 m) pathfinding
      range/               #   targets.py: passive APC targets, bunkers.py: ridge blockhouses
                           #   (nothing on the range shoots back)
  assets/                  # compiled client (webpack output, gitignored)
tools/
  replay.py                # scripted battle replay: the simulation's sanity harness
  fixtures/                # its recorded tick summaries (checked in)
  smoke_lock_test.js       # browser-console driver: seeker A/B smoke test
  vehicle_trace.py         # range APC route trace: SVG path plot + per-tick CSV
  raid_trace.py            # a battle's raiders: force-gradient + driven traces as SVG
frontend/
  webpack.config.js        # entry src/main.js -> ../backend/assets/bundle.js
  icons.json               # single source of truth for every HUD icon glyph
  scripts/                 # fetch-material-icons.sh: rebuild the font subset
  vendor/material-symbols/ # committed Material Symbols subset (a few KB) + LICENSE
  src/
    main.js                # boots Game, exposes it as window.__game
    game.js                # client loop, input -> server, camera, fx, settings menu
    icons.js               # icons.json -> glyph strings, FontFace loader, drawIcon()
    net.js                 # websocket, snapshot decode, interpolation
    map.js                 # client copy of terrain + identical ballistics
    scene.js               # unit models built from instanced primitives
    renderer.js            # tiled near terrain + far LOD grid, roads, instanced draws
    hud.js                 # centre cross, arm impact markers, lock box, modules, minimap
    meshes.js              # primitive meshes (box, sphere, cone, cylinder, rock)
    particles.js           # smoke, trails, impacts
    math.js                # mat4, lookAt, compose, dirFrom, angles
    input.js               # pointer lock, keys, wheel, buy hotkeys
    audio.js               # WebAudio cues, master gain + volume setting, SOUND_SPEED + attenuation()
```

### Server

* **ECS** — an *archetype* ECS: the group of entities that own exactly the same
  component set is an `Archetype`, and it holds one dense buffer per component,
  as long as the entities that actually have it. An entity id is not a storage
  address any more — `_arch`/`_row` turn it into (archetype, row). Queries
  return ascending ids (`ecs.query("unit")`); `ecs["pos"]` still answers
  `view[eid]`, `view[eid, i]`, `view[ids]`, `view[ids, i]`, filling a default
  where the entity does not own the component, and `ecs.row(eid, "pos")` is the
  cheap single-entity door.
* **The component set is the entity's type and never changes.** It is decided
  in the one `ECS.create(comps)` call that spawns the entity, from
  `components.UNIT_COMPS` / `*_COMPS` plus `UNIT_DEFS[...]["tags"]`. A state
  transition is a *different* entity: `systems/smoke.py` destroys a canister and
  creates the puff it bursts into. There is deliberately no
  `add_component`/`remove_component` — allowing one would make the archetype key
  space explode, invalidate row indices mid-tick, and turn the mask back into a
  mutable soup.
* **Write systems against buffers, not through `View`.** `ecs.groups(...)`
  hands out an archetype with its buffers hoisted and rows `0..n-1`: reach for
  it whenever the system integrates entities independently (movement, countdown
  timers, leg turning) and the whole pass becomes a handful of numpy calls.
  `ecs.slot(eid)` is the same thing for a per-entity loop that touches many
  components — one lookup, then `bufs["pos"][row, 0]`; `"combat" in bufs` is the
  component test. `ecs.locate(ids)` gives `(rows, archetypes)` when the loop has
  to stay in query order.
* **Iteration order is part of the simulation.** `weapons`, `ai` and the
  spread/acquire draws take numbers from `world.rng` inside a loop over
  entities, so visiting them in a different order produces a different battle.
  Those loops must keep the ascending `query()` order (`locate`/`slot`);
  `groups` (archetype-major) is only for order-free work. `tools/replay.py`
  fails loudly when this is broken.
* **Identity is a component, never a type switch.** What an entity *is* comes
  from the marker components its type declares in
  `defs.UNIT_DEFS[...]["tags"]`: `unit` (a hull rounds stop on and damage
  applies to), `combat` (may be targeted, may hold a town), `lockable` (IR
  seeker), `moves` (locomotion), `invulnerable` (bunker). Systems ask for the
  tag they can handle — `world.units()`, `combat_units()`, `lockable_units()`,
  `movable()`, `smoke_puffs()` — so an entity that is not a unit (a smoke puff,
  a shell) is invisible to combat code without any exclusion list. `etype` is
  per-type *data* (hull geometry, hp slots, which arm fires what) and is only
  read for that.
* **World** (`world.py`) owns terrain, players, the ECS and the system
  pipeline. Shared order: `rearm`, `player_input`, the mode's pre-systems
  (battle: `ai`), `locking, smoke, weapons, movement, projectiles, proximity,
  economy, debris`, then the mode's post-systems (battle: `waves`).
  `World.update(dt)` is the only entry point; the loop in `main.py` calls it at
  a fixed 30 Hz (`TICK_DT = 1/30`).
* **Buying costs time, not just money.** `shop.deal` charges the bill and books
  the priced lines on the pilot, writing the seconds the draft cost into the
  mech's `work_remaining_time` (a plain float, 0.0 = idle); `systems/rearm.py`
  counts it down and only at zero applies the lines. `player_input` zeroes the
  movement and both trigger bits while it is above zero, so a job is a real
  pause in a fight — the mech can still look around. The job dies with the
  mech (and the money with it), and a second order is refused until it ends.
  Durations are `defs.REARM_*` (swap 2 s per arm, ammo 4 s per arm, repair 2 s
  per 100 hp); `shop.quote` charges a swap both a fit and a reload.
* **Grid** (`grid.py`) is a 128 m cell index over every unit that moves *or* can
  be fought (`moves` ∪ `combat`), refiled at the top of each tick (`world.grid`)
  so "who is near me" is a box of cells instead of a scan of the field. It keeps
  its own copy of the positions — the build gathers them once per archetype, so
  a query touches no component view at all. The second tag is what puts the
  command base in the grid: it never moves, but it is a target, and a grid of
  `moves` alone hid it from `ai._acquire`, so no AI hull ever shot the thing its
  lane ends at. `ai._acquire` is the only user: it asks for the box covering the
  shooter's `sight` (exactly the range the old full scan filtered on, so the
  same target comes back) and then distance-sorts the few candidates in it.
  Rebuild before querying anything that has moved since; in the tick's pipeline
  only `player_input` runs ahead of `ai`, and it moves nothing.
* **Raiders** (`modes/battle/raids.py`) are the one unit that plans.  Every
  `RAID_INTERVAL` a side sends `RAID_SIZE` mechs out, **one per lane, the lanes
  drawn at random**, and each of them is briefed separately (a plan belongs to a
  hull, keyed `(faction, party, lane)` on the world - two raiders two minutes
  apart are kilometres apart, so a shared route would send the older one
  backwards).  Both halves of the briefing come off one **force gradient**
  (`_gradient`): the enemy army binned per 128 m node, spread over each hull's
  own `sight` with a triangular falloff and weighted by `FORCE_WEIGHT`, i.e.
  "defenders that could shoot a hull standing here".  `_pick_town` takes the
  lowest-gradient enemy town on the raider's lane; `_astar` costs a node as
  `1 + WOOD_COST·wood + WALL_COST·buildings + GRADIENT_COST·force`, so the route
  bends round the enemy without doubling the march (`GRADIENT_COST` is the one
  tuned-by-measurement constant in the file).  The plan is **two-level**: the
  128 m strategic grid crosses the field and hands over to a 16 m `StreetNav`
  grid 400 m out, because at 128 m a town is a smear of half walls and a route
  drawn on it strands a hull against a house.  A hull that pushes at something
  for `STUCK_SKIP` seconds drops the way point, and after `STUCK_REPLAN` it is
  re-routed on the street grid (`_rescue`) instead of re-planned.  The mission
  is `ai_state`: MARCH, then ASSAULT (stand in the 170 m ring - `waves._capture`
  does the taking), then HOLD for `RAID_HOLD` (a raider in the ring is what
  keeps the enemy from taking the cut back), then the next weak spot.  The
  `raider` tag is what takes them out of `ai._advance`; the guns stay `ai.run`'s.
  Raiders die like any other hull - 58% of them in a measured round - which is
  why the plan dict is pruned when a party is gone.  `tools/raid_trace.py` runs
  a battle and draws the gradient, the planned routes and where the hulls died.
* **Modes** (`modes/`) own everything that differs between game modes: the
  map, what a fresh round spawns, the mode's own systems and the rules the
  shared code asks about (`fixed_faction`, `start_money`, `rearm_anywhere`,
  `restart_respawn`, `mech_spawn`). Adding a mode means adding a folder, never
  another `if` in a shared system. Battle runs `ai` + `waves` (capture and
  reinforcement waves); range runs `targets` and no AI at all.
* **Players** are plain Python objects (`world.Player`) attached to a mech
  entity through `owner`; `pl.mech` is -1 while dead. Income and respawns live
  in `systems/economy.py`; wreck/corpse cleanup in `systems/debris.py`.
* **Proximity fuse** (`systems/proximity.py`) — the 30mm round bursts on
  *personnel*: `defs.WEAPONS[...]["prox"]` is a fuse radius in metres (absent =
  impact fuse only), and `person` is the tag a man carries (riflemen, AT
  riflemen, civilians). Hulls are not tagged, so the fuse ignores them. A round
  covers 36 m per tick, so the fuse solves the leg it *flew* this tick —
  `proj_prev` (written by `projectiles.run`) to `pos` — takes the closest
  approach to an enemy man and, if that is inside the radius, moves the round
  there and calls `projectiles._impact` at that point. Never sample the tick
  boundaries instead: that steps clean over a man standing 4 m off the path.
  Airburst is blast only (it is not a direct hit), and the burst is logged
  (`mech.prox: prox fuse: ...`) so the range test can read the burst position
  straight out of the log.
* **Damage** is two halves, never one number. `defs.pen_damage(ap, ad, dp)`:
  beating the target's `dp` outright delivers the full `ad`, beating only half
  of it shaves `ad/5`, anything duller does nothing. `defs.blast_damage(ed, de,
  er, dist)` falls off linearly to `er` and always keeps `max(e - de, e/5)`. A
  round *landing* on a unit takes `world.hit()` (both halves); a unit merely
  near the burst takes `world.splash()` (blast only - a charge going off a few
  metres away cannot punch a plate). `world.damage()` stays the raw hp
  subtractor and is what debug knobs use.
* **A wreck goes up.** `world.kill()` of an APC or a mech fires
  `defs.DEATH_BLAST` (ER 30 m, ED 10) through `World._death_blast` *after* the
  unit is destroyed and its hulk placed, so the dead machine is not in it. It is
  the one blast in the game that takes no sides - crews of the dead hull's own
  faction are cooked too - but ED 10 over 30 m is a wide, thin shock: a scorch
  on armour (2 points at contact), a wound on men (9, never a kill), not a
  second warhead. It is solved
  (nearest hit sphere per unit) before any damage lands, so a chain reaction
  cannot hit the wreck the next hull has already become. The kill's credit goes
  to whoever killed the first hull.
* **One hull pool.** A mech's chassis and torso share `hp[MOD_CHASSIS]`; a hit
  on either body sphere, and a hit on an arm that is already gone, is charged
  there. `MOD_TORSO`'s health slot is unused (0) - it only names the upper hit
  sphere. Arms keep their own 50.
* **Debris** — `world.kill()` leaves an inert entity behind: a corpse
  (`ETYPE_CORPSE`, 5 s) for infantry, a smoking hulk (`ETYPE_DEBRIS`, 10 s,
  original model in snapshot `flags` bits 4-5) for vehicles and mechs. They are
  spawned without the `unit` tag, so `world.units()` (rounds, damage, AI,
  capture) never sees them; only `world.all_units()` (the snapshot) does.
* **Smoke** (`systems/smoke.py`) — a pull throws a lattice of `canister`
  entities that closes the whole front hemisphere (14 of them, 50 m out); each
  one trades its tag for `smoke` + `smoke_r` + `smoke_t`, a 45 m sphere that
  bursts open fast and then creeps (`_radii(age)`) for its 5 s life. The puffs
  are drawn **opaque** (solid pass, `renderer.smoke`), so a screen really hides
  what is behind it and costs nothing to blend. It has no
  `unit`/`faction`/`etype`, so nothing can target or hit it. What it does do is
  **hide**, and it hides through the same door a tree canopy does: `systems/
  sight.py` asks `smoke.blocks(world, a, b)` for it, so the IR lock, the AI's
  lookout and a missile's seeker all lose the hull behind a screen - exactly as
  they lose one behind a stand of trees. It travels in its own snapshot block.
* **Lanes** — `world.lanes` counts from the red base, `world.lanes_rev` from
  the blue base. Every per-faction lookup (spawn point, path index, "front
  line") must use the faction's own copy; mixing them mirrors positions across
  the map. `path_i` is always an index into the faction's own list, and for a
  vehicle it is **one past the end once the lane is finished** (the sticky
  mark that sends the hull at the command base instead).  The lane is a plain
  polyline: **the map does not round its corners**, because a wheeled hull
  cannot follow a right angle whatever the map does with it. That is the AI's
  problem and `ai._passed` is the answer — see the wheeled-hull note below.
* **Wheeled hulls** — an APC carries the `vehicle` tag, and that tag changes
  both halves of its movement. Locomotion (`movement._vehicle`) is a rear-axle
  bicycle model: `leg_yaw` *is* the hull state (mirrored into `yaw` for the
  client), `drive[0]` the signed speed rolling along it, `veh[]` the back-out
  and blocked timers. The hull rolls only along its own axis and turns at
  `speed / APC_TURN_R`, but never harder than `APC_LAT_ACCEL` of side load
  (`yaw rate = min(u / R, lat / u)`) — that cap is what keeps it from swinging
  lock-to-lock at speed, and it matches the speed the planner takes a bend at,
  so the two agree by construction. It cannot strafe or pivot on the spot, and
  it **never drives fast in reverse**: `APC_REVERSE` of top speed is only used
  to back off something the nose is against (after `APC_BLOCKED` seconds). A
  goal behind the hull is taken by turning round for it — the brake grows with
  the heading error, so a U-turn comes down to the hull's radius instead of
  being swung at top speed or (worse) reversed into. A few degrees of heading
  error cost no speed at all: only a real bend or a half turn slows the car.
  Throttle is thrust against drag, not a fixed ramp (`APC_THRUST` m/s^2 at a
  standstill, balance point `APC_V_INF`), which is what gives a heavy hull a
  brisk low end and a lazy top end: 0-25 km/h in 3.5 s, 0-100 in 20 s. Reverse
  is `APC_REVERSE` of top speed and only ever used to back off a wall.
  The command interface is unchanged — the AI still writes a direction and a
  speed into `vel` — but the AI *drives the way points* (`ai._advance_route`):
  a way point inside the acceptance radius counts as reached and is dropped, so
  the hull is already aiming at the next one while still short of the last — the
  standard acceptance radius of a way point tracker, and what lets a car take a
  dense line of sharp corners at speed by cutting across the inside of them (it
  will happily leave the road doing it; the road is not the constraint). The
  radius **grows with the speed** (`ai._arrive_radius`: `clamp(speed * 1.1 s,
  10 m, 30 m)`) — a hull at 100 km/h needs tens of metres of arc to turn at all,
  while a slow one can and should hold the line tightly. A point it
  cut *wide* of is dropped too, once the hull is past the line through it
  square to the incoming leg (`ai._passed`) — otherwise a car that missed the
  circle turns back for it for ever. Speed comes from the corners ahead within
  `APC_CORNER_HORIZON`. A corner's speed comes from the **cut budget**
  (`APC_CUT`, 8 m): a fillet of radius R misses the vertex by
  `R (1/cos(angle/2) - 1)`, so the budget caps R, the hull floors it, and the
  bend is driven at `sqrt(lat_accel * R)` — a right angle lands at ~11 m/s
  cutting 8 m, a 20 degree kink costs nothing. A vertex whose *both* legs are
  shorter than the fast acceptance radius imposes no limit at all: at speed the
  hull would swallow such a point whole, and braking for it would turn a dense
  line into a crawl. The scan starts at the way point *before* the one being
  aimed at, because the acceptance radius drops a corner while the hull is still
  ~12 m short of it.
  The gunner keeps the turret; the hull points where it drives, so `yaw` is not
  the AI's to turn for a vehicle. `tools/vehicle_trace.py` drives one round the
  range and draws the trace if you want to see the result.
* **Terrain** (`terrain.py`) — 1000x1000 tiles of 16 m, heights on 4 m steps.
  Three extra bitmaps travel with the map: `roads` (direction bits `ROAD_N/E/
  S/W` for a straight, half-tile strip plus `ROAD_D_WS`/`ROAD_D_NE` for the two
  45° half-tile pieces the diagonal lanes alternate between) and `walls`
  (building height per tile, 2-6 storeys of `FLOOR_H` = 8-24 m). A road leg is
  laid on **tile centres** (half-tile coordinates), the corridor around it is
  flattened to a terrace (`ROAD_WEIGHT` makes the asphalt beat the verges), and
  a road tile's ground keeps the grass colour - the asphalt is a separate client
  pass, the OpenTTD way. `ground_hit` treats buildings as upright columns
  (`obstacle_at` = ground + `wall_at`) and walks the ray **tile by tile**
  (`_spans`, a 2D DDA), so a 30 mm round that covers 36 m in one tick cannot
  skip a facade; inside a tile it samples every 8 m and then bisects 6 times,
  which lands the burst on the outer face.  The client's `predictImpact`
  samples its 1/30 step every 8 m the same way - testing only the end point let
  fast rounds predict a hit behind the building.
* **Trees are trunks for physics, canopies for sight.** A tree/forest tile
  holds 1-5 trees whose trunks are the only solid part of it: `_trunks(i, j)`
  (server) / `treeTrunks` in `map.js` lay them out from one hash both sides
  replicate exactly, and each is a **cylinder** - `r` = 0.35-0.6 m, `h` =
  0.4 x the tree's 10-18 m height, the same cylinder the renderer draws.
  A trunk is a metre wide, far thinner than the 8 m the ray walk samples at, so
  it is **solved, not sampled**: `_trunk_hit` (segment vs circle in plan view,
  then the height at the entry point) inside `_march`, mirrored by
  `MapData.trunkHit`. Hulls test the same cylinders (`trunk_blocked`, from
  `movement`) and drive under the canopy. `wall_at`/`blocked` return the trunk
  only. **The leaves are a volume, not a tile**: `_canopy`/`canopyOf` build one
  body per tree out of the same draws the renderer uses - a broadleaf
  (`q3 > 0.45`) is an ellipsoid ball on the trunk, a conifer a **cone** standing
  on it - and `_canopy_hit`/`canopyHit` solve the sight segment against each of
  them (`_canopies`/`canopiesAt` cache, per tile, every canopy that reaches into
  it, neighbours included: a ball is up to 8 m across and hangs over the next
  tile). Two shapes, two solvers, and the client must keep them the same or the
  box and the lock disagree. What a tile once did - a 10 m slab over the whole
  16 m cell - hid hulls standing in the open beside a stand, so it is gone.
  Never make a tree tile block a round or a hull, and never give a canopy a
  bullet collision: the plan is trunk-solid, leaves-see-through.
* **There is one line of sight, and it is `systems/sight.py`.** `sight.clear` /
  `sight.why` answer "does anything cut a->b" out of the two halves that hide
  things: the static field (`terrain.los` - ground, buildings up to the roof,
  tree canopies) and the smoke (`smoke.blocks` - deployed puffs, `why` returns
  "smoke"/"terrain" for the seeker's log). A tree hides what stands behind it
  for good, a puff hides it for five seconds and is gone: nothing else differs
  between them, so nothing pairs the two checks up on its own. Every eye goes
  through it - the AI's `_sees`, the player's IR lock (`locking.py`, which used
  to ask the field only and so let a hull be locked straight through a screen
  of smoke) and the missile seeker. `map.js` mirrors the same rule for the HUD
  (`MapData.sightAt` + `sightBlocked`, canopy and puffs); the pre-aim box is
  drawn only through a clear line of sight, exactly what the seeker can lock.
  The ballistic prediction (`sightPoint`, `groundHit`) stays on the *round's*
  rule instead - leaves and smoke never stop a bullet.
* **Towns are grown, not drawn.** Each one starts on its lane (`_on_lane` snaps
  the spot onto the nearest lane point, slides it ±3 tiles along the road and
  returns the lane direction) and then `_town_streets` grows the network
  OpenTTD-style: a front runs 2-6 tiles of street, then carries on, turns, dies
  and drops a branch, or joins the street it ran into - which is what closes a
  block. Fronts start in the middle and on both sides of the lane, and reopen on
  any street with room to grow. Two passes then make the network drivable
  instead of a maze: `_town_rungs` punches a cross street every `TOWN_RUNG` (4)
  tiles of a long street - only where a street really is on the other side, so
  a block is opened up and never a cul-de-sac added - and `_town_close` runs
  every remaining loose end on to the nearest other street (a shortest-path
  search over free ground, houses are walls) or lets it fade out into the
  fields. Straight stretches between junctions average 2 tiles and dead ends are
  ~0.1% of the street tiles; keep it that way, a long street you cannot get off
  or a street that stops at a wall is what makes a town feel like a trap.
  `_town_houses` then puts the buildings up in rows of 2-5 along one frontage,
  `TOWN_HOUSES` (120-200) per town, one storey count per building and taller
  towards the middle - the rows plus the gaps are what make a town read as a
  town rather than a wall of blocks.
  **The town only ever ORs road pieces into the bit map - it never removes
  one - and `no_build` keeps buildings 1 tile off a straight lane and 2 off a
  diagonal one**, so the arterial stays straight and drivable however the town
  grows around it. `TOWN_PLAZA` (= 5 tiles, 80 m) is the open square in the
  middle: no buildings and no trees there, because the 40-70 m forward spawn
  ring and the civilians live inside it. Streets are laid *before* the corridor
  cut so they sit on terraces like the lanes do; they are part of `roads`, so
  the client draws them from the same bits.
* **Netcode** — one init payload with the map and all definitions, then binary
  snapshots at 15 Hz (`SNAP_EVERY`) and JSON meta at 10 Hz (`META_EVERY`).
  Entity state is authoritative on the server; the client only interpolates.
* **Debug messages** are accepted only when `MECH_DEBUG=1` (see `main.py`),
  except `respawn`, which is always allowed. All of them are sent from the
  console as `g.net.send({t: '...', ...})`; the payloads are in
  [Debug knobs](#debug-knobs) below.

### Debug knobs

Everything here is one websocket message from the browser console
(`const g = window.__game`). The three that have a button live in the **gear
menu** (top right of the HUD, needs a free pointer: press `ESC`):
`RESPAWN`, `RESTART`, `GOD ON/OFF`.

| Message | Payload | Effect |
| --- | --- | --- |
| `respawn` | — | skips the 3 s death timer on the next tick (always allowed) |
| `debug_restart` | — | wipes the field and starts a fresh round, same map (`world.reset_match()`); info `MATCH RESTARTED` |
| `debug_god` | `on: true/false` | stops the AI / damage from interrupting a test; meta `god` mirrors it (the menu button reads that back) |
| `debug_spawn` | `etype` (default `3` APC), `dist` (m, default `800`), `angle` (rad, relative to your yaw), `faction` (default the enemy), `weapons` (optional list) | spawns a unit `dist` metres ahead |
| `debug_trees` | `dist` (m, default `200`), `angle` (rad, relative to your yaw), `radius` (tiles either side, default `2`), `forest` (default `true`) | grows a stand of trees `dist` metres ahead (`Terrain.plant`): the server repaints the tiles, rebuilds the cull grid and hands the same rectangle to the clients, which repaint their ground and props. Buildings, rocks and bases are left alone |
| `debug_tp` | `dist` (m, default `300`), `angle` (rad, relative to your yaw) | teleports the mech forward along its yaw |
| `debug_hurt` | `module` (`0` hull / `1` torso, which charges the hull pool / `2` left arm / `3` right arm, default `1`), `amount` (default `100` = a whole hull), `base` (damage to the *enemy* command base) | wounds the player or the enemy base |

Entity types for `debug_spawn` come from `defs.py`: `1` rifleman, `2` AT
rifleman, `3` APC, `4` mech, `5` command base, `6` civilian, `9` bunker.

```js
g.net.send({ t: 'debug_spawn', etype: 3, dist: 400, angle: 0.3 });  // APC 400 m ahead
g.net.send({ t: 'debug_hurt', module: 2, amount: 999 });            // blow off the left arm
g.net.send({ t: 'debug_tp', dist: 1500 });                         // hop 1.5 km forward
g.net.send({ t: 'debug_trees', dist: 150, radius: 2 });            // a tree stand in between
g.net.send({ t: 'debug_god', on: true });                          // then confirm g.net.meta.god
```

### Client

* `game.js` sends input at 30 Hz (`{t:"in",yaw,pitch,mx,mz,fl,fr}`) and drives
  the frame: interpolate snapshots, aim, build the draw list, draw, HUD.
* `renderer.js` draws the ground twice: a radial LOD grid out to the horizon
  (far) and a **tile-aligned patch** (`TILE_PATCH` tiles around the eye, moved
  by the `uOrigin` uniform) that carries the OpenTTD look - every vertex is a
  tile corner, so the per-triangle flat normal from the fragment derivatives
  shades each tile/ramp as one facet. The far grid `discard`s the patch area
  (`uNear`), the fragment shader draws the tile seam in screen space and fades
  it out once a tile is only a few pixels wide. Heights come from an `R32F`
  texture, tiles from an `R8UI` one. The far grid keeps the plain tile spacing
  for `uA0` rings (one quad per 16 m cell, nothing merged) and only doubles its
  ring spacing every `uG` rings past that (`TERRAIN_LOD`): the flat radius
  follows the optics - 768 m at 1x, **3 km at 20x** - and the cover shrinks with
  it, 17.6 km to 5 km, which the fog hides. Every vertex is **snapped to the
  world grid of its own ring spacing** (`floor(uCam/ spacing) * spacing` in
  `TERRAIN_VS`): a grid that slides continuously with the eye re-samples the
  heightfield every frame and makes the distant hills crawl. Roads are a
  **separate pass built from the `map.roads` bitmask** (`_buildRoads`): every
  piece is a polygon inside one
  tile, so it has to be cut along that tile's terrain diagonal (`clipHalf`) to
  stay on one facet - an uncut quad that spans both triangles of a sloped tile
  sinks into the ground and reads as z-fighting. A straight road is two 10 m
  half-tile strips (`LIFT` = 0.06 above the ground), the diagonal lanes are the
  two 45° pieces the server put in `roads`, joined into one continuous band.
  Units/props/projectiles are instanced primitives submitted through
  `WEBGL_multi_draw` with a per-command fallback. Prop instances are batched
  per mesh (`this.meshes.length` batches) and culled in 640 m chunks
  (`CHUNK_TILES` = 40) by `_buildProps`, which plants the trees of
  `map.js`'s `treeTrunks` (the same trunks the server collides against) and,
  per building tile, a `MESH_FACADE` box as tall as `map.walls`
  (storeys and windows are painted in the shader off `vLocal`/`vScale`,
  `uFacade`), a roof slab with a parapet, an occasional plant box or annex.
* `scene.js` builds unit models from primitives in a local frame: **+X
  forward, +Y up, +Z right** (see `math.js compose`). A Y-axis primitive is
  laid along the barrel with `pitch = FORWARD` (`-PI/2`); use
  `FORWARD + p` when a pitch is already in play.
* `map.js` must stay ballistically identical to the server: same gravity,
  same 1/30 step, same launch bias (`wd.bias`), the same "building walls are
  columns, tree trunks are cylinders" rule (`wallAt`/`obstacleAt`/`trunkHit`),
  otherwise the predicted impact point drifts off the real one. It also carries
  the **sight** rule (`sightWall`/`sightAt`, `canopiesAt`/`canopyOf`,
  `sightBlocked`), because the HUD draws a pre-aim box only on a hull the seeker
  could lock; the impact marker and the centre cross keep using the ballistic
  rule instead (`sightPoint`/`groundHit`) - leaves and smoke never stop a
  bullet. Both solvers are mirrored line for line, and the two shapes of canopy
  (ball, cone) have their own.
* **Sound is not immediate.** Gunfire and explosions go through
  `game._sound(x, y, z, level, play)` -> `game.sounds`, a list of acoustic events
  in the air (emit position, time, source level, 50 s TTL - a 17 km
  blast takes that long to arrive). `_pumpSounds()` runs
  every frame and compares the wavefront radius (`SOUND_SPEED` = 343 m/s x age)
  with the distance to the mech's **current** position: the event plays (at
  `level * attenuation(dist)`) and leaves the list when the front catches up.
  Using the live position is what makes a 180 km/h hull hear a bang where it
  actually caught up with it - never cache the distance at emit time. Flashes,
  impacts, screen shake and the electronic cues (lock tone, RWR alarms,
  kill/denied chimes) stay immediate.
* **Distance is three effects, not one curve.** `audio.attenuation` is inverse
  square *compressed* (`SOUND_ROLLOFF` 0.6, reference `SOUND_REF_DIST` 25 m):
  true 1/r over 16 km is 84 dB of dynamic range, i.e. silence, so the shape of
  the near field is kept (a gun at 100 m is 7 dB below one at 20 m) and the far
  field stays audible (-32 dB at 12 km). `audio.airCutoff` is the second: air
  absorbs the top end far faster than the bottom (0.4 dB/km at 125 Hz, 30 dB/km
  at 4 kHz), 18 kHz inside 300 m down to 600 Hz past 8 km - a distant cannon is
  a dull thud, and *that*, not the level, is what the ear reads as distance. It
  shares one filter with the "behind you" lowpass in `audio._bus(g, pan, behind,
  dist)`. Third is the direct/reverberant split: the wet send is lifted by
  `reverbLift(dist)` so the diffuse field decays slower than the direct sound
  (wet is ~13 dB above dry at 8 km) - far away is "echo", not "turned down".
  `SOUND_LEVEL` (shot .6 / boom .7 / big .8 / blast 1) is how loud a cue is at
  the reference distance.
* **The server decides what a pilot's ear gets.** `defs.SOUND_*` mirror the
  client's curve (`attenuation`, `SOUND_LEVEL`, same numbers), and
  `netcode.hearable(events, ear)` drops every `shot`/`boom` whose arriving gain
  is under `SOUND_FLOOR` (2 %: a cannon reaches ~7.2 km, a hull's death blast
  17 km). `main.broadcast` filters per connection against that pilot's mech
  position - a dead pilot has no ear and gets no sound events at all, while
  messages (kill, capture, rearm, tiles, gameover) always pass. Otherwise every
  bang on a 16 km map would be broadcast to everybody: measured, 861 events in
  15 s at the median 6.4 km, none of them worth the packet.
* **A bang has a side, and a back.** `game._bearing(s, own, dist)` turns an
  event's bearing into `pan` (its lateral share, `-1` left .. `+1` right) and
  `behind` (how much of it is behind the view, 0..1), taken at the moment the
  wavefront arrives - the live position *and* `input.viewYaw`, so turning the
  head re-places the next bang, never the one already sounding. `audio._bus`
  spends them: a `StereoPanner` in front of the master bus, and for `behind` a
  lowpass (12 kHz down to 2 kHz) - two ears cannot place a sound along the
  nose-to-tail axis, so the dulling is the whole of "behind you". Inside
  `EAR_NEAR` (1 m) the pan is faded to the centre, because a bang on the canopy
  has no side. The reverb send leaves ahead of `_bus`, so the room stays wide
  while only the direct sound is placed. The machine's own voices (RWR warble,
  lock tone, `hit`/`denied`) stay centred - they are not in the world.
* **Every cue has its own voice.** `audio.boom()` is a round going off;
  `audio.explosion()` is a hull going up (`ev.blast`) - four layers (crack, fire,
  sub-bass thump, rumble) written because a dead mech is a different event, not a
  louder round. The mix can sum past 0 dB on purpose, so the master bus runs
  through a limiter (`audio.limiter`) instead of clamping at the destination:
  scale a cue's gains up rather than adding another layer elsewhere.
* The **gear** in the top-right corner is the only HUD click target: it needs a
  free pointer (`ESC`) and opens the settings panel (`hud._settings`), the
  master volume plus the debug buttons. `input.onHudClick` fires for canvas
  clicks while the pointer is free and returns `true` when the HUD took the
  click; everything else keeps its old meaning (a click re-grabs the pointer).
* **Icons** are Material Symbols glyphs drawn from a font, not hand-built
  canvas shapes: `frontend/icons.json` is the single source of truth,
  `src/icons.js` derives the glyph strings from it and loads the vendored
  subset (`FontFace`), and `scripts/fetch-material-icons.sh` regenerates
  `frontend/vendor/material-symbols/material-symbols-outlined.woff2` (a few KB,
  committed). Adding an icon = an entry in `icons.json` + re-running that
  script; `drawIcon()` returns `false` while the face is loading, so callers
  keep a primitive fallback for the first frames.
* `window.__game` (`game.js` instance) is exposed for automation: `g.input`,
  `g.net.send(...)`, `g.net.meta`, `g.net.entities`, `g.renderer`,
  `g.settingsOpen`, `g.audio.setVolume(0..1)`.

## Build

```bash
cd frontend && npm install && npm run build      # -> backend/assets/
./dev.sh build                                    # same, with a shared npm cache
```

* Webpack production build, output `backend/assets/{bundle.js,index.html}`,
  gitignored. The server serves them at `/` and `/static/*`.
* Frontend changes need **only** a rebuild — the server reads the files per
  request, no restart.
* Backend changes need a **server restart** (`./dev.sh stop && ./dev.sh start`,
  ~8 s). `netcode.STATIC` (map + definitions) is built once at import time.
* Python: `>=3.10`, venv at `.venv`
  (`uv venv && uv pip install -r backend/pyproject.toml`).
* No test suite, no linter, no type checker. Verify by running the game and
  watching `/tmp/mech-server.log` plus the browser console.
* **`tools/replay.py` is the one automated check there is.** It drives a
  scripted 30 s battle (900 ticks: both weapons, smoke pulls, debug spawns,
  forced kills, AI waves), records per-tick aggregates — entity counts, total
  hp, the sum of every position and velocity, money / kills / deaths — and
  compares them with `tools/fixtures/replay_battle.json`. The first raiding
  party is brought forward into the window, so a round of `raids.py` (plan,
  march, fight, die) is covered as well, and a separate assertion checks that a
  raider's route starts under it, ends at the town it was sent to, stays on its
  lane, and touches no building tile. It is tolerant by
  design: float sums may drift 1 %, counts may be off by one. Run it after
  anything that touches `ecs.py`, `systems/` or `terrain.py`; `--save` rewrites
  the fixture when a change is meant to move the numbers.
  ```bash
  .venv/bin/python tools/replay.py          # compare
  .venv/bin/python tools/replay.py --save   # re-baseline
  ```
* **`tools/raid_trace.py`** runs a whole battle headless and draws the raiders:
  the **force gradient** of both armies, every raider's planned route (dashed)
  against the trace it actually drove (solid), the capture rings it was sent to
  hold, the towns changing hands and an × where a hull died.  Per raider it
  prints distance, time, woods crossed, mean enemy exposure against what the
  straight line would have been, deviation off its own route, time in a fight,
  time holding, replans/rescues and when it was destroyed - the four things a
  raid is judged on (target, movement, fighting, losses).  It also writes the
  per-sample trace as CSV.  Pass `--bbox X0,Z0,X1,Z1` to plot one corner of the
  map at street scale.
  ```bash
  .venv/bin/python tools/raid_trace.py                       # -> /tmp/raid_trace.{svg,csv}
  .venv/bin/python tools/raid_trace.py --minutes 20 --seed 7 --out /tmp/r7
  .venv/bin/python tools/raid_trace.py --bbox 9600,10000,14000,14200
  # the SVG is hand-written: render it with headless chrome if you want a PNG
  google-chrome --headless --screenshot=/tmp/r.png --window-size=1180,1320 \
      --user-data-dir=/tmp/chrome-prof file:///tmp/raid_trace.svg
  ```
* **`tools/vehicle_trace.py`** drives one APC round a route on the range map
  (a 100 m square or the 3.7 km winding road) and writes an SVG - way points,
  the rounded reference route, the driven trace coloured by speed, a velocity
  vector every few dozen metres - plus a per-tick CSV. It steps `ai._advance_route`
  by hand because the range runs no AI. Useful whenever the vehicle model or the
  route follower changes: it prints the lap time, the mean speed, the worst
  deviation off the route and the speed through the corners.
  ```bash
  .venv/bin/python tools/vehicle_trace.py                    # -> /tmp/apc_trace.{svg,csv}
  .venv/bin/python tools/vehicle_trace.py --route winding --out /tmp/w
  ```

## Run

`dev.sh` is the normal entry point. It starts the server inside a detached
byobu session (falls back to plain `tmux` when `~/.byobu` is not writable):

```bash
./dev.sh dev        # build the client, then start the server (first run)
./dev.sh start      # start the server in session "mech"
./dev.sh attach     # attach to it (detach: F6 for byobu, Ctrl-a d for tmux)
./dev.sh stop       # kill the session (battle and range)
./dev.sh status     # list sessions
./dev.sh range      # weapon-range server in session "mech-range" (see below)
tail -f /tmp/mech-server.log
```

Then open <http://127.0.0.1:8010/>; the lobby picks callsign, side and the
weapon in each arm before `DEPLOY`.

`./dev.sh range` starts a second world (`run.py --range`, i.e. `MECH_RANGE=1`)
on <http://127.0.0.1:8011>: a 4 km test map, one 200 m hill NW, plains
elsewhere, 12 blue APC targets (2 patrolling) that respawn 3 s after dying.
**Nothing on the range shoots back** - there is no AI and no incoming fire, so
it is the place to test ballistics or look at a model up close. The player is
locked to RED with $99999, can rearm anywhere and the smoke cooldown is 2 s.
Seeker lock breaks are logged by `mech.smoke` (you have to be shot at for
that - the battle mode AI does it); the browser script `tools/smoke_lock_test.js`
drives a whole A/B smoke run from the console when such a missile is in the air.
Log: `/tmp/mech-range.log`, overrides `MECH_RANGE_PORT` (8011), `MECH_RANGE_LOG`.
Product notes: `doc/product/range.md`.

Environment overrides: `MECH_SESSION` (default `mech`), `MECH_PORT` (8010),
`MECH_LOG` (`/tmp/mech-server.log`). `dev.sh` always sets `MECH_DEBUG=1`,
which unlocks the debug websocket messages and the DEBUG block of the settings
menu (gear, top right: RESPAWN / RESTART / GOD). Without it the menu keeps the
volume slider only.

Equivalent raw tmux invocation:

```bash
tmux new-session -A -d -s mech \
  "cd $PWD/backend && MECH_DEBUG=1 .venv/bin/python run.py --port 8010 2>&1 | tee /tmp/mech-server.log"
```

Manual run without tmux:

```bash
cd backend && MECH_DEBUG=1 ../.venv/bin/python run.py --port 8010
```

### Inside a sandboxed agent shell

Two things bite here, both about `/tmp`:

* `/tmp` is a per-session scratch directory that does not exist yet, so tmux
  cannot create its socket (`error creating /tmp/tmux-1000/default`). Create it
  once and `dev.sh` works as documented:

  ```bash
  mkdir -p /tmp/tmux-$(id -u) && chmod 700 /tmp/tmux-$(id -u)
  ./dev.sh start
  ```

* **Never start the server with `nohup ... &`**, `start-stop-daemon` or any
  other background job: the tool call hangs on the open pipe and the process
  dies with the sandbox that spawned it. Start it in the tmux session
  (`./dev.sh start`, or `tmux new-session -d -s mech ...`), then read the world
  through `./dev.sh status`, `curl http://127.0.0.1:8010/` and
  `tail /tmp/mech-server.log` - the session outlives the individual shell call.

## Checking the game in a browser

The client is playable but also drivable from the console, which is how changes
are verified here (no test suite):

```js
const g = window.__game;
g.net.meta              // money, hp, wave, respawn, god, winner
g.net.entities          // Map<id, entity> (x, y, z, yaw, type, faction, hp)
g.net.send({ t: 'debug_spawn', etype: 2, dist: 40 });   // needs MECH_DEBUG=1
g.net.send({ t: 'debug_god', on: true });
g.input.yaw = 1.2; g.input.pitch = -0.1;                // aim without a mouse
```

Pointer lock is rejected in headless automation — that is expected and
harmless; set `g.input.*` directly instead of synthesising mouse moves.
Screenshots and console evaluation go through the browser bridge
(`POST http://127.0.0.1:10086/command` with an `evaluate` or `screenshot`
action); write request bodies to a file under `/tmp` and pass `-d @file`
when the code contains quotes.

### Looking at the world: a free camera

The frame camera is built by `game._camera(own, dt)` from the mech's position,
the yaw / pitch and the zoom. Replacing it from the console and passing a fake
`own` puts the eye anywhere, which is how terrain, roads and props are
inspected (the mech itself never has to move):

```js
const g = window.__game;
g.__origCam = g.__origCam || g._camera.bind(g);
g.__cam = (eye, yaw, pitch) => { g._camera = (own, dt) => {
  // `input.viewYaw` is a GETTER (yaw + freeYaw), its assignment is silently
  // dropped - the real knobs are `yaw` and `freeYaw`
  g.input.yaw = yaw; g.input.freeYaw = 0;
  g.input.pitch = pitch;                     // -1.45 = straight down
  return g.__origCam({ x: eye[0], y: eye[1] - 5.05, z: eye[2] }, dt);
}; };
g.__cam([x, y, z], Math.PI / 4, -0.45);      // yaw: dirFrom convention
```

* `_camera` is a cockpit camera whose eye is the commander's sight head: it
  sits at `defs.mech.eye` (5.05 m) above `own.y`, on the hull's own centre line
  and with no forward step, hence the `- 5.05` above. `_sightEye` returns that
  same point - it is also the origin the server fires and locks from - so the
  cross, the range readout and the lead circles cannot disagree. The pilot's own
  machine is drawn like any other unit, in full: there is **no separate
  first-person view model**, so the barrels on screen are the barrels the
  rounds leave from. The one piece left out of it is the sight head the eye is
  inside of (`scene.mech`, the `own` flag). That head is drawn with its optical
  axis on `MECH_EYE` (`SIGHT_Y` in `scene.js` - the two numbers move together),
  which is what keeps the eye *below* every other hull's sight roof instead of
  looking down on it.
* `input.pitch` is a plain field (settable), `input.viewYaw` is not: writing
  `g.input.viewYaw = ...` does nothing and the eye keeps looking along
  `input.yaw`, which is the *last thing sent to the server* - a still mech can
  sit there for minutes with a stale yaw. `debug_tp` sets it, `g.input.yaw` is
  what the mech turns to.
* The override survives until the page is reloaded — a `navigate` clears it and
  drops you back in the lobby, so re-click `DEPLOY` (or re-click the element
  whose `textContent` is `DEPLOY`).
* `g.renderer.roadCount = 0` (save the old value) hides the road pass;
  replacing `g.renderer.draw` with a wrapper that calls `g.renderer.beginDynamic()`
  first hides every dynamic body — including the pilot's own mech — which is
  what a clean scenery shot needs:

```js
const r = g.renderer;
r.__d = r.__d || r.draw.bind(r);
r.draw = (cam) => { r.beginDynamic(); r.__d(cam); };
```

* `g.map.heightAt(x, z)`, `g.map.walls`, `g.map.roads`, `g.renderer.propStats`
  and `g.renderer.roadCount` are all readable, so a picture can be checked
  against the data it came from.

### Moving the mech without the keyboard

`debug_tp` moves the mech along the **server's** copy of the input yaw
(`pl.in_yaw`), not the one the message carries, so a teleport has to be sent a
few ticks after `g.input.yaw` was set and the result read back from the
*server* position:

```js
const M = g.net.meta, me = () => g.net.entities.get(M.mech);
for (let k = 0; k < 6; k++) {
  const p = me();                                  // p.px / p.pz = server pos
  const dx = TX - p.px, dz = TZ - p.pz, d = Math.hypot(dx, dz);
  if (d < 20) break;
  g.input.yaw = Math.atan2(dz, dx);
  await sleep(300);                                // ticks so in_yaw is current
  g.net.send({ t: 'debug_tp', dist: Math.min(d, 1200) });
  await sleep(1100);                               // next snapshot back
}
```

* Read `px` / `pz` (authoritative) — `x` / `z` are interpolated and freeze
  after a big jump, which reads like a teleport that did nothing.
* One `debug_tp` per ~second: the client only sees the move in the next
  snapshot pair.
* `debug_hurt` with `module: 0, amount: 99999` kills the mech (god mode must be
  off first) → the camera falls back to the base orbit and the own hull is no
  longer drawn; `{ t: 'respawn' }` brings it back.
* An async `evaluate` with `await sleep(...)` is the way to sequence these
  steps; keep each call under the shell timeout (raise it for the bash call, not
  with `-m` alone) and take the screenshot in a separate request.

## Conventions

* Game text, comments, commit messages and docs are in English — except
  `doc/product/`, which is written in Chinese (UI strings quoted verbatim).
* Minimal, scoped edits: match the surrounding style (numpy vectorised
  systems, short helper functions, comment density of the file you touch).
* Units are metres and seconds; the map is 16 km square, the clock is 30 Hz.
* Tuning values (damage, rpm, range, speeds, wave interval, capture time) live
  in `defs.py` and at the top of the mode systems (`modes/battle/waves.py`,
  `modes/range/targets.py`) — prefer changing them there over editing the code
  paths.
* A weapon is a real projectile: keep `kind` semantics
  (`bullet`/`rocket`/`saclos`/`top`) intact when adding or retuning one.
