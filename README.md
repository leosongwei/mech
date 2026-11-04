# MECH — 16 km Front

A browser based mech shooter: a C# (.NET 10 / ASP.NET Core) archetype-ECS
server, a WebGL2 client with indirect (multi-draw) instanced rendering, three
lanes, two factions and physically flown projectiles.

```
dev.sh                 # start / attach / stop the server (byobu / tmux)
Dockerfile.backend     # .NET 10 server image (port 8010)
Dockerfile.frontend    # nginx + webpack client image (port 8080)
deploy/k8s/            # kustomize manifests: battle and range, split images
.github/workflows/     # build & push both images to GHCR
dotnet.sh              # build / run helper (dotnet's own state in .dotnet_tmp/)
MechServer.csproj      # the server; <Version> is the product version
Program.cs             # entry point: --host / --port / --range / --check
Ecs/                   # archetype ECS: an entity's component set is its type
  Ecs.cs               #   buffers, queries, views, slot/group doors
  Components.cs        #   every component the game declares
Core/                  # the shared libraries and the world
  Defs.cs              #   entity types, weapons, unit stats, mech geometry
  Terrain.cs           #   1000x1000 tiles of 16 m -> 16 km square map
  World.cs             #   players, spawning, tick, the system pipeline
  Combat.cs Ballistics.cs Flight.cs Seeker.cs Shop.cs Grid.cs MathUtil.cs
Systems/               # gameplay systems, one file per mechanic
Modes/                 # per-mode rules: Battle (waves, raids), Range
Net/                   # Netcode.cs: init payload / snapshots / meta
                       # Server.cs:   websockets + static files + 30 Hz loop
Harness/               # headless checks: ./dotnet.sh run -- --check battle|range
assets/                # compiled client (webpack output, gitignored)
frontend/              # webpack + WebGL2 client
  src/renderer.js      # tiled terrain (near patch + far LOD), roads, instancing
  src/map.js           # client copy of terrain + identical ballistics
  src/scene.js         # unit models built from primitives
  src/hud.js           # crosshairs, lock box, module health, minimap
```

## Install (.NET 10 on Ubuntu 26.04)

The server is a single .NET 10 project with **no third-party packages**, so the
SDK is the whole dependency (it brings the ASP.NET Core runtime with it):

```bash
sudo apt update
sudo apt install -y dotnet-sdk-10.0
dotnet --list-sdks          # must print 10.0.x
```

That is the whole install on Ubuntu 26.04, whose archive carries .NET 10 as an
LTS.  If `dotnet-sdk-10.0` is not found there, or you are on a release whose
archive is behind, use one of these two instead:

```bash
# 1. Microsoft's apt feed (replaces any distro dotnet package)
sudo apt install -y wget gpg
wget -qO- https://packages.microsoft.com/keys/microsoft.asc \
  | sudo gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg
echo "deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] \
https://packages.microsoft.com/ubuntu/24.04/prod noble main" \
  | sudo tee /etc/apt/sources.list.d/microsoft-prod.list
sudo apt update && sudo apt install -y dotnet-sdk-10.0

# 2. user-local install, no root, no repo (put it on PATH yourself)
curl -sSL https://dot.net/v1/dotnet-install.sh \
  | bash -s -- --channel 10.0 --install-dir "$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
```

Only the **SDK** is needed - not the desktop/runtime bundles, not
`dotnet-aspnetcore-runtime` separately.  Client-side builds want Node.js 20+
(`frontend/`, `npm install && npm run build`).

`dotnet.sh` is the way to run it here, not a convenience wrapper: `dotnet`
writes first-run state into `$HOME` (and a `mkdtemp` under `$XDG_RUNTIME_DIR`)
before it reads the project, so on a box where those are read-only a bare
`dotnet build` dies with `EROFS`.  The script puts all of it under
`.dotnet_tmp/` (gitignored) and runs from the repo root:

```bash
./dotnet.sh build                              # restore + compile
./dotnet.sh run -- --port 8010                 # battle
./dotnet.sh run -- --port 8011 --range         # weapon range
./dotnet.sh run -- --check battle --minutes 5  # headless, no socket
```

## Run

```bash
./dev.sh dev          # build the client, then start the server in a session
./dev.sh attach       # attach to that byobu/tmux session (F6 to detach)
./dev.sh stop
```

or manually:

```bash
cd frontend && npm install && npm run build && cd ..
./dotnet.sh run -- --port 8010                 # battle (16 km map)
./dotnet.sh run -- --port 8011 --range         # weapon range (4 km test map)
```

<<<<<<< HEAD
Everything runs through `./dotnet.sh` (see **Install** above) - a bare
`dotnet build` is never needed.
=======
Needs the .NET 10 SDK.  `dotnet.sh` exists because `dotnet` wants to write
first-run state into `$HOME`; the script points all of it at `.dotnet_tmp/`
(gitignored) so a bare `dotnet build` is never needed.
>>>>>>> 43dd4b6 (Replace the Python backend with the C# one, move the repo to a single root layout)

Then open <http://127.0.0.1:8010/>.  The lobby lets you pick a callsign, a side
(RED / BLUE / AUTO) and the weapon in each arm before you hit **DEPLOY**.

`MECH_DEBUG=1` (set by `dev.sh`) enables websocket development helpers:

```jsonc
{"t":"debug_spawn","etype":3,"dist":800,"angle":0}   // drop a unit in front of you
{"t":"debug_tp","dist":400}                          // teleport your mech
{"t":"debug_hurt","module":1,"amount":300}           // damage your mech
{"t":"debug_hurt","base":7000}                       // smash the enemy command base
{"t":"debug_god","on":true}                          // ignore all damage while testing
```

A destroyed command base ends the round; the match resets automatically 40 s
later on the same map.

## Container

<<<<<<< HEAD
The deployment is split into two images:

- **`mech-backend`** — the .NET 10 SDK publishes the ASP.NET Core server from
  `MechServer.csproj` and the runtime image runs it on `8010` as a non-root
  user. It ships no client files; the game endpoint is `/ws`.
- **`mech-frontend`** — Node 22 builds the client with the existing webpack
  flow, then the result is copied into `nginx-unprivileged`. Nginx serves the
  static files on `8080`, proxies `/ws` to the backend, and exposes `/healthz`
  for probes.

```bash
docker network create mech

docker build -f Dockerfile.backend  -t mech-backend .
docker build -f Dockerfile.frontend -t mech-frontend .

# battle: backend is internal, the browser talks to nginx only
docker run -d --name mech-backend --network mech mech-backend
docker run -d --name mech-web --network mech -p 8080:8080 \
  -e BACKEND_HOST=mech-backend -e BACKEND_PORT=8010 mech-frontend

# range (4 km test map)
docker run -d --name mech-range-backend --network mech \
  -e MECH_RANGE=1 mech-backend
docker run -d --name mech-range-web --network mech -p 8081:8080 \
  -e BACKEND_HOST=mech-range-backend -e BACKEND_PORT=8010 mech-frontend
```

Then open <http://127.0.0.1:8080/>. `MECH_DEBUG` is off in the image; turn the
console helpers on deliberately with `-e MECH_DEBUG=1` on the backend.
`MECH_LOG_LEVEL=DEBUG` enables the per-weapon log lines. There is no persistent
state, so a restart is a fresh battle. Do not run more than one backend
replica: the world is a single in-process object, and every extra process is a
separate battle. The frontend is stateless.

## Kubernetes

`deploy/k8s/` contains kustomize manifests for both modes, each split into a
backend and a frontend:

| mode | frontend (ClusterIP) | backend (ClusterIP) |
| --- | --- | --- |
| battle | `mech-battle-frontend:8080` | `mech-battle-backend:8010` |
| range | `mech-range-frontend:8080` | `mech-range-backend:8010` |

The frontend nginx proxies `/ws` to the matching backend Service. Backends run
one replica with `Recreate` and TCP startup/readiness/liveness probes; frontends
are stateless and probe `/healthz`. All four pods run non-root with a read-only
root filesystem and a `/tmp` emptyDir.
=======
The image is multi-stage: Node 22 builds the client into `backend/assets`, then
the runtime image installs the backend from `pyproject.toml` with `uv`. The
server listens on `8010` and runs as a non-root user.

```bash
docker build -t mech .

# battle
docker run --rm -p 8010:8010 mech

# range (4 km test map)
docker run --rm -e MECH_RANGE=1 -p 8011:8010 mech
```

`MECH_DEBUG` is off in the image; turn the console helpers on deliberately with
`-e MECH_DEBUG=1`. `MECH_LOG_LEVEL=DEBUG` enables the per-weapon log lines.
There is no persistent state, so a restart is a fresh battle. Do not add
uvicorn workers or run more than one replica: the world is a module-level
single-instance object, and every extra process is a separate battle.

## Kubernetes

`deploy/k8s/` contains kustomize manifests for both modes: `mech-battle`
(ClusterIP `8010`) and `mech-range` (ClusterIP `8011`, `MECH_RANGE=1`). Both run
one replica with `Recreate`, startup/readiness/liveness probes on `/`, a
read-only root filesystem and a `/tmp` emptyDir.
>>>>>>> 76a725c (新增一些基础设施)

```bash
kubectl apply -k deploy/k8s
kubectl -n mech get pods,svc
<<<<<<< HEAD
kubectl -n mech port-forward svc/mech-battle-frontend 8080:8080
kubectl -n mech port-forward svc/mech-range-frontend 8080:8080
```

The Services are ClusterIP only: expose the frontend Service through your own
reverse proxy or gateway (it must pass WebSocket Upgrade and long-lived
connections), or use `port-forward` for local play. `deploy/k8s/configmap.yaml`
holds the backend's `MECH_DEBUG` and `MECH_LOG_LEVEL`; set `MECH_DEBUG=1` there
only for a test deployment.

`.github/workflows/container.yml` builds and pushes
`ghcr.io/leosongwei/mech-backend` and `ghcr.io/leosongwei/mech-frontend` to GHCR
on `main` and `v*` tags (pull requests build without pushing). The manifests use
`:latest`, so restart the Deployments after a new push:

```bash
kubectl -n mech rollout restart deploy/mech-battle-backend deploy/mech-battle-frontend
kubectl -n mech rollout restart deploy/mech-range-backend deploy/mech-range-frontend
```

If the GHCR packages are private, add an `imagePullSecrets` entry to the
=======
kubectl -n mech port-forward svc/mech-battle 8010:8010
kubectl -n mech port-forward svc/mech-range 8011:8011
```

The Services are ClusterIP only: expose them through your own reverse proxy or
gateway (it must pass WebSocket Upgrade and long-lived connections), or use
`port-forward` for local play. `deploy/k8s/configmap.yaml` holds `MECH_DEBUG`
and `MECH_LOG_LEVEL`; set `MECH_DEBUG=1` there only for a test deployment.

`.github/workflows/container.yml` builds and pushes
`ghcr.io/leosongwei/mech` to GHCR on `main` and `v*` tags (pull requests build
without pushing). The manifests use `:latest`, so restart the Deployments after
a new push:

```bash
kubectl -n mech rollout restart deploy/mech-battle deploy/mech-range
```

If the GHCR package is private, add an `imagePullSecrets` entry to the
>>>>>>> 76a725c (新增一些基础设施)
Deployments.

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

* **ECS.** An archetype ECS: the group of entities owning exactly the same
  component set holds one dense, flat `float`/`int`/`byte` buffer per
  component, and an id resolves to (archetype, row). The component set is the
  entity's type and never changes, so new mechanics = one new component + one
  system file.
* **Systems** (in `World.systems` order): `rearm` → `player_input` → *(mode)*
  `raids` → `ai` → `gunnery` → `locking` → `smoke` → `weapons` → `movement` →
  `projectiles` → `proximity` → `blasts` → `mortality` → `economy` → `debris` →
  *(mode)* `waves`.
* **Tick** 30 Hz, snapshots 15 Hz as a packed binary blob (80 B/unit, 32 B/round,
  32 B/smoke), meta (money, lock progress, towns) at 10 Hz, effects as JSON.
  Each socket has its own outbound queue so a stalled client can never stop the
  simulation.
* **Lobby.** the websocket `join` message carries callsign, side and the two
  arm weapons; the server respawns the mech accordingly.
* **Terrain.** 1000×1000 vertex heightfield quantised into 4 m terraces (the
  angular OpenTTD look) + one byte per 16 m tile (`grass / road / building /
  tree / forest / rock / base / solid`), three lanes with towns along them,
  groves, boulders. Roads are drawn as ribbons along the lane polylines, so a
  lane can cut diagonally across the grid. Both arrays are sent to the browser
  zlib-compressed, so the client can predict ballistics with the server's
  exact numbers.

## Combat

Every weapon is a real ballistic body: bullets and unguided rockets are
integrated with gravity, guided rounds fly with thrust, a maximum speed and a
turn-rate limit.

| weapon | kind | range | notes |
| --- | --- | --- | --- |
| 30mm autocannon | ballistic | 3.2 km | 1080 m/s, 300 rpm, 8 m splash |
| 12.7mm machine gun | ballistic | 1.8 km | 890 m/s, 680 rpm |
| unguided rocket pod | rocket | 4 km | one round per trigger pull, no reload, 20 in the pod; the launcher is sighted 2.5° up so it meets the cannon's line at 1 km |
| SACLOS ATGM | beam rider | 3.75 km | steers onto your sight line, keep the reticle on target |
| top-attack IR ATGM | lock-on | 4.75 km | acquisition cone + 1.1 s lock, then fire and forget, dives onto the roof |
| 5.56 rifle / RPG | infantry | 450 / 500 m |  |

* Direct-fire weapons show the predicted impact point and range (client side
  ray march at the server's 30 Hz step with the same gravity vector and the
  same launch bias, so the reticle is where the round really lands).
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

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE).
