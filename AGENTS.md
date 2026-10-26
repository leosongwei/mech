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
    ballistics.py          # fire control: muzzle sight point, zeroing elevation
    netcode.py             # init payload, binary snapshots, JSON meta
    main.py                # FastAPI: websockets, static files, 30 Hz game loop
    systems/               # mechanics shared by every mode, each exposes run(world, dt)
                         #   smoke.py: hot smoke canisters, opaque puffs, blocks(a, b)
    modes/                 # per-mode rule sets: map, setup, pipeline, spawn rules
      base.py              #   Mode interface every mode implements
      battle/              #   waves.py: town capture + reinforcement waves
      range/               #   targets.py: passive APC targets, bunkers.py: ridge blockhouses,
                           #   threats.py: one incoming top-attack missile every 10 s
  assets/                  # compiled client (webpack output, gitignored)
tools/
  smoke_lock_test.js       # browser-console driver: range missile A/B smoke test
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
    hud.js                 # crosshair, lock box, module health, minimap, panels
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
  `locking, smoke, weapons, movement, projectiles, economy, debris`, then the
  mode's post-systems (battle: `waves`). `World.update(dt)` is the only entry point; the loop in
  `main.py` calls it at a fixed 30 Hz (`TICK_DT = 1/30`).
* **Modes** (`modes/`) own everything that differs between game modes: the
  map, what a fresh round spawns, the mode's own systems and the rules the
  shared code asks about (`fixed_faction`, `start_money`, `rearm_anywhere`,
  `restart_respawn`, `mech_spawn`). Adding a mode means adding a folder, never
  another `if` in a shared system. Battle runs `ai` + `waves` (capture and
  reinforcement waves); range runs `targets` and no AI at all.
* **Players** are plain Python objects (`world.Player`) attached to a mech
  entity through `owner`; `pl.mech` is -1 while dead. Income and respawns live
  in `systems/economy.py`; wreck/corpse cleanup in `systems/debris.py`.
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
| `debug_hurt` | `module` (`0` hull / `1` torso / `2` left arm / `3` right arm, default `1`), `amount` (default `100`), `base` (damage to the *enemy* command base) | wounds the player or the enemy base |

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
  texture, tiles from an `R8UI` one. Roads are a ribbon mesh built from
  `map.lanes` (`_buildRoads`, gravel/asphalt/dashed centre line) so a lane can
  cut diagonally across the grid. Units/props/projectiles are instanced
  primitives submitted through `WEBGL_multi_draw` with a per-command fallback.
  Prop instances are batched per mesh (`this.meshes.length` batches) and
  culled in 640 m chunks (`CHUNK_TILES` = 40) by `_buildProps`, which plants
  several trees per tree tile and a block plus roof per building tile.
* `scene.js` builds unit models from primitives in a local frame: **+X
  forward, +Y up, +Z right** (see `math.js compose`). A Y-axis primitive is
  laid along the barrel with `pitch = FORWARD` (`-PI/2`); use
  `FORWARD + p` when a pitch is already in play.
* `map.js` must stay ballistically identical to the server: same gravity,
  same 1/30 step, same launch bias (`wd.bias`), otherwise the predicted impact
  point drifts off the real one.
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
elsewhere, 12 blue APC targets (2 patrolling) that respawn 3 s after dying and
never shoot back, plus one IR top-attack missile from the hill at the player
every 10 s so the hot smoke can be tested against a real seeker. The player is
locked to RED with $99999, can rearm anywhere and the smoke cooldown is 2 s.
Lock breaks are logged by `mech.smoke`, launches by `mech.range`; the browser
script `tools/smoke_lock_test.js` drives a whole A/B run from the console. Log:
`/tmp/mech-range.log`, overrides `MECH_RANGE_PORT` (8011), `MECH_RANGE_LOG`.
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
