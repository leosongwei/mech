# MECH — 16 km Front

A browser based mech shooter: a numpy-ECS Python server, a WebGL2 client with
indirect (multi-draw) instanced rendering, three lanes, two factions and
physically flown projectiles.

```
dev.sh                 # start / attach / stop the server (byobu / tmux)
backend/               # FastAPI + simulation
  server/ecs.py        # numpy component registry, entity masks, queries
  server/components.py # every component the game declares
  server/systems/      # gameplay systems, one module per mechanic
  server/terrain.py    # 1600x1600 tiles of 10 m -> 16 km square map
  server/netcode.py    # init payload, binary snapshots, JSON meta
  server/main.py       # websockets + static files + 30 Hz game loop
  assets/              # compiled client (webpack output)
frontend/              # webpack + WebGL2 client
  src/renderer.js      # terrain LOD grid, instanced primitives, draw lists
  src/map.js           # client copy of terrain + identical ballistics
  src/scene.js         # unit models built from primitives
  src/hud.js           # crosshairs, lock box, module health, minimap
```

## Run

```bash
./dev.sh dev          # build the client, then start the server in a session
./dev.sh attach       # attach to that byobu/tmux session (F6 to detach)
./dev.sh stop
```

or manually:

```bash
uv venv && uv pip install -r backend/pyproject.toml
cd frontend && npm install && npm run build
cd ../backend && ../.venv/bin/python run.py --port 8010
```

Then open <http://127.0.0.1:8010/>.  The lobby lets you pick a callsign, a side
(RED / BLUE / AUTO) and the weapon in each arm before you hit **DEPLOY**.

`MECH_DEBUG=1` (set by `dev.sh`) enables websocket development helpers:

```jsonc
{"t":"debug_spawn","etype":3,"dist":800,"angle":0}   // drop a unit in front of you
{"t":"debug_tp","dist":400}                          // teleport your mech
{"t":"debug_hurt","module":1,"amount":300}           // damage your mech
{"t":"debug_hurt","base":7000}                       // smash the enemy command base
```

A destroyed command base ends the round; the match resets automatically 40 s
later on the same map.

## Controls

| input | action |
| --- | --- |
| `W A S D` | move, relative to the view; the legs turn towards the travel direction |
| mouse | X = torso yaw, Y = arm elevation |
| mouse wheel | sight magnification: 1x / 4x / 10x / 20x |
| `ALT` + mouse | free look (torso and arms stay put) |
| LMB / RMB | fire left / right arm |
| `1`-`5` / `SHIFT`+`1`-`5` | buy a weapon for the left / right arm (at base or an owned town) |
| lobby | callsign, side and initial arm loadout before deploying |
| `H` | toggle the controls overlay |
| `ESC` | release the mouse |

## View

First person from the cockpit: the eye sits at the mech's sensor height and
looks exactly where the arms point, so the reticle is the centre of the screen
at every magnification. Only the pilot's own machine is trimmed - hull and
cockpit are skipped, the legs and the two weapon pods are drawn, the pods as a
camera anchored view model so they read as hands in the lower corners.

The wheel steps the optics through 1x / 4x / 10x / 20x. Magnification is a
field of view division (`1.05 rad / zoom`), mouse sensitivity is divided by the
same factor, and the radial terrain grid is rebuilt per step with more rings
per octave (`uG`) so the ground does not collapse into a handful of triangles
when 20x squeezes it into a three degree cone.

## Simulation model

* **ECS.** Every component is a dense numpy array of `(capacity, *shape)`;
  entities carry a 64-bit mask so `ecs.query("pos", "hp")` is one vectorised
  test. New mechanics = one new component + one system module.
* **Systems** (in `World.systems` order): `player_input` → `ai` → `locking` →
  `weapons` → `movement` → `projectiles` → `spawner`.
* **Tick** 30 Hz, snapshots 15 Hz as a packed binary blob (64 B/unit, 32 B/round),
  meta (money, lock progress, towns) at 10 Hz, effects as JSON.
  Each socket has its own outbound queue so a stalled client can never stop the
  simulation.
* **Lobby.** the websocket `join` message carries callsign, side and the two
  arm weapons; the server respawns the mech accordingly.
* **Terrain.** 1600×1600 vertex heightfield (1 m terraces) + a tile map
  (`grass / road / building / tree / rock / base / solid`), three lanes with
  towns along them, forests, boulders. Sent to the browser zlib-compressed, so
  the client can predict ballistics with the server's exact numbers.

## Combat

Every weapon is a real ballistic body: bullets and unguided rockets are
integrated with gravity, guided rounds fly with thrust, a maximum speed and a
turn-rate limit.

| weapon | kind | range | notes |
| --- | --- | --- | --- |
| 30mm autocannon | ballistic | 3.2 km | 1080 m/s, 190 rpm, 3 m splash |
| 12.7mm machine gun | ballistic | 1.8 km | 890 m/s, 680 rpm |
| unguided rocket pod | rocket | 4 km | gravity + motor, 2 rockets per trigger |
| SACLOS ATGM | beam rider | 3.75 km | steers onto your sight line, keep the reticle on target |
| top-attack IR ATGM | lock-on | 4.75 km | acquisition cone + 1.1 s lock, then fire and forget, dives onto the roof |
| 5.56 rifle / RPG | infantry | 450 / 500 m |  |

* Direct-fire weapons show the predicted impact point and range (client side
  ray march with the same gravity vector).
* Firing an IR missile without a lock does nothing: the HUD prints
  `NO LOCK` and plays a denial tone. While acquiring you hear interrupted
  beeps, once locked a steady tone plus a red diamond on the target.
* Mech damage is per module (`chassis / torso / left arm / right arm`); a
  destroyed arm loses its weapon, destroyed legs crawl at 20 % speed, torso or
  chassis at zero kills the machine.

## Modes

Dota-like square map: three lanes, wave reinforcements every 22 s (infantry,
then APCs every third wave, AI mechs every fourth), towns that can be captured
(9 s with no enemy inside) and become forward spawn points, money for kills,
and the enemy command base (6500 HP) as the win condition.

## Rendering

* One radial LOD grid is drawn for the whole 16 km map; heights are fetched
  from an `R32F` texture in the vertex shader, tile colours and detail noise
  are computed per pixel. The ring spacing is `tile * 2^(ring/uG)`, so `uG`
  selects how many rings one doubling of distance gets - the optics step
  rebuilds the grid with a larger `uG` and a finer index grid as the sight
  magnifies.
* Units, props and projectiles are instanced primitives (`box`, `sphere`,
  `cone`, `cylinder`). Prop chunks (640 m) are frustum/distance culled on the
  CPU and submitted as a command list through `WEBGL_multi_draw`
  (`multiDrawArraysInstancedWEBGL`), with a per-command instanced fallback when
  the extension is missing.
* Logarithmic depth in every shader keeps a 1 m … 32 km view stable.
