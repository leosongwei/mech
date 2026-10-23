# AGENTS.md

Working notes for coding agents touching **MECH**, a browser-based mech
shooter: numpy-ECS Python server, WebGL2 client, 16 km tile map, three lanes,
RED vs BLUE.

## Read `doc/product` first

`doc/product/` is the Chinese product documentation and the source of truth for
everything a player can see: interaction flow, every screen, and the full unit
and weapon parameter tables (mirrors of `defs.py` / `spawner.py`).

* **Before changing anything, read `doc/product/`** and check the change fits
  the documented rules.
* **Every product-facing change must be registered there**: update the relevant
  page (`flow.md`, `screens.md`, `units.md`, `weapons.md`) and add a dated entry
  at the top of `doc/product/CHANGELOG.md` in the same commit. That covers UI
  elements, copy, keybindings, unit/weapon numbers, spawn and rule timers,
  win conditions and debug UI.
* Tuning changes in `defs.py` / `spawner.py` without the matching table update
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
    terrain.py             # 1600x1600 tiles of 10 m -> 16 km square map
    mathutil.py            # angles, lead solving, terrain helpers
    netcode.py             # init payload, binary snapshots, JSON meta
    main.py                # FastAPI: websockets, static files, 30 Hz game loop
    systems/               # one module per mechanic, each exposes run(world, dt)
  assets/                  # compiled client (webpack output, gitignored)
frontend/
  webpack.config.js        # entry src/main.js -> ../backend/assets/bundle.js
  src/
    main.js                # boots Game, exposes it as window.__game
    game.js                # client loop, input -> server, camera, fx, debug panel
    net.js                 # websocket, snapshot decode, interpolation
    map.js                 # client copy of terrain + identical ballistics
    scene.js               # unit models built from instanced primitives
    renderer.js            # terrain LOD grid, instanced draws, multiDraw batching
    hud.js                 # crosshair, lock box, module health, minimap
    meshes.js              # primitive meshes (box, sphere, cone, cylinder, rock)
    particles.js           # smoke, trails, impacts
    math.js                # mat4, lookAt, compose, dirFrom, angles
    input.js               # pointer lock, keys, wheel, buy hotkeys
    audio.js               # WebAudio cues
```

### Server

* **ECS** — every component is one numpy array indexed by entity slot.
  Presence lives in a `uint64` mask per entity; queries return index arrays
  (`ecs.query("etype", "hp")`). Never loop over 8192 slots per system; use the
  query results. Entities are created/destroyed through `ECS.create/destroy`.
* **World** (`world.py`) owns terrain, players, the ECS and the system
  pipeline, in this order: `player_input, ai, locking, weapons, movement,
  projectiles, spawner`. `World.update(dt)` is the only entry point; the loop
  in `main.py` calls it at a fixed 30 Hz (`TICK_DT = 1/30`).
* **Players** are plain Python objects (`world.Player`) attached to a mech
  entity through `owner`; `pl.mech` is -1 while dead. Respawn, income and
  capture live in `systems/spawner.py`.
* **Lanes** — `world.lanes` counts from the red base, `world.lanes_rev` from
  the blue base. Every per-faction lookup (spawn point, path index, "front
  line") must use the faction's own copy; mixing them mirrors positions across
  the map. `path_i` is always an index into the faction's own list.
* **Netcode** — one init payload with the map and all definitions, then binary
  snapshots at 15 Hz (`SNAP_EVERY`) and JSON meta at 10 Hz (`META_EVERY`).
  Entity state is authoritative on the server; the client only interpolates.
* **Debug messages** are accepted only when `MECH_DEBUG=1`:
  `debug_spawn`, `debug_tp`, `debug_hurt`, `debug_god`, `debug_restart`
  (see `main.py`). `respawn` (skip the death timer) is always allowed.

### Client

* `game.js` sends input at 30 Hz (`{t:"in",yaw,pitch,mx,mz,fl,fr}`) and drives
  the frame: interpolate snapshots, aim, build the draw list, draw, HUD.
* `renderer.js` renders the whole map as one radial LOD grid; heights come
  from an `R32F` texture. Units/props/projectiles are instanced primitives
  submitted through `WEBGL_multi_draw` with a per-command fallback.
  Prop instances are batched per mesh (`this.meshes.length` batches) and
  culled in 640 m chunks by `_buildProps`.
* `scene.js` builds unit models from primitives in a local frame: **+X
  forward, +Y up, +Z right** (see `math.js compose`). A Y-axis primitive is
  laid along the barrel with `pitch = FORWARD` (`-PI/2`); use
  `FORWARD + p` when a pitch is already in play.
* `map.js` must stay ballistically identical to the server: same gravity,
  same 1/30 step, same launch bias (`wd.bias`), otherwise the predicted impact
  point drifts off the real one.
* `window.__game` (`game.js` instance) is exposed for automation: `g.input`,
  `g.net.send(...)`, `g.net.meta`, `g.net.entities`, `g.renderer`.

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
never shoot back. The player is locked to RED with $99999 and can rearm
anywhere. Log: `/tmp/mech-range.log`, overrides `MECH_RANGE_PORT`
(8011), `MECH_RANGE_LOG`. Product notes: `doc/product/range.md`.

Environment overrides: `MECH_SESSION` (default `mech`), `MECH_PORT` (8010),
`MECH_LOG` (`/tmp/mech-server.log`). `dev.sh` always sets `MECH_DEBUG=1`,
which unlocks the debug websocket messages and the top-right debug panel
(RESPAWN / RESTART / GOD).

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
  in `defs.py` and at the top of `systems/spawner.py` — prefer changing them
  there over editing the code paths.
* A weapon is a real projectile: keep `kind` semantics
  (`bullet`/`rocket`/`saclos`/`top`) intact when adding or retuning one.
