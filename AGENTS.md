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
    ecs.py                 # numpy component registry: masks, queries, free list
    components.py          # every component the game declares
    defs.py                # entity types, weapons, unit stats, mech geometry
    world.py               # World: terrain, players, spawning, damage, tick
    terrain.py             # 1000x1000 tiles of 16 m -> 16 km square map
    mathutil.py            # angles, lead solving, terrain helpers
    ballistics.py          # fire control: sight setting elevation (player) / sight-point solve (AI)
    netcode.py             # init payload, binary snapshots, JSON meta
    main.py                # FastAPI: websockets, static files, 30 Hz game loop
    systems/               # mechanics shared by every mode, each exposes run(world, dt)
                         #   smoke.py: hot smoke canisters, opaque puffs, blocks(a, b)
                         #   proximity.py: 30mm airburst fuse, bursts on people only
    modes/                 # per-mode rule sets: map, setup, pipeline, spawn rules
      base.py              #   Mode interface every mode implements
      battle/              #   waves.py: town capture + reinforcement waves
      range/               #   targets.py: passive APC targets, bunkers.py: ridge blockhouses
                           #   (nothing on the range shoots back)
  assets/                  # compiled client (webpack output, gitignored)
tools/
  smoke_lock_test.js       # browser-console driver: seeker A/B smoke test
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

* **ECS** — every component is one numpy array indexed by entity slot.
  Presence lives in a `uint64` mask per entity; queries return index arrays
  (`ecs.query("unit")`). Never loop over 8192 slots per system; use the query
  results. Entities are created/destroyed through `ECS.create/destroy`.
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
  pipeline. Shared order: `player_input`, the mode's pre-systems (battle: `ai`),
  `locking, smoke, weapons, movement, projectiles, proximity, economy, debris`,
  then the mode's post-systems (battle: `waves`). `World.update(dt)` is the only
  entry point; the loop in `main.py` calls it at a fixed 30 Hz (`TICK_DT = 1/30`).
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
  `unit`/`faction`/`etype`, so nothing can target or hit it; only the missile
  seekers and the AI look for it, through `smoke.blocks(world, a, b)`. It travels
  in its own snapshot block.
* **Lanes** — `world.lanes` counts from the red base, `world.lanes_rev` from
  the blue base. Every per-faction lookup (spawn point, path index, "front
  line") must use the faction's own copy; mixing them mirrors positions across
  the map. `path_i` is always an index into the faction's own list.
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
  only; `sight_wall`/`sight_at` still stop the line of sight at the whole tile
  at `TREE_H` = 10 m, which is what `los()` - lock, seeker, AI - uses. Never
  make a tree tile block a round or a hull again (`TREE_H` as an obstacle), and
  never give the canopy a bullet collision: the plan is trunk-solid,
  leaves-see-through.
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
| `debug_tp` | `dist` (m, default `300`), `angle` (rad, relative to your yaw) | teleports the mech forward along its yaw |
| `debug_hurt` | `module` (`0` hull / `1` torso, which charges the hull pool / `2` left arm / `3` right arm, default `1`), `amount` (default `100` = a whole hull), `base` (damage to the *enemy* command base) | wounds the player or the enemy base |

Entity types for `debug_spawn` come from `defs.py`: `1` rifleman, `2` AT
rifleman, `3` APC, `4` mech, `5` command base, `6` civilian, `9` bunker.

```js
g.net.send({ t: 'debug_spawn', etype: 3, dist: 400, angle: 0.3 });  // APC 400 m ahead
g.net.send({ t: 'debug_hurt', module: 2, amount: 999 });            // blow off the left arm
g.net.send({ t: 'debug_tp', dist: 1500 });                         // hop 1.5 km forward
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
  otherwise the predicted impact point drifts off the real one.
* **Sound is not immediate.** Gunfire and explosions go through
  `game._sound(x, y, z, play)` -> `game.sounds`, a list of acoustic events in
  the air (emit position + time, 20 s TTL). `_pumpSounds()` runs every frame and
  compares the wavefront radius (`SOUND_SPEED` = 343 m/s x age) with the
  distance to the mech's **current** position: the event plays (at
  `attenuation(dist)`, inverse square anchored at 1 km = 50%) and leaves the
  list when the front catches up. Using the live position is what makes a
  180 km/h hull hear a bang where it actually caught up with it - never cache
  the distance at emit time. Flashes, impacts, screen shake and the electronic
  cues (lock tone, RWR alarms, kill/denied chimes) stay immediate.
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

* `_camera` is a cockpit camera sitting on the canopy dome: it adds
  `defs.mech.eye` (5.05 m) plus `CAM_UP` (1.2 m) to `own.y` and steps `CAM_FWD`
  (0.75 m) along the view yaw, hence the `- 5.05` above (the eye then lands
  1.2 m higher and 0.75 m ahead - close enough for scenery). The pilot's own
  machine is drawn like any other unit, in full: there is **no separate
  first-person view model**, so the barrels on screen are the barrels the
  rounds leave from.
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
