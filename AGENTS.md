# AGENTS.md

Working notes for coding agents touching **MECH**, a browser-based mech shooter:
a C# (.NET 10) archetype-ECS server, a WebGL2 client, a 16 km tile map, three
lanes, RED vs BLUE, a 30 Hz clock. Player-facing rules live in `docs/product/`,
engineering manuals in `docs/dev/`. This file is the contract: principles, the
development flow, and who lives where.

## 1. Documents, and the order of a change

Four layers, one job each:

| layer | where | answers | a defect looks like |
| --- | --- | --- | --- |
| working notes | `AGENTS.md` (this file) | how code must be written, how to build/run/verify, which module owns what | a player-visible number, or a mechanic explained in prose |
| product docs | `docs/product/` | rules, numbers, screens, judgements | one page serving two readers |
| engineering manual | `docs/dev/` | how to build, run, measure; client conventions | read only while developing |
| plans | `docs/plans/` | why it was designed this way | edited after the work landed |

**Before changing anything, read `docs/product/`** (index: `docs/product/README.md`)
and check the change fits the documented rules.

The order of a product-facing change, all in one commit:

1. the code;
2. the product page the fact belongs to (`rules/`, `systems/`, `hud/`, `modes/`);
3. a dated entry at the top of `docs/product/CHANGELOG.md` - eight lines or fewer,
   the difference a player sees plus the pages touched;
4. the version in `MechServer.csproj`: a feature moves the second number (the
   third back to 0), a change a player cannot see moves the third. `frontend/package.json`'s
   `version` is a private manifest and is never read.

Rules that keep the four layers honest:

- **One fact, one home.** A number, a formula or a judgement lives on the page
  that owns it; every other page carries a link. `docs/product/README.md` holds
  the ownership table ("改哪里").
- **New mechanics go to `docs/product/`.** A functional paragraph added to this
  file is a review failure; the principles below are the only things that belong
  here.
- **Internal refactors register nothing** - no page, no CHANGELOG line - and a
  change to a document or a comment alone bumps no version.
- **Code is English, documentation is Chinese.** Source, comments, commit
  messages, log lines and every string a player reads are English. Everything
  under `docs/` is Chinese, quoting UI strings and identifiers verbatim. The rule
  is about the language of a file, never its name: this file stays English
  because it is the agent's notes, not a document.
- **No negations in comments or documents.** Describe what the code is and what
  it does. A rule that must hold is stated positively; a thing that does not
  exist is not worth a sentence.
- **Units are metres and seconds.** The map is 16 km square and the tick is
  1/30 s.
- **Minimal, scoped edits.** Tuning values live in `Core/Defs.cs` and at the top
  of the mode systems, never inline in a code path. A bug fix does not carry a
  cleanup of the surrounding code.

## 2. Development flow

### Build

```bash
cd frontend && npm run build     # -> assets/  (deps check + syntax check + webpack)
./dotnet.sh build                # the server; must be clean, zero warnings
./dev.sh build                   # the client build with the shared npm cache
```

Always build the server through `./dotnet.sh`: it points the CLI home, the NuGet
cache, `XDG_RUNTIME_DIR` and `TMPDIR` at `.dotnet_tmp/` (gitignored), because a
bare `dotnet build` writes into `$HOME` and dies on a read-only sandbox.
`Nullable` is on: a warning is a defect.

A frontend change needs only a rebuild - the server reads `assets/` per request,
so no restart. A backend change needs a restart (`./dev.sh stop && ./dev.sh start`).

### Run

```bash
./dev.sh dev      # build the client, then start the server (first run)
./dev.sh start    # start the server in session "mech" (byobu, or plain tmux)
./dev.sh attach   # attach (detach: F6 / Ctrl-a d)
./dev.sh stop     # kill the battle and range sessions
./dev.sh status
./dev.sh range    # the weapon-range server in session "mech-range"
tail -f /tmp/mech-server.log
```

<http://127.0.0.1:8010/> is the battle server, <http://127.0.0.1:8011/> the
range. The lobby picks the callsign and the side before `DEPLOY`.

**Never start the server with `nohup ... &`** or any other detached background
job: the tool call hangs on the open pipe and the process dies with the shell
that spawned it. Start it in the tmux session and read the world through
`./dev.sh status`, `curl` and the log.

In a sandboxed agent shell `/tmp` is a fresh per-session directory, so tmux
cannot create its socket until it exists:

```bash
mkdir -p /tmp/tmux-$(id -u) && chmod 700 /tmp/tmux-$(id -u)
```

### Verify

The headless harness is `Harness/`, driven by `./dotnet.sh run -- --check ...`.
It runs a real `World` in-process - no socket, no wall clock - so a check is a
number, not a session. `MECH_SEED` makes a single-threaded run repeatable; read
the number for its magnitude (did this make things worse), never as a baseline to
diff. Every check ends with `ecs.validate()`.

| command | asks |
| --- | --- |
| `--check battle --minutes 12` | a whole scripted battle: the census, the cost per tick |
| `--check range --minutes 2` | the range world with a pilot firing |
| `--check hit` | every weapon against a parked APC |
| `--check intercept` | every missile against the defence post |
| `--check beam` | a beam rider: hits, cover, a cut laser |
| `--check fcs` | crew versus pilot on the same scenarios, plus chassis/pull/smoke/arsenal |
| `--check drive` | the mech's thrust model against its own algebra |
| `--check terrain` | facet slopes, lane drivability, sample cost |
| `--check nav` | the navmesh: region, profiles, a long plan, the lanes |
| `--check stuck` | does a battle keep moving, and is anything trapped |
| `--check diag --minutes 2` | what a few AI hulls are doing |
| `--check net --minutes 30 --from 8` | one pilot's wire, per minute |
| `--check tiers --minutes 14 --from 8` | what alternative snapshot tiers would cost |
| `MECH_PERF=1 --check battle` | the same battle without the event scan, per-minute cost |

Commands, the acceptance shape and the current readings: `docs/dev/checks.md`.
Unit tests are `./run_tests.sh [cs|js|compare] [--filter NAME]` - no server, no
socket; `tests/cases/fcs.json` and `height.json` are contracts both hosts answer.

**Measure a mechanism on the range, not on a battle.** A battle is two dozen AI
crews firing, dying and driving through the same world, so a level, a node count
or a hit read there is mostly a measurement of where the pilot happened to be.
The range's fixtures never fire; `./dev.sh range` plus console messages is the
bench, and a battle is where the change is *seen* after the number is right.

### Logging

`MECH_LOG_LEVEL` sets verbosity (`INFO` by default, `DEBUG` on request).
`MECH_DEBUG` is a different thing: the websocket gate for the development
messages and for the DEBUG block of the settings menu (`dev.sh` always sets it).
A line that fires per round, per hit, per lock or per tick is DEBUG; INFO is for
the once-a-session event.

### In a browser

`window.__game` is the console door (`g.net.*`, `g.input`, `g.renderer`,
`g.audio`, `g.map.*`, `g.freeCam`). Pointer lock is rejected in headless
automation - set `g.input.*` directly instead of synthesising mouse moves.
Recipes: `docs/dev/browser.md`.

## 3. Module map

```
dev.sh  dotnet.sh  run_tests.sh  MechServer.csproj (the <Version>)  Program.cs (--check)
Ecs/        Ecs.cs (archetype storage)      Components.cs (every component)
Core/       Defs.cs (types, weapons, constants)   World.cs (players, tick, spawning)
            Terrain.cs (heightfield, roads, walls, towns)   Nav.cs  Grid.cs  Rng.cs
            FireControl.cs  Flight.cs  Seeker.cs  Ballistics.cs   Combat.cs  Shop.cs
            SphereTable.cs  Profiler.cs  MathUtil.cs
Net/        Netcode.cs (frames, snapshots, meta)   Server.cs (websocket + loop)
            KillStream.cs (the kill feed on its own wire)
Systems/    one file per system, `run(world, dt)`
  Rearm PlayerInput Gunnery Locking Smoke Weapons Unstick
  Movement/{Foot,Vehicle,Mech/{Steer,Thrust,Slope,Drag,Ground},Support/{Drive,Integrate}}
  Sweep Beams Flights FirstHit Impact Blasts Mortality Economy Debris Sight Smoke
  AI/{Sense,Threats,Intercept,Engage*,Wander}  AI/Strategies/{Front,Raid}
  AI/Support/{Tags,Crew,Policy,Engage,ThreatTable,Pipeline}  AI/Nav/{NavMesh,NavWalk,NavRegion,NavQuery,Paths}
  Support/KillFeed.cs          # the line a pilot reads - who killed whom, with what, from how far
Modes/      Mode.cs  Modes.cs  Battle/  Range/
Harness/    Check BeamCheck InterceptCheck FcsCheck NetCheck Tiers StuckCheck DriveCheck
            TerrainCheck NavCheck
tests/      cases/*.json  frontend/ (node:test)  backend/ (console runner)  compare.mjs
frontend/   src/{core,net,map,audio,render,scene,hud,fx,input,app}  (four layers, one direction)
assets/     the compiled client (gitignored)
tools/      gun_report.py  saclos_motor.py  smoke_lock_test.js
```

The server's tick, in `Core/World.cs`:

```
rearm, player_input,            # a bought job counts down; input becomes intent
mode pre-systems                # battle: the AI crews
gunnery, locking, smoke, weapons,   # the machines slew after the intent, then shoot
unstick, movement,                  # a trapped hull is moved clear, then every driver
sweep.spheres, beams, expire,       # the tick's hit spheres, the lasers, the batteries
flight_ballistic/rocket/saclos/top, # one system per kind of round
first_hit_ground/structures/units/fuse,   # what each leg ran into (a record only)
impact,                             # the one reader of that record; ends the round
blasts, mortality, economy, debris, # the damage chain, in that order
mode post-systems               # battle: waves
```

The client is four layers with one direction, checked by
`frontend/scripts/depgraph.mjs`: `core -> net/map/audio -> render/scene/hud/fx/input -> app`.
`render/passes.js` is the draw order, `app/frame.js`'s `STAGES` the frame order.
Conventions and the traps behind them: `docs/dev/client.md`.

### State ownership

| state | written by | read as |
| --- | --- | --- |
| `pos`, `vel` | the driver of the chassis (`Movement/`), the flight system of a round, the smoke canister | the world's one truth; every collision, seeker and snapshot reads it |
| `vel_cmd` | `player_input` (a pilot's stick) or a driver's `nav_* -> vel_cmd` step | the machine's intent; the gap to `vel` *is* the machine |
| `nav_*` | an AI crew (route, point, speed) | the driver's input, the one thing a crew may write about motion |
| `sight_yaw`/`sight_pitch` | `player_input`, the crews' `Engage`, a mode | where the eye looks; answers instantly |
| `yaw`, `pitch` | `Gunnery` | the laid gun; the hull and the arms slew after the sight |
| `hp` | `Combat.apply` (and `Shop`'s repair) | any system, but as of its own stage of the tick |
| `first_hit_record` | the flight systems (re-arm) and each collision system (`if t < first_hit_t`) | `Impact` only |
| `ai_tgt` | `AI/Sense` | a crew's belief; every crew gun aims at it, never at `pos` |
| snapshot, meta | `Netcode` | the client, which interpolates and never simulates |

## 4. Principles

**P1. The component set is an entity's type, and it never changes.** An entity is
created by one `ECS.create(comps)` call and its archetype is fixed for life; a
state transition is a *different* entity (a canister becomes the puff it bursts
into). `etype` is per-type data and is read only for that. Details:
`docs/dev/ecs.md`.

**P2. One `pos`, one `vel`, on every mover.** `vel` is what the entity is
actually doing: `pos` is advanced by it, every collision is solved against it, a
seeker leads on it. The order a hull was given lives in its own `vel_cmd`. A
scripted mover or a teleport writes both and zeroes `vel`. `World.spawn_unit`
throws if a mover was built without them. Details:
`docs/product/systems/movement.md`.

**P3. Identity is a component, never a type switch.** What an entity *is* comes
from the marker tags its type declares: `unit`, `combat`, `lockable`, `moves`,
`person`, `invulnerable`, `interceptable`. Systems query the tag they can handle,
so an entity that is not a hull is invisible to combat code with no exclusion
list. Details: `docs/dev/ecs.md`.

**P4. Write systems against buffers, not through `View`.** `groups()` hands out
an archetype with its buffers hoisted; `slot(eid)` is the per-entity door;
`locate(ids)` resolves a flat list once. A `View` is for a scattered read of one
component and answers a registered default. Pick on the shape of the loop.
Details: `docs/dev/ecs.md`.

**P5. The order a system visits entities in is not part of the simulation.**
`world.rng` is one stream per thread, so no draw can depend on which entity drew
first, and queries return archetype blocks. Verification is therefore by
magnitude and by the tick's invariants, never by diffing two runs. Details:
`docs/dev/checks.md`.

**P6. The pipeline is semantics; the rest is cost.** The shared stages, the
mode's pre- and post-systems and the round's own seven stages are the
simulation's meaning. Within a group, order buys a faster compare and nothing
else. Details: `docs/dev/pipeline.md`.

**P7. There is one line of sight.** Terrain, tree canopies and smoke all hide
through `Sight.clear` / `Sight.why`, and every eye goes through it: a crew's
lookout, the player's IR lock, a missile's seeker, the pre-aim box. A tree hides
for good, a puff for its five seconds - nothing else differs between them.
Details: `docs/product/systems/sight.md`.

**P8. Damage is two halves, and it is not a system.** `Combat.apply` is the only
writer of `hp` and it decides nothing: no death, no destruction, in the heat of
a hit. A round that lands takes both halves, a unit merely near a burst takes the
blast. Details: `docs/product/rules/damage.md`.

**P9. A round's ending is one record and one judge.** Each collision system
offers its own answer through `first_hit_record`; `Impact` is the one reader and
the only thing that ends a round. A round that runs out of battery writes a
contact at t=0, which is the whole of "it dies where it stands". Details:
`docs/product/rules/damage.md`.

**P10. A round's kind is a tag.** One kind of round is one tag plus one flight
system, never another branch in shared code; each shape a leg can meet gets its
own collision query at its own granularity. Details:
`docs/product/systems/flight.md`.

**P11. AI is intent, movement is the machine.** A crew says where and how fast
(`nav_*`) and where it is looking; the driver of the chassis owns `vel_cmd`,
`vel`, `pos`, `leg_yaw` and the wheeled state. Corner speed, the acceptance
radius and the obstacle fan are the machine's. A crew's guns fire at its belief
(`ai_tgt`), which `AI/Sense` refreshes through the one line of sight. Details:
`docs/product/systems/ai.md`.

**P12. One fire-control law, two settings, both hosts.** The law is a pure
function of a shot and a track; the pilot's zero is his dial and a crew's is the
range it is engaging. The client solves the same function and is compared against
the server by a shared case table. The client interpolates what the wire carries
and simulates nothing. Details: `docs/product/systems/firecontrol.md`.

**P13. Documents follow the change.** One fact has one home; only a
player-visible difference is a product change; a plan is frozen once it lands.
Details: `docs/product/README.md`.

**P14. Small edits, bench before battle.** A mechanism is proven on its own bench
and the battle is where it is accepted. A tuning number is one constant in one
table, and the page that owns it moves with it. Details:
`docs/dev/checks.md`.

## 5. Feature index

| to learn about | read |
| --- | --- |
| how a match starts, runs, ends | `docs/product/flow.md` |
| keys, zoom, zeroing, aim assist | `docs/product/controls.md` |
| a HUD element, a colour, a message line | `docs/product/hud/` |
| unit stats, modules, hit spheres, radar | `docs/product/rules/units.md` |
| weapon numbers, racks, ammunition | `docs/product/rules/weapons.md` |
| penetration, blast, fuses, death, interception | `docs/product/rules/damage.md` |
| prices, income, rearm time | `docs/product/rules/economy.md` |
| terrain, roads, trees, towns, lanes, the map's mirror | `docs/product/rules/map.md` |
| spawning, the three-step town pick, respawn waits | `docs/product/rules/spawn.md` |
| waves, reinforcements, raiders, capture, victory | `docs/product/modes/battle.md` |
| the range and its fixtures | `docs/product/modes/range.md` |
| aiming, launchers, the master arm, the trigger gate | `docs/product/systems/firecontrol.md` |
| ballistics, aerodynamics, guidance, motors, batteries | `docs/product/systems/flight.md` |
| locks, channels, memory, re-acquisition, belief points | `docs/product/systems/seekers.md` |
| the warning receiver | `docs/product/systems/rwr.md` |
| the mech's five laws, the bicycle model, slopes, unsticking | `docs/product/systems/movement.md` |
| crews: sensing, engaging, missile defence, raider planning, nav | `docs/product/systems/ai.md` |
| what hides what: terrain, canopies, smoke, the laser sphere | `docs/product/systems/sight.md` |
| sound: distance, the far bed, the limiter, voice budget | `docs/product/systems/audio.md` |
| frames, row widths, tiers, the clock, congestion, RTT, minimap | `docs/product/systems/net.md` |
| console messages and automation | `docs/product/interfaces/debug.md` |
| the kill feed's own wire | `docs/product/interfaces/kills_sse.md` |
| storage, the three doors, invariants | `docs/dev/ecs.md` |
| what runs when, and why there | `docs/dev/pipeline.md` |
| render passes, local frames, icons, quality | `docs/dev/client.md` |
| how to build, run and measure | `docs/dev/checks.md` |
| driving the game from a browser | `docs/dev/browser.md` |
