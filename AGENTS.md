# AGENTS.md

Working notes for coding agents touching **MECH**, a browser-based mech
shooter: C# (.NET 10) archetype-ECS server, WebGL2 client, 16 km tile map, three lanes,
RED vs BLUE.

## Read `doc/product` first

Everything under `doc/` is written in Chinese (the code, the comments and every
string a player sees are English - see [Conventions](#conventions)).
`doc/product/` is the product documentation and the source of truth for
everything a player can see: interaction flow, every screen, and the full unit
and weapon parameter tables (mirrors of `Core/Defs.cs` and the mode systems).

* **Before changing anything, read `doc/product/`** and check the change fits
  the documented rules.
* **Every product-facing change must be registered there**: update the relevant
  page (`flow.md`, `screens.md`, `units.md`, `weapons.md`) and add a dated entry
  at the top of `doc/product/CHANGELOG.md` in the same commit. That covers UI
  elements, copy, keybindings, unit/weapon numbers, spawn and rule timers,
  win conditions and debug UI.
* Tuning changes in `Core/Defs.cs` / mode systems without the matching table update
  count as unfinished work. Internal refactors with no player-visible effect do
  not need an entry.

## Architecture

```
dev.sh                     # build / start / attach / stop helper (byobu or tmux)
dotnet.sh                  # build / run helper (dotnet's own state in .dotnet_tmp/)
MechServer.csproj          # the server; its <Version> is the product version
Program.cs                 # entry point: --host / --port / --range / --check battle|range|hit|intercept
doc/                       # Chinese: every document in the repo lives here
  product/                 #   player-facing rules: flow, screens, units, weapons, changelog
  plans/                   #   refactor and feature plans (e.g. speed_component.md)
  top_attack_plan.md       #   design notes next to the code they describe
Ecs/
  Ecs.cs                   # archetype ECS: component set is the entity's type
  Components.cs            # every component the game declares
Core/
  Defs.cs                  # entity types, weapons, unit stats, mech geometry
  World.cs                 # World/Player: terrain, players, spawning, tick, queries
  Terrain.cs               # 1000x1000 tiles of 16 m -> 16 km square map
  MathUtil.cs              # angles, lead solving, hit spheres, muzzle geometry
  Profiler.cs              # white-box probes (MECH_PROBE=1), off and free by default
  Grid.cs                  # 128 m cell index over the units, refiled every tick (AI broadphase)
  Nav.cs                   # routes (the poly lines a driver follows) + the way point maths
  Ballistics.cs            # fire control: sight setting elevation (player) / sight-point solve (AI)
  Flight.cs                # aerodynamics + guidance of a winged round (the top-attack missile)
  Seeker.cs                # an IR round's lock: what it stares at, and what it re-locks on
  Combat.cs                # damage resolution: the only writer of `hp` (apply/hit/splash)
  Shop.cs                  # the rearm panel: quote, buy, the time a job costs
  SphereTable.cs           # the tick's hit spheres: centres/radii/prev/disp + a block index
Net/
  Netcode.cs               # init payload, binary snapshots, JSON meta, per-ear sound filter
  Server.cs                # ASP.NET Core: websockets, static files, 30 Hz game loop
Systems/                   # mechanics shared by every mode, each exposes run(world, dt)
                           #   AI/        the crews (see "AI is intent, movement is the machine"):
                           #     Sense.cs, Threats.cs, Intercept.cs, Engage{Mech,Vehicle,Foot}.cs,
                           #     Wander.cs (one file = one run), Strategies/{Front,Raid}.cs
                           #     (mission state machines), Support/{Tags,Crew,Policy,Engage,
                           #     ThreatTable}.cs (columns, vocabulary, shared fire control),
                           #     Support/Pipeline.cs (the block a mode registers)
                           #   Movement/  one driver per chassis: Foot.cs / Mech.cs / Vehicle.cs,
                           #     Support/{Drive,Integrate}.cs.  The only writer of `vel`/`pos`.
                           #   Gunnery.cs: the sight leads, the hull and the arms slew after it
                           #   Sight.cs: the one line of sight (terrain + smoke), clear/why
                           #   Smoke.cs: hot smoke canisters, opaque puffs, blocks(a, b)
                           #   Sweep.cs: the tick's hit spheres, its broad phase, and the one
                           #             "segment vs moving sphere" solve (the spheres system)
                           #   Flights.cs: one system per kind of round (bullet / rocket /
                           #             saclos / top) plus Expire, which ends a flight
                           #   FirstHit.cs: what each leg ran into, one system per shape
                           #             (ground / structures / units) plus the airburst fuse
                           #   Impact.cs: the one reader of `first_hit_record` and the only
                           #             thing that ends a round
                           #   Blasts.cs: an explosion is an entity; the only area damage
                           #   Mortality.cs: `hp <= 0` -> wreck, burst, kill money, victory
                           #   Rearm.cs: the bought job at the base, `work_remaining_time`
Modes/                     # per-mode rule sets: map, setup, pipeline, spawn rules
  Mode.cs                  #   Mode interface every mode implements
  Modes.cs                 #   the registry `Modes.get(name)`
  Battle/                  #   BattleMode.cs: setup, bases, respawn A/B/C, kills feed
                           #   Waves.cs: town capture + reinforcement waves
  Range/                   #   RangeMode.cs, Targets.cs: passive APC targets plus
                           #   two mechs on a 200 m leg at 16 m/s and a 1 km leg at
                           #   the hull's 50 m/s top speed - the only hulls that
                           #   accelerate hard, which is what the lead ring's
                           #   second-order term is measured on),
                           #   Bunkers.cs: ridge blockhouses, Record.cs: the top-attack trace,
                           #   Interceptor.cs: the missile-defence post
Harness/                   # headless checks, no socket: Check.cs (battle/range/diag),
                           # InterceptCheck.cs (missile defence, target hits),
                           # NetCheck.cs (one pilot's wire, per minute)
assets/                    # compiled client (webpack output, gitignored)
tools/
  gun_report.py            # weapon-report audio analysis (numpy; the numbers in audio.js)
  smoke_lock_test.js       # browser-console driver: seeker A/B smoke test
frontend/
  webpack.config.js        # entry src/main.js -> ../assets/bundle.js
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
    renderer.js            # tiled near terrain + far LOD grid, roads, instanced draws,
                           #   and the screen-space overlay (the threat arrows)
    hud.js                 # centre cross, arm impact markers, lock box, modules, minimap
    meshes.js              # primitive meshes (box, sphere, cone, cylinder, rock)
    particles.js           # smoke, trails, impacts
    math.js                # mat4, lookAt, compose, dirFrom, angles
    input.js               # pointer lock, keys, wheel, panel keys
    audio.js               # WebAudio cues, master gain + volume setting, SOUND_SPEED + attenuation()
```

### Server

* **ECS** — an *archetype* ECS: the group of entities that own exactly the same
  component set is an `Archetype`, and it holds one dense, flat buffer per
  component (`float[]`, `int[]`, `byte[]` or `ushort[]`, `stride` elements per
  row), as long as the entities that actually have it. An entity id is not a
  storage address any more — `_arch`/`_row` turn it into (archetype, row), so a
  hull's position is `a.f("pos")[row * 3 + 0..2]`. `ecs.query("unit")` returns the
  ids of one archetype block after another; `ecs.fv("pos")` answers `view[eid]`,
  `view.get(eid, i)`,
  `view.get(ids, i, dst)`, filling a registered default where the entity does not
  own the component, and `ecs.slot(eid)` / `ecs.row_f(eid, "pos", out row)` are
  the cheap single-entity doors.  Everything in the simulation is `float`.
* **The component set is the entity's type and never changes.** It is decided
  in the one `ECS.create(comps)` call that spawns the entity, from
  `Components.UNIT_COMPS` / `*_COMPS` plus `Defs.UNIT_DEFS[etype].tags`. A state
  transition is a *different* entity: `Smoke.cs` destroys a canister and
  creates the puff it bursts into. There is deliberately no
  `add_component`/`remove_component` — allowing one would make the archetype key
  space explode, invalidate row indices mid-tick, and turn the mask back into a
  mutable soup.
* **One position, one velocity, on everything that moves.** `pos` and `vel` are
  `float[3]` on every mover - a hull, a round, a smoke canister - and `vel` is
  **what the entity is actually doing**: `pos` is advanced by it, every collision
  is solved against it, a seeker leads on it. The order a hull was given lives in
  its own component, `vel_cmd`, and only a hull has one: the gap between the two
  *is* the machine (the mech's ramp, a car's steering, a wall). `movement` is the
  one writer of a hull's `vel` (including `vel[1]`, the slope it just drove
  over), `projectiles` of a round's, `smoke` of a canister's. A scripted mover or
  a teleport writes `pos` **and** zeroes `vel`; a controller writes `vel_cmd`.
  The invariant is checked - `World.spawn_unit` throws at every spawn if a mover
  was built without `vel` (or a `moves` hull without `vel_cmd`) - and it is worth
  keeping because the alternative was five subsystems each quietly rebuilding a
  velocity of their own: `Seeker.track` handed proportional navigation a hull's
  *command* (50 m/s the instant a pilot nudged the stick) and the top attack
  missed a stationary hull by 8-36 m (`doc/plans/speed_component.md`).
* **Wheeled locomotion state is three scalars, not a component.** `wheel_speed`
  (m/s along the hull, negative in reverse), `backout_t`, `blocked_t` belong to
  `movement._vehicle` alone: not on the wire, never written by a mode or an AI.
  They used to be one `veh` vec3, and `veh[1]` told nobody it was the back-out
  clock.
* **Write systems against buffers, not through `View`.** `ecs.groups(...)`
  hands out an archetype with its buffers hoisted and rows `0..n-1`: reach for
  it whenever the system integrates entities independently (movement, countdown
  timers, leg turning) and the whole pass becomes a handful of array calls.
  `ecs.slot(eid)` is the same thing for a per-entity loop that touches many
  components — one lookup, then `bufs["pos"][row, 0]`; `"combat" in bufs` is the
  component test. `ecs.locate(ids)` resolves a flat id list into `(rows,
  archetypes)` once, for a loop that keeps the ids and wants buffer access. A
  `View` (`ecs.fv`/`uv`/`iv`) is the door for a scattered read of one component,
  and answers the registered default where an entity does not own it. There is
  no batch-column door: the snapshot builds its rows one `slot()` at a time.
* **The order a system visits entities in is not part of the simulation.**
  `world.rng` is one stream per thread (`Core/Rng.cs`), so a draw cannot depend
  on which entity drew before it, and `query()` returns its ids in archetype
  blocks. Any pass may take `groups()` (buffers hoisted, no per-row component
  test) or `query()` (a flat id list) — pick on the shape of the loop, and the
  cost of a row, never on "does this system draw random numbers". `MECH_SEED`
  makes a run reproducible while one thread does all the drawing, which is what
  `--check` does; with a socket attached, or once a `run()` is parallelised, two
  runs differ. Verification is therefore by magnitude and by the per-tick
  invariants, never by diffing two runs. What *is* order, and is semantics
  rather than accident: the system pipeline below, and the damage chain
  (`projectiles`/`proximity` write `hp`, `blasts` judges the burst, `mortality`
  decides the death).
* **Identity is a component, never a type switch.** What an entity *is* comes
  from the marker components its type declares in
  `Defs.UNIT_DEFS[etype].tags`: `unit` (a hull rounds stop on and damage
  applies to), `combat` (may be targeted, may hold a town), `lockable` (IR
  seeker), `moves` (locomotion), `invulnerable` (bunker). Systems ask for the
  tag they can handle — `world.units()`, `combat_units()`, `lockable_units()`,
  `movable()`, `smoke_puffs()` — so an entity that is not a unit (a smoke puff,
  a shell) is invisible to combat code without any exclusion list. `etype` is
  per-type *data* (hull geometry, death recipe, which arm fires what) and is only
  read for that.
* **World** (`Core/World.cs`) owns terrain, players, the ECS and the system
  pipeline. Shared order: `rearm`, `player_input`, the mode's pre-systems
  (battle: `ai`), `gunnery, locking, smoke, weapons, movement, projectiles,
  proximity, blasts, mortality, economy, debris`, then the mode's post-systems
  (battle: `waves`).
  A round's tick then runs `spheres, expire, flight_ballistic, flight_rocket,
  flight_saclos, flight_top, first_hit_ground, first_hit_structures,
  first_hit_units, first_hit_fuse, impact` - one system per kind of round and one
  per shape a leg can run into (see "Identity is a component" and
  `first_hit_record` below).
  `gunnery` sits between the crews saying where they are looking and anything
  reading a barrel, so `yaw` / `pitch` are the *laid gun* by the time a weapon
  fires or the client draws.  The last three are the damage chain and their
  order is load bearing: `projectiles` and `proximity` write hp where they land
  and leave bursts behind, `blasts` judges the bursts (including the ones the
  previous tick's deaths left), and `mortality` decides who died - after every
  damage source, before `waves` counts hulls to take a town.
  `World.update(dt)` is the only entry point; the loop in `Net/Server.cs` calls it at
  a fixed 30 Hz (`TICK_DT = 1/30`).
* **An empty server is a paused server.**  With `CONNECTIONS` empty the tick
  loop in `Net/Server.cs` skips `World.update` and `broadcast` altogether and just
  sleeps (`IDLE_SLEEP` = 0.1 s, re-basing `next_t` on the way out so the world
  resumes on a clean tick instead of catching up).  Nothing is simulated and
  nothing is sent while nobody is connected - the battle, the economy, the
  waves and the raiders all stand exactly where the last pilot left them, which
  is the point: a server that had been idle for an hour used to hand a joining
  pilot a mid-war map.  `MECH_DEBUG=1` disables the pause (`PAUSE_WHEN_EMPTY`),
  because that is the run a script, a replay or a console session inspects from
  the outside with no browser attached.  A tool that drives `world` directly
  (`Harness/Check.cs`, ...) never goes through the loop and is unaffected
  either way.
* **The sight leads, the machine follows** (`Gunnery.cs`, `defs.MECH_
  SIGHT_Z`).  A mech's aim is a free turret on the roof, mounted *on the hull*
  `MECH_SIGHT_Z` (0.62 m) behind the hull's own rotation axis.  Two component
  pairs, never one: `sight_yaw` / `sight_pitch` is where the crew is looking
  (a pilot's view, an AI's intercept), and it answers instantly; `yaw` (the
  hull) swings after it at `MECH_HULL_RATE` (80 deg/s) and the arms' `pitch`
  climbs after it at `MECH_ARM_RATE` (50 deg/s), stopping at the mount's
  `MECH_PITCH_MIN/MAX` (-26 / +43 deg) while the sight keeps looking at the
  sky.  Firing uses the *bore* (`weapons.bore_of`); the eye, the IR seeker and
  a SACLOS beam use the *sight* (`weapons.aim_of`), because a gunner keeps his
  cross on the target and the launcher catches up.  The eye is the sight's own
  pivot (`mathutil.sight_eye(pos, hull_yaw)`) - so turning the *sight* does not
  move it, and turning the *hull* walks it along a 0.62 m arc.  That is the
  whole point of the split: the camera rides the machine, and the machine can
  be felt moving under a view that has already turned.  Every other unit aims
  with its whole mount and has nothing to catch up with; `gunnery` mirrors its
  sight from the hull (or from a carrier's turret) so the meaning of the
  component never changes.  The AI lays the sight on the intercept and only
  pulls the trigger when the *bore* is on it (`err < 0.14` rad, `|d pitch| <
  0.02`), or a hull still coming round would hose the dirt.
* **Buying costs time, not just money.** `shop.deal` charges the bill and books
  the priced lines on the pilot, writing the seconds the draft cost into the
  mech's `work_remaining_time` (a plain float, 0.0 = idle); `Rearm.cs`
  counts it down and only at zero applies the lines. `player_input` zeroes the
  movement and both trigger bits while it is above zero, so a job is a real
  pause in a fight — the mech can still look around. The job dies with the
  mech (and the money with it), and a second order is refused until it ends.
  Durations are `defs.REARM_*` (swap 2 s per arm, ammo 4 s per arm, repair 2 s
  per 100 hp) plus `defs.SMOKE_LOAD_S` for the smoke rack; `shop.quote` charges a
  swap both a fit and a reload. A smoke reload is priced per canister
  (`defs.SMOKE_LOAD_COST`, `SMOKE_CHARGES` in the rack) and only for the ones
  missing, so `REPAIR & REARM` can carry it. What was
  bought dies with the hull: `World.spawn_mech` resets `Player.loadout` to
  `defs.DEFAULT_LOADOUT`, so every respawn is a fresh standard machine and a
  105 mm is a thing to buy again.
* **An AI crew has no shop, so it draws its loadout.** `World.spawn_unit` hands
  a hull that declares `arms` (the mech — the one component test, not a type
  switch) a template off `defs.AI_LOADOUTS` when no `weapons` were passed: six
  configs at 30/10/10/25/15/10 %, the left arm always the 30 mm and the right
  arm the variable. A pilot's mech always passes `player.loadout`, and the
  range's defence post passes its own, so both are untouched by the table. The
  draw comes off `world.rng`.
* **AI is intent, movement is the machine.**  A crew says *where* and *how fast* -
  `nav_route` (an index into `world.routes`, `Core/Nav.cs`), or a point
  `nav_x`/`nav_z`, plus `nav_speed` - and nothing else that moves.  The drivers
  (`Systems/Movement/`: one file per chassis, `Foot` / `Mech` / `Vehicle`, with
  `Support/Drive.cs` for the intent-to-way-point step and `Support/Integrate.cs` for
  `vel_cmd -> vel -> pos`) own `pos`, `vel`, `vel_cmd`, `leg_yaw`, `wheel_speed`,
  `backout_t`, `blocked_t` and the route cursor `path_i`.  Corner speed, the
  acceptance radius and the obstacle fan are *the machine's*: an 11 m turning radius
  cannot hold 100 km/h round a right angle, so `Nav.route_speed` / `Nav.arrive_radius`
  are called by `Vehicle.run` and never by a crew.  A pilot writes `vel_cmd` from the
  stick; an AI crew never touches it.  What a driver does for *every* hull of its
  chassis is the machine's own behaviour - the leg swing, the mech's ramp, the
  bicycle model, the integration - so only the `nav_* -> vel_cmd` step is gated on
  `ai_crew`.  Leave a pilot's `leg_yaw` lying where it spawned and his hull tops out
  at the leg-alignment floor: `0.45 * 50 m/s`, i.e. 81 km/h, in whatever direction
  the alignment happens to be bad - which reads as "some directions are slower".  The plan and its reasoning:
  `doc/plans/AI_plan.md`.
* **A crew is a component.**  `World.spawn_unit(crew: true)` puts `ai_crew` on a hull
  plus its platform tag (`mech_ai` for a mech's crew, `foot_ai` for a man's; a
  carrier already carries `vehicle`), and a mission adds its own (`front_mission`,
  `raider`, `crew_engage`).  A pilot's machine and every fixture/puppet is built
  with `crew: false` and is out of every AI query by construction - there is no
  per-row `owner` test left anywhere.  `Systems/AI/` is one file per system (`run`),
  the shared columns and vocabulary in `Support/`, the mission state machines in
  `Strategies/`, and `Support/Pipeline.cs` is the block a mode registers
  (`battle()` / `range()` / `defence()`).
* **An AI crew fires the arms it has.** `Systems/AI/Engage*.cs` used to pull the
  right trigger only for `kind in ("rocket", "saclos")`; it fires any arm
  whose own `range` covers the target (still 2.2 km for those two, `ai_range`
  for everything else), and `weapons.run` gives a `lock` weapon on an
  ownerless hull the lock the crew already holds — the AI's `target`
  component — because there is no `pl.lock_target` to wait for. There is also a
  floor under the cycle: `defs.AI_LAUNCH_GAP` (15 s) for a lock round, or eight
  tubes are gone in four seconds and the machine is toothless for the rest of
  the match. SACLOS is not on that clock — its price is holding the sight.
* **A round's ending is one record, and one judge.**  Every round carries
  `first_hit_record` - `first_hit_t` (a fraction of this tick's leg,
  `Defs.FIRST_HIT_ARMED` = 2 while nothing has been hit), `first_hit_pos`,
  `first_hit_kind` (`Defs.HIT_UNIT` / `GROUND` / `BUILDING` / `TRUNK` / `FUSE` /
  `EXPIRE` / `VANISH`), `first_hit_unit`, `first_hit_mod` and the `first_hit_gap`
  a fuse measured.  Each collision system offers its own answer through
  `if (t < first_hit_t)` and `Systems/Impact.cs` is the one reader and the only
  thing that ends a round: so the order the collision systems run in decides the
  cost of a compare and nothing else.  The record is per tick - the round's own
  flight system re-arms it, which is the same moment it writes `proj_prev`:
  `(proj_prev, pos)` is the leg and the record is where along it the round stops.
  This is what makes "a fuse fuses at t=0.4 but the ground is at t=0.7" answer
  itself: before the record existed, `projectiles` destroyed the round on the
  ground and `proximity` never got to look at it.
* **A round's kind is a tag, and `proj_kind` is its `etype`.**  `Defs.KIND_TAGS`
  maps `kind` to the component (`bullet` / `unguided_missile` / `saclos` /
  `top_attack`), `World.spawn_round` builds the archetype from it once, and each
  flight system queries the kind it knows (`Flights.Ballistic` over `bullet`,
  ...) - so a new kind is a tag plus a system, never another branch in shared
  code.  `proj_kind` stays on the round as the wire byte and the `Defs.WEAPONS`
  key, exactly the role `etype` plays on a hull; `world.projectiles()` is still
  `query("proj_kind")`, i.e. the union of the four archetypes.
* **A round's lifetime is a contact at t=0.**  `Flights.Expire` counts `proj_age`
  and `proj_life` for every round before anything moves, and a round that runs out
  of battery or wire writes a contact at t=0 (`HIT_EXPIRE` when
  `wd.expire_burst`, `HIT_VANISH` otherwise) - that is the whole of "the round
  dies where it stands", and it is why a flight system's first line is
  `if (first_hit_t <= 1) continue`.  The same line in a collision system is a
  pure cost optimisation; these two are not the same thing.
* **The sight is one line, so the lead is solved for the slowest round.**
  `Systems/AI/Support/Engage.cs` picks the aim speed as the minimum `mean_speed` over
  the arms that reach the target, not the primary weapon's muzzle figure: a
  beam rider averages ~340 m/s at 1 km against the cannon's 1080, and leading
  it with the cannon's number put every missile tens of metres behind a moving
  hull (measured: 16 launches, no kills on movers; after: 3–4 kills per 16).
  `mean_speed` is the boost-and-coast profile off `vel` / `thrust` / `maxvel` /
  `burn`, so a new missile needs no entry in the AI.
* **A launcher of tubes is described once, in `defs.rack`.** `rack(cols, rows,
  count, bore=, length=, centre=, ...)` hands back a plain dict — the lattice,
  the tube diameter and length, the pitch, the mouth (`centre + length/2`) and
  the body it is drawn as — and `defs.rack_tube(fired, spec)` says which cell
  the next round leaves from (bottom row first, left to right). `weapons.fire`
  solves its launch point off those two and `scene.js`'s `rack()` draws the same
  lattice from the *same spec*, which travels to the client with the rest of the
  weapon table: a mouth that was drawn is a mouth a round leaves. The gun's own
  length wins first (`wd["reach"]`), then the rack's mouth, then the kind's
  figure in `MECH_ARM_REACH` — a racked weapon never needs an entry there.
  Ammunition is one round per tube: the rocket nest is 5 × 8 with 40 rockets in
  it, the top-attack rack is 2 × 4 with `mag=8` and the SACLOS rack 4 × 4 with
  `mag=16`, both `reserve=0, reload=0` — an arm fires its tubes and goes quiet
  until the pilot buys a fresh rack at the base, which is why all three share
  one mechanism instead of three.  The cells are packed at
  about 1.15 tube widths (the nest's 0.155 on a 0.135 bore), which is what makes
  a face read as a bundle of tubes; the body is a 0.04 m sheet-metal edge round
  the lattice and nothing more.  How a rack is *hung* is two fields, and the
  server, `scene.js` and the client's muzzle all read the same two: `elev` is
  what the tubes add to the angle of the arm they are on, and `bolted` means
  the arm's slewing does not move them at all.  So the top-attack rack is
  `bolted` at its 42° (the round's own loft, whatever the pilot's cross does),
  the SACLOS pack is carried at 0.18 rad *above* the arm's aim — a soft launch
  angle that rides the arm down as well as up — and the nest is mounted flat
  (`elev` 0), i.e. it simply is the arm.  Never clamp an elevation as a floor
  (`max(pitch, elev)`): a flat rack whose `elev` is 0 is then pinned to the
  horizon and can never bear on anything close, while its rounds — which leave
  along the arm — still hit, so the bug reads as "the pod won't depress" and
  nothing else.  A launcher's elevation is settled *before* the muzzle is
  solved, so the mouth drawn is the mouth fired from.  `bore` is the tube's
  outside diameter in **both** halves of the file:
  the client's hexagon scales as a width, so passing half a bore draws a tube a
  quarter of its area and leaves a hole's worth of empty box around it.
* **Grid** (`Core/Grid.cs`) is a 128 m cell index over every unit that moves *or* can
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
* **Raiders** (`Systems/AI/Strategies/Raid.cs`) are the one unit that plans.  Every
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
  `raider` tag is what takes them out of the lane policy; the guns stay the engage
  systems', and the plan reaches the machine as a route (`Nav.add`) plus `nav_speed`.
  Raiders die like any other hull - 58% of them in a measured round - which is
  why the plan dict is pruned when a party is gone.  (The Python tool that drew
  the gradient, the planned routes and where the hulls died went with the Python
  backend; `--check battle` still exercises a whole raid cycle.)
* **Modes** (`Modes/`) own everything that differs between game modes: the
  map, what a fresh round spawns, the mode's own systems and the rules the
  shared code asks about (`fixed_faction`, `start_money`, `rearm_anywhere`,
  `restart_respawn`, `mech_spawn`, `respawn_delay`). Adding a mode means adding
  a folder, never another `if` in a shared system. Battle runs `ai` + `waves`
  (capture and reinforcement waves); range runs `targets`, the missile post, and
  the shared AI block, whose queries only reach hulls that carry `ai_crew` - i.e. the
  ones a pilot dropped there with `debug_spawn`.
* **A death is planned from where it happened.** `World.kill` records
  `Player.death_pos` and asks the mode for `respawn_delay`; the mode's
  `mech_spawn` later turns that position into a place. Battle's version
  (`Modes/Battle/Init.cs`) picks the town with `waves.respawn_town`, which
  is three steps: **A** the faction's own town nearest the death (it only picks
  the lane, so a spawn may come from another lane), **B** the town on A's lane
  nearest the death that is still connected to the base *and* not contested,
  **C** one town back from B towards the base - C is the spawn, none means the
  base ring (`World.base_spawn`). Its wait interpolates from the distance to
  the enemy base: 40 s on your own doorstep, 3 s on theirs. The first deploy
  has no `death_pos` and lands on the most advanced connected town instead
  (`_lead_town`), because 5 km out of the gate is not a spawn point.
  `spawn_mech` clears `death_pos` once it has been spent.
* **A town being fought over supplies nothing.** `waves._capture` recomputes
  `town["contested"]` every tick - a unit of a faction other than the owner is
  inside `CAPTURE_RADIUS` (170 m) - and `waves.front_town` walks the connected
  chain backwards past every contested town, so the column comes out of the
  next depot down the lane (or not at all). Ownership still decides the chain;
  `contested` only decides who gets to raise troops.
* **Players** are plain Python objects (`world.Player`) attached to a mech
  entity through `owner`; `pl.mech` is -1 while dead. Income and respawns live
  in `Economy.cs`; wreck/corpse cleanup in `Debris.cs`.
* **Proximity fuse** (`FirstHit.Fuse`) — the 30mm round bursts on
  *personnel*: `defs.WEAPONS[...]["prox"]` is a fuse radius in metres (absent =
  impact fuse only), and `person` is the tag a man carries (riflemen, AT
  riflemen, civilians). Hulls are not tagged, so the fuse ignores them. A round
  covers 36 m per tick, so the fuse solves the leg it *flew* this tick —
  `proj_prev` (written by its flight system) to `pos` — takes the closest
  approach to an enemy man and, if that is inside the radius, writes the burst
  point into the round's `first_hit_record`; `impact` then detonates there.
  Never sample the tick boundaries instead: that steps clean over a man standing
  4 m off the path.
  Airburst is blast only (it is not a direct hit), and the burst is logged at
  DEBUG (`mech.prox: prox fuse: ...`, with the leg it flew and the closest
  approach) so a fuse hunt can read the burst position out of the log —
  `MECH_LOG_LEVEL=DEBUG`, see "Logging" under Build.  It fires once per round
  that fuses, so it is never an INFO line.
* **A missile is a target, and the ECS says so** (`Components.MISSILE_COMPS`).
  `World.spawn_round` - the one place a round's archetype is built - picks the
  pool off `defs.is_interceptable(wid)`, the tag off `Defs.KIND_TAGS[wd.kind]`,
  and the seeker's memory (`SEEKER_COMPS`) only for the kind that has a seeker: a
  `kind` `rocket`/`saclos`/`top` round is spawned with `interceptable` + `hp` +
  `hpmax` + `armor` + `last_hit` (10 points, 1/1 of plate), a bullet keeps
  `PROJ_COMPS` and pays nothing.  A guided round also carries the seeker's
  memory (`tgt_mem` / `tgt_vel`, `Core/Seeker.cs`) - see the seeker bullet below.
  The tag is what puts a round in the
  hit-sphere table and in the fuse's target set, so `combat.hit`/`splash`/
  `apply` work on it with no special case, and `systems/mortality.py::
  _kill_round` is the second death channel: a killed missile leaves **nothing** -
  no warhead, no wreck, no credit (a SACLOS that still went off 300 m out would
  make shooting it down pointless).
* **The hit-sphere table carries motion** (`Sweep.sphere_table(world, dt)`).
  Rows are `(centres, radii, ids, modules, factions, blocks, cull, prev, disp)`
  and the sweep solves the *relative* leg: `rel = prev - p0`, `w = seg - disp`,
  `t* = clamp(rel·w/|w|²)`, `d_min = |rel - w t*|`.  A missile covers 20 m a tick
  against a 2.5 m fuse and a 0.08 m ball, so a solve against a standing target is
  decided by the phase of the clock (measured: the head-on crossing case is 6 m
  off at the end of the leg, i.e. a clean miss).  The two blocks are gathered at
  different moments and each says so: a **unit** has already been moved by
  `movement` (it runs earlier), so its `pos` is the end of its tick and
  `prev = pos - vel*dt`; a **missile** has not moved yet (the flight systems move
  it), so its `pos` is the start and `disp` is predicted from its velocity.  That
  is why `Sweep.Spheres` is a system with a fixed place in the tick: after
  `movement`, before the flights.  The
  broad-phase box grows by the fastest row's displacement (`reach`), or a
  missile crossing into a round's leg is never a candidate.
  **One table per tick is the shape, but not the count.**  `world.update` used to
  throw the table away every tick, so `weapons` (which fires before `movement`)
  had to rebuild every hull's spheres *at its own stage* to solve an aim; now
  `world.aim_spheres()` is the aiming door: it hands back the table it has plus
  `row_ok` (those ids are still live hulls owning their ball, and their `pos` is
  within 10 m of the centre in the table), and only builds one if there is none
  for this tick — marking it `_sph_premove`, which forces `sphere_table` to
  rebuild before the sweep.  A table built before `movement` is *not* the one the
  sweep may use; that flag is the whole of the distinction.
* **The geometry is batched, the bodies are not.**  The flight systems fly every
  round first and publish the leg each one covered (`world.sweep`), then
  `FirstHit.Ground` asks the terrain once for the whole tick's legs
  (`terrain.ground_hits_many`) - a leg is 36 m of terrain sampling and there are
  dozens of them.  Each leg is then *judged* one round at a time, and the pass
  with the side effects (a burst, the damage, the dead round) is `Impact`, a
  single system whose order against the gatherers carries no meaning.  What is
  *not* worth batching is the integration itself — a few batched loops over one
  or two rows cost more than the scalar arithmetic they replace (measured,
  twice).
* **A leg is queried at the granularity of each shape it can meet.**  The ground
  is a continuous field and is sampled every 9 m; a building is a whole tile and
  is solved as a slab from the terrain to its roof (`walls`); a trunk is a
  metre-wide cylinder and is solved as one; a unit is a sphere.  `FirstHit.Ground`
  / `Structures` / `Units` are those three queries, and `Terrain.structure_hit`
  is the door the second one goes through - a tile with no structure in it costs
  one byte test, which is why the structure pass is cheap out in the field.
* **The fuse settles the round it fused on.**  `FirstHit.Fuse` fuses on
  `person` ∪ `interceptable` and bursts at the round's position at `t*`; a
  missile found there takes `combat.splash` **immediately**, at the gap it was
  at when the fuse fired, and is handed to the burst as `skip`.  `blasts` runs a
  stage later against positions gathered for the whole tick, by which time a
  600 m/s round is twenty metres out of a four metre radius - settling it
  directly is what turns "did the blast catch it" into an exact answer.  The
  numbers are unchanged: blast only, at the true gap (17 at contact, 11.4 at
  the 2.5 m trigger, against 10 hp) - so a fuse that **triggers** at all is a
  **kill**, with 14 % of margin.  It used to be a 4 m trigger against a 2.67 m
  lethal radius (8 at the edge, the round flew on): the trigger now sits
  *inside* the pattern, and the tighter circle is paid for by the rate of fire
  (300 rpm) - measured, `tools/intercept_check.py` read 34 / 36 either way before
  the mech's radar went to 1.75e14, and 36 / 36 after it (the crew sees the round
  368 m earlier).
* **Radar is one equation, two implementations** (`defs.radar_k`): a doppler
  receiver only sees what closes on it, so `radar_strength * rcs / d^4 *
  (v_c / RADAR_V_REF)^2 >= 1`, i.e. `d² <= K·v_c` with `K = sqrt(S·rcs)/V_REF`.
  `V_REF` (600 m/s, `defs.RADAR_V_REF`) only makes the speed term dimensionless;
  `radar_strength` (m⁴, on the unit type: mech 1.75e14, APC 3.125e12, nobody
  else) is the one knob, and `d ∝ S^¼` is why an APC sees 1/2.7 as far.
  `v_c <= 0` fails the test by itself, which is why there is no speed threshold
  any more (the client's old `RADAR_SPEED` is gone).  Discovery distances
  against a mech, each round at the speed it arrives at: SACLOS 1368 m, S-8
  975 m, top-attack 977 m (measured 778 m on a 2.2 km launch, because it is
  slower and steeper by the time it is that close), RPG 544 m.  The mech's
  number is tuned to the top attack and to a *time*: the round is 778 m out and
  closing at 195 m/s exactly 4 s before it goes off, so `K >= 778²/195 = 3100`,
  i.e. `S >= 1.73e14` - four seconds being the window in which a pilot can lay
  a 30 mm burst inside the fuse's 0.26 deg cone at that range.  Both sides read the
  same numbers: `netcode.static_payload` sends a `radar` block (`v_ref`, the
  largest `rcs`).  The dial's **face is a constant 3 km** (`RWR_RANGE` in
  `game.js`), the same in every machine, so a mark's radius reads as metres - and
  it is the snapshot's guided tier (`TIER_ROUND`), so every mark on the dial is a
  round the wire would have carried.  `radarRing` (the hull's own discovery
  range, 1368 m for a mech) no longer sets the scale - it decides whether a
  contact has a *position* at all.  Every gate is **gated on line of sight**
  (`map.sightBlocked`: terrain, canopies, smoke - the same door the AI's lookout,
  the IR lock and the pre-aim box go through), for the bearing-only contacts and
  for laser designators both, because without it every missile on a 16 km map
  that merely closed on the hull rang the alarm: measured, 29 % of hull-frames
  were ringing at a median threat range of 5.5 km, 0 % of them aimed at that
  hull.  Four marks, four answers: seen (a red **dot** at true bearing and range,
  plus a pre-aim box through the sight rule), heard (a 5 Hz red **line** from the
  centre to the rim - bearing, no range - drawn for *every* round the receiver
  holds, whatever its course), a laser inside 3 km (a red **triangle** at its
  true position with a dashed beam) and a laser further out (the same triangle
  **pinned to the rim**: the bearing is real, the range is "3 km or more").
  Five judgements, three outputs (`doc/product/rwr.md`):
  `heard` is `d² <= Λ²·K·v_c` with `Λ² = 10^(ΔL/20)` and ΔL = 12 dB
  (`ALARM_DB` / `ALARM_GAIN`), `seen` is `d² <= K·v_c`, `steady` is a horizontal
  bearing that is not drifting (`|θ̇| < (er + WARN_MARGIN)/K`, read over
  `STEADY_T` from the round's own `_bearTrack` history), and the geometry is
  **two rays out of the round with one tolerance**
  (`er + max(WARN_MARGIN, d_h·sin(WARN_CONE))`): `warn` along the *relative*
  velocity - the track this hull's own motion makes of it, i.e. a round aimed at
  a lead point - and `aim` along the round's own, i.e. where it is pointed right
  now.  `rayMiss` is the one helper both call.  One ray answers one guidance law
  and a homing round uses both in turn: pure pursuit outside `pn_r` points its
  nose at the hull (`aim`), terminal PN points at the lead and the track closes
  (`warn`).  `seen ? (warn || aim)` is not redundant with adding `steady` back:
  inside `seen`, `steady` implies `warn` (the threshold is anchored at the
  `seen` boundary), so the two judgements there say the same thing - measured on
  the range, a 1500 m top attack against a hull crossing at 50 m/s was rung at
  **890 m** by `aim` against **240 m** by `warn` alone, 0.4 s before a hit that
  landed.  A hull standing still has `warn == aim`, so every tuning run needs a
  crossing one.  `rayMiss` measures along the *forward* part of the ray, so a
  round that has gone past is judged by the gap to itself and stops ringing.
  `v_c` is saturated at `v_ref` so the alarm ceiling is a constant 2728 m and can
  never cross `RWR_RANGE`.  And shot
  down (`intercept` fx: a few sparks for everyone who can see it, and no cue of
  its own - the event still carries `pid` (the crew that fired the burst) and
  `firer` (whose round it was) so a script can read them, but a chime per round
  was the busiest sound in the game, since the battle's AI crews shoot missiles
  down all over the map).  **One boolean, two channels**: `_threats` computes
  `radarAlarm = some(t => t.alarm)` where `t.alarm` is
  `seen ? (warn || aim) : steady` written per round, and both the rim
  (`hud.js`) and the trill (`_alarms`) read exactly that - which is why they can
  count the same beats (`RWR_TONE_S`).  A round the receiver can place is judged
  on its geometry, one it only hears on its bearing drift; `seen` is re-judged
  every frame, so a round that slows down or turns hands judgement straight back
  to `steady`.  That fallback is **information**, not noise: no latch, no
  hysteresis, no smoothing, because a pilot learns to read "the trill broke" as
  "it is turning".  Every geometric judgement here is **horizontal** - a
  top-attack round climbs to 950 m, glides and dives, so its vertical profile
  says "not for me" until it is overhead while its ground track never does.
  The gates upstream of all of it are about the receiver *holding* the round at
  all - `min(2728, RWR_RANGE)` and a clear sight line.  Do not drop the sight
  gate to "make the alarm honest": a battle is 16 km of houses, tree lines and
  smoke, and the RWR that sang through all of it rang 29 % of hull-frames at a
  median 5.5 km with none of them aimed at that hull.
* **The dial also points, in three dimensions** (`game.js::_threatArrows`,
  `renderer.js::screenArrow`).  Beside the dial sits a second circle of the same
  radius (`hud.arrowRingAt`, 16 px of gap, level with it), and the nearest
  `RWR_ARROWS` (3) rounds the alarm is raised on (the same `t.alarm` the rim
  and the trill read) each get an arrow in it.
  Every arrow springs from the ring's **centre**, so three bearings read as three
  needles of one compass.  The arrow's *heading* is that round's direction **in
  the camera's frame** - right, down, into the glass, from `viewYaw` and the
  camera's own pitch - so turning the sight that way puts the round on the cross.
  That frame is the point: the dial is hull-frame (up is the hull), and the sight
  and the hull disagree exactly when a pilot is craning round for an incoming
  round.  Two real instanced pieces - a cylinder shaft and a cone head - so a
  round on the crosshair shows the **back** of the head (a white cone base round a
  red shaft cap), a round to one side shows the arrow side-on, and a round behind
  comes off the glass at the pilot.  It is drawn by the renderer's **screen-space
  overlay** (`screenArrow` / `_drawArrows`): the world's own meshes, buffers and
  shader, with `_ovlMat` in place of the camera's view-projection.  Three things
  in that pass are load bearing, and each was a measurement:
  * the perspective divides about the **ring's centre**, which is also every
    arrow's tail: an arrow parked off to one side smears diagonally the moment it
    has any depth to it, which reads as a sheared layer rather than a turned
    arrow. One matrix for the whole overlay, so three arrows are **one pass** -
    two `_submit` calls, one per mesh, however many arrows there are;
  * the depth buffer is **cleared and tested** for the pass, with `uFar` = 2 so
    the mesh shader's log depth becomes a plain `log2(w)`: pieces live at
    w ∈ [1, 1.1] and overlap each other (a cone's base pokes through its own
    sides when it is seen end-on), so painter's order alone draws a mess;
  * the light is the **world's sun folded into the same frame** by `overlaySun`,
    so an arrow is lit by the sun the machine under it is lit by; `uOutline`
    doubles as the piece selector (1 cylinder, 2 cone) because the shader paints
    that cone's base cap white.
  `hud.js` owns the layout (`dialAt`, `arrowRingAt`, `ARROW_R`): the ring is the
  strip the gear vacated when it moved into the screen's corner, and the message
  stack stops 10 px short of it.  With a round inbound the rim **blinks** - pure
  red against a washed-out red, one alternation per tone of the missile trill
  (`audio.RWR_TONE_S`, 0.083 s), so the eye and the ear count the same beats -
  and the band it is drawn with (`RING_ALARM_W`, 12.8 px) grows *inwards*:
  the stroke is centred half a width inside the radius, which keeps the ring's
  outer edge - and so its size next to the dial - where it was.
* **AI missile defence** (`Systems/AI/Intercept.cs` + the platform's `Engage*.cs`)
  runs in the AI block before the ground fight and preempts it: a crew that has a round
  coming for it lays the sight on the lead point (`mu.solve_lead`, which honours
  the target's vertical velocity - a climbing ATGM is not a hull) and pulls the
  trigger only when the *bore* is within `atan((prox + hit_r)/d)` (0.15° at
  1 km, not the 0.14 rad a hull gets).  Only a fused arm fires (`PROX[wid] > 0`,
  today just the 30 mm), so a 12.7 mm carrier stays out of it - data driven, not
  a type switch.  That gate is the *AI's* rule only: a missile is a 10 hp / 1
  armour target like any other, so **any** round landing on one kills it
  (one 12.7 mm bullet is `AD 10`), which is why a pilot in an APC can and does
  shoot an incoming round down with the gatling while the AI crew ignores it -
  the fuse buys margin (2.5 m trigger, 11.4 against 10 hp), not the ability.  "Coming for me" is `seeker target == me` for a top-attack (it
  climbs 150 m and dives, so its straight line misses) - or, once that lock is
  cut, a stare point inside `CPA_MEM` (100 m) of the hull, because a blinded
  round still flies at its memory and can take the hull back - and closest
  approach < `CPA_HIT` (40 m) for everything else; a `t_cpa` gate would say
  nothing (a round is 1.1-2.6 s from its closest approach the moment it is
  first seen).
  `Intercept.run` decides only *which* round; laying the gun on it is the platform's
  engage system, so a crew has exactly one trigger writer (`ai_cover` is the byte
  that tells it which job this tick's gun has).  `Pipeline.defence()` is that block
  on its own, for a mode whose hulls do not fight.
* **The range has a missile-defence post** (`Modes/Range/Interceptor.cs`): one
  blue AI mech at `(1550, 2250)` with a 30 mm only - a crew with a radar and no
  `crew_engage`, so it lays its intercept and never picks a ground fight, and it
  comes back 5 s after being destroyed.  `--check intercept` fires every
  missile type at it and prints the outcome per round, read out of the server
  log (`missile N took X (prox Y m) - hp A -> B`, `intercept: ... round N shot
  down at ...` - both DEBUG, the tool raises those loggers itself).  Measured:
  34 of 36 rounds fused and shot down (two got through).
* **A guided round flies an airframe, it does not follow a curve** (`Core/Flight.cs`).
  The top-attack missile is a point mass with an angle of attack: guidance demands
  a lateral acceleration, `CL = m·a/(qS)` capped at `cl_max` is what the airframe
  can make of it, the nose leads the velocity by `α = CL/cl_α`, and the drag polar
  `CD = cd0 + k·CL²` charges for it (squared and unsigned, so a pushover costs what
  a pull-up costs). Thrust is along the nose, lift square to the velocity, drag
  against it, gravity always - so the round climbs on the motor, glides for
  nothing, and buys a hard pull with speed.  Guidance is three phases over one
  reference: a parabola across the launch range whose climb equals the
  `loft` quadrant (42°, the tube's own elevation) and whose far half *is* the dive,
  so the round arrives at the attack angle instead of turning into it; inside
  `pn_r` and above `pn_v` it is true proportional navigation on the roof point.
  Horizontally it is a pure pursuit everywhere - a lead only in the last 300 m.
  The airframe is a pure function of `(pos, vel, proj_age, proj_trav, stare)`,
  where `stare` is a *point and a velocity*, not a hull id - so a stale stare
  point is guided exactly like a live one and the airframe still carries no
  state.  The two components that are state (`tgt_mem` / `tgt_vel`) belong to the
  seeker and never change the wire.
  The round's archetype is `SEEKER_COMPS` (`MISSILE_COMPS` plus that memory); its
  `life` is a **battery**: at zero the record gets a contact at t=0 and the
  warhead goes off where the round happens to be, which is what puts the range
  ceiling at ~4.3 km, not the motor.  (The headless envelope
  harness - `top_attack_trace.py` - went with the Python backend; the range's
  own `MECH_RECORD` trace and `--check range` still fly the same model.)
* **The seeker remembers, and the guidance does not care** (`Core/Seeker.cs`).  A
  top-attack round's lock is not a hull id, it is a *point* (`tgt_mem`, the
  deck at `defs.AIM_ROOF` = 92 % of the hull's height) and the velocity that
  hull had the last time it was seen (`tgt_vel`, copied from the hull's `vel` -
  the velocity it is really moving at, never the command, or a pilot's nudge
  would drag the round after a 50 m/s phantom).  While the sight line is
  clear both are refreshed every tick off the live hull; when it is cut - smoke,
  a ridge, a canopy, the hull dying - the point keeps walking at that last
  velocity and `flight.step` is handed it as if nothing had happened (which is
  why it is `step(p, v, age, trav, stare, stare_vel, wd, dt)` and no longer
  takes a hull height).  Re-acquisition is then one question per tick:
  `seeker.acquire` looks for an enemy `lockable` hull within
  `defs.SEEKER_CONE` (3°) of the stare *line* - measured from the round's own
  eye, on the deck it would be dived on, nearest to the line first - and takes
  the first one with a clear sight line.  It does not care whether that is the
  hull it was fired at, nor whether it is a mech or an APC: friend or foe is
  the whole of the test.  `weapons.fire` writes the memory at the shot, so a
  lock cut before the first tick of flight still leaves something to fly at; a
  round with no memory at all (a lock weapon cannot be fired without one) is
  still a rock.  Both events are logged (`mech.smoke`, at DEBUG): the loss,
  which the range's smoke test reads (`MECH_LOG_LEVEL=DEBUG`), and
  `re-locked on hull N`, which nothing but a reader of the log sees - the
  pilot is not told that his round changed its mind, and the client shows no
  cue at all.  Numbered in `doc/top_attack_relock.md`.
* **Damage** is two halves, never one number. `defs.pen_damage(ap, ad, dp)`:
  beating the target's `dp` outright delivers the full `ad`, beating only half
  of it shaves `ad/5`, anything duller does nothing. `defs.blast_damage(ed, de,
  er, dist)` falls off linearly to `er` and always keeps `max(e - de, e/5)`. A
  round *landing* on a unit takes `combat.hit()` (both halves); a unit merely
  near the burst takes `combat.splash()` (blast only - a charge going off a few
  metres away cannot punch a plate). `combat.apply()` is the raw hp subtractor
  and is what debug knobs use. It is also **the only writer of `hp`** (plus
  `shop`'s repair, which restores it), and it decides nothing: no death, no
  destruction, in the heat of a hit. It does record **who** is owed the last
  point it took (`last_hit`, the killing blow included), because that is the
  pilot `mortality` credits a hull's kill money to and addresses a missile's
  shot-down cue to - a hit that kills is exactly the one that has to be written
  down.
* **Damage is not a system.** `Core/Combat.cs` is a library next to
  `Core/Ballistics.cs` and `Core/Shop.cs`: a round writes hp where it lands,
  inside the system that fired
  it. There is deliberately no damage queue - a system owns state, and damage
  owns none. The consequence to remember: **hp a system reads is the hp at its
  own stage**, and a hull at 0 is still in the record until `mortality` runs
  (last of the damage chain), which is why `combat.apply` also zeroes `vel` and
  `vel_cmd` - a hull at 0 stops where it is instead of driving on for a stage.
* **Death has one judge.** `Mortality.cs` asks one question for every
  unit, `hp <= 0`, and then follows the type's own recipe from
  `defs.UNIT_DEFS[...]["death"]`: `blast=(ed, er)` burst, `loud` for the cue,
  `wreck=(etype, seconds, wire kind)`, `ends_match` for the base. The three
  match rules live there too (cross-faction kill money, the respawn plan from
  where it died, victory) - they are rules, not unit properties. `kill()` is
  gone; so is `DEATH_BLAST_ETYPES` and the four-way `_leave_debris`.
* **A kill is announced, but only to somebody who has a stake in it.**
  `mortality._feed` writes the one line the HUD's message list shows -
  `killer killed victim with weapon`, the money appended for the pilot who
  earned it. It stays silent for infantry (a battle kills them by the dozen)
  and for AI fighting AI (most of the war, none of a pilot's business), so a
  line only ever appears when a *pilot* is on one end of it - his kill, or his
  death; every player sees every line. "The pilot is credited" is decided at
  the **muzzle**, not at the impact: `proj_pid` / `proj_name` / `proj_etype`
  are frozen onto the round when it leaves (`weapons.fire`), the same rule as
  `blast_pid`, because a round outlives the hull that fired it by a second to
  several and by then its id can belong to the wreck it left. Names come from a
  component, never a type switch: `name_id` is an index into `World.names`
  (index 0 = nameless, and a car or a man answers to its `etype` name), an AI
  mech draws one off `defs.AI_NAMES` at spawn and a pilot's hull carries his
  callsign; `[bot]` is appended to the ones no pilot owns. **Who killed it is
  frozen at the hit, not looked up at the death** - `combat.apply` records
  `last_name` / `last_fac` / `last_etype` / `last_wid` next to `last_hit`, and
  `blasts.spawn` freezes the same three onto the burst (`blast_name` /
  `blast_etype` / `blast_wid`), because the shooter can be a wreck with its id
  handed out again by the time `mortality` judges the kill. The client only
  paints the words: `game._killfeed` builds segments (`{text, fac, bold}`) and
  `hud._messages` resolves `fac` through `FACTION_COLORS` and draws a dark
  plate under the line, since 12px monospace in three colours is unreadable
  straight off a bright cloud.
* **An explosion is an entity** (`Blasts.cs`). Whoever wants one - a
  round landing, a proximity fuse, a wreck - only calls `blasts.spawn`, and the
  one implementation of area damage reads it a stage later: `blast_power` =
  `[ed, er]`, `blast_t` the fuse, `blast_friendly` (a weapon burst spares its
  own side, a wreck does not), `blast_skip` the hull already hit directly,
  `blast_pid` the credit frozen at creation. A hull that dies this tick leaves
  a burst that goes off on the next one, so **a chain reaction is a tick apart
  by construction** and cannot damage the hulk the dead hull has already
  become. `blast_t > dt` would be a burning area; nothing uses that yet.
* **`hp` is one number, and `hp <= 0` is the whole of "dead".** A hull built out
  of separately damaged modules declares `arms=(left, right)` in its defs and
  the factory gives it `arm_hp` / `arm_hpmax` (a mech: hull 120, one arm 50
  each). Every system tests *the component*, never the type - `combat.arm_alive`
  is `arm_hp[eid, arm] > 0`, and the arm index is the weapon index. Three rules
  live in `combat._mech_hit` and nowhere else: a hit on an arm stays off the
  hull, an arm at 0 loses its ammunition, and a round into an arm that is
  already gone goes inwards. `MOD_TORSO` is hit geometry only - it charges the
  hull, and column 1 of the wire `hp` is always 0.
* **Debris** — `Mortality.cs` leaves an inert entity behind: a corpse
  (`ETYPE_CORPSE`, 5 s) for infantry, a smoking hulk (`ETYPE_DEBRIS`, 10 s,
  original model in snapshot `flags` bits 4-5) for vehicles and mechs. They are
  spawned without the `unit` tag, so `world.units()` (rounds, damage, AI,
  capture) never sees them; only `world.all_units()` (the snapshot) does.
* **Smoke** (`Smoke.cs`) — a pull throws a lattice of `canister`
  entities that closes the whole front hemisphere (14 of them, 50 m out); each
  one trades its tag for `smoke` + `smoke_r` + `smoke_t`, a 45 m sphere that
  bursts open fast and then creeps (`_radii(age)`) for its 5 s life. The puffs
  are drawn **opaque** (solid pass, `renderer.smoke`), so a screen really hides
  what is behind it and costs nothing to blend. It has no
  `unit`/`faction`/`etype`, so nothing can target or hit it. What it does do is
  **hide**, and it hides through the same door a tree canopy does: `systems/
  `Systems/Sight.cs` asks `Smoke.blocks(world, a, b)` for it, so the IR lock, the AI's
  lookout and a missile's seeker all lose the hull behind a screen - exactly as
  they lose one behind a stand of trees. It travels in its own snapshot block.
* **Lanes** — `world.lanes` counts from the red base, `world.lanes_rev` from
  the blue base. Every per-faction lookup (spawn point, "front line") must use the
  faction's own copy; mixing them mirrors positions across the map.  Each faction's
  copy of each lane is registered as a **route** (`World.register_routes`,
  `Core/Nav.cs`) with the enemy command base appended, so a hull spawned with a lane
  starts out following it and "the lane has run out" is simply its last point.
  `path_i` is the **driver's cursor** into that route (`Nav.advance` drops the way
  points it has passed).  The lane is a plain polyline: **the map does not round its
  corners**, because a wheeled hull cannot follow a right angle whatever the map does
  with it — which is the vehicle driver's problem, not a crew's (`Nav.passed`).
* **Wheeled hulls** — an APC carries the `vehicle` tag, and that tag changes
  both halves of its movement. Locomotion (`movement._vehicle`) is a rear-axle
  bicycle model: `leg_yaw` *is* the hull state (mirrored into `yaw` for the
  client), `wheel_speed` the signed speed rolling along it, `backout_t` /
  `blocked_t` the crew's own timers - three scalars, not a packed vector, because
  `veh[1]` never told anybody it was the back-out clock. The hull rolls only along its own axis and turns at
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
  The interface is an *intent* — the crew says which route and how fast
  (`nav_route` / `nav_speed`) and `Systems/Movement/Vehicle.cs` drives it, which is
  why it owns the way point maths too (`Core/Nav.cs`):
  a way point inside the acceptance radius counts as reached and is dropped, so
  the hull is already aiming at the next one while still short of the last — the
  standard acceptance radius of a way point tracker, and what lets a car take a
  dense line of sharp corners at speed by cutting across the inside of them (it
  will happily leave the road doing it; the road is not the constraint). The
  radius **grows with the speed** (`Nav.arrive_radius`: `clamp(speed * 1.1 s,
  10 m, 30 m)`) — a hull at 100 km/h needs tens of metres of arc to turn at all,
  while a slow one can and should hold the line tightly. A point it
  cut *wide* of is dropped too, once the hull is past the line through it
  square to the incoming leg (`Nav.passed`) — otherwise a car that missed the
  circle turns back for it for ever. Speed comes from the corners ahead within
  `APC_CORNER_HORIZON` (`Nav.route_speed`). A corner's speed comes from the **cut budget**
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
  the AI's to turn for a vehicle.  (The trace tool went with the Python backend.)
  range and draws the trace if you want to see the result.
* **Terrain** (`Core/Terrain.cs`) — 1000x1000 tiles of 16 m, heights on 4 m steps.
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
  `_march` is the sight walk (`los`, i.e. the eye of every system that hides
  something) and it is the hot loop of the whole simulation: **87.6 % of its
  calls are ground legs of 50 m or less**, so what pays off there is the cost of
  one sample, not the length of the ray.
  Keep it scalar and inline (`_spans` unrolled into the loop, `sample()` caching
  the previous tile's four corner heights, the tree test hoisted out of
  `_trunk_hit`/`_canopy_hit`).  Vectorising a *single* ray is a trap: five
  samples are cheaper inline than in a dozen small array operations (tried,
  measured 10× slower).  `ground_hits_many` is the other side of the same coin -
  many rays, one batch, ground only; a round's buildings and trunks are
  `structure_hit`, which walks the same tiles and asks each one.
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
* **There is one line of sight, and it is `Sight.cs`.** `sight.clear` /
  `sight.why` answer "does anything cut a->b" out of the two halves that hide
  things: the static field (`terrain.los` - ground, buildings up to the roof,
  tree canopies) and the smoke (`smoke.blocks` - deployed puffs, `why` returns
  "smoke"/"terrain" for the seeker's log). A tree hides what stands behind it
  for good, a puff hides it for five seconds and is gone: nothing else differs
  between them, so nothing pairs the two checks up on its own. Every eye goes
  through it - the AI's `_sees`, the player's IR lock (`Systems/Locking.cs`, which used
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
* **Netcode** — one init payload with the map and all definitions, then **one
  binary frame every tick**: a snapshot at 15 Hz (`SNAP_EVERY`) and the 16 B
  clock block on the ticks between (see the clock block bullet below), with the
  minimap block every `CONTACT_EVERY` ticks, plus JSON meta at 10 Hz
  (`META_EVERY`).
  Entity state is authoritative on the server; the client only interpolates.
  **A unit row is as wide as its kind is read for** (`Netcode.ROWS`): mech 74,
  apc 46, command base 30, wreck/bunker 25, infantry/civilian/corpse 20 B, and
  the header carries one count per class in that order (32 B: the 16 B clock
  block, then eight u16 counts).  It was one 80 B row for everything - a numpy
  column dump from
  before the archetype storage, where `arr[field] = columns(field)` wrote a
  whole column whether the entity owned the component or not, so a rifleman
  paid for a mech's arms, magazines and sight (56 of his 80 bytes empty, and
  `pad3` + `hp[1]` were never even written: `AllocateUninitializedArray` sent
  heap bytes).  Row widths now follow what `scene.js` / `hud.js` actually read:
  a mech keeps `syaw`/`spitch` (its own sight head is drawn on them) and
  `ammo`/`mag`/`weapon`/`arm_hp`, an APC keeps `turret`/`pitch`/`vel`/`target`
  (the turret, the lead rings, the laser warning), a base keeps `pitch`/
  `turret`/`hp[0]` (its gun and the integrity bar), a wreck keeps `flags` bits
  4-5 and `leg`, and an infantryman keeps a point and a heading.  The client
  resets every field its class does not carry before writing the ones it does,
  because entity ids are recycled and a rifleman must not inherit a mech's
  magazines.  In a mech row `yaw` / `pitch` are the *laid gun* (hull and arms),
  `syaw` / `spitch` where the crew is looking, and `vx` / `vy` / `vz` the
  velocity the hull is **actually** moving at (`vel`, not `vel_cmd`), which is
  what the client's lead boxes and speed readout use.  The client's
  own machine reads its pose from the snapshot like any other - overriding it
  with the view is exactly the teleporting hull this split removed - while the
  *camera* is the local view (an instant sight) sitting on the server-side
  eye.  `defs.MECH_SIGHT_Z` travels in the init payload (`mech.sight_z`) so
  `game._sightEye` mounts the eye where `mathutil.sight_eye` does.
* **The snapshot is a sphere around the pilot's eye, not the field**
  (`Netcode.SnapshotView`: `snapshot_bytes(world, view)`).  One frame
  per *connection* - the tier is per eye, so `Server.broadcast` builds one
  buffer per pilot instead of one per tick.  Radii are client constants read the
  other way round, so the table is not a taste:

  | radius | rows | the client constant behind it |
  | --- | --- | --- |
  | 6 km | mech, APC, the range's bunkers | `Defs.ZERO_RANGE` 4 km - the centre cross and the impact marker solve *against these rows* - and `game.LASER_RANGE` 3.4 km |
  | 6 km | smoke, canisters and puffs | `map.js sightBlocked` reads puffs out to the same 4 km |
  | 4 km | wrecks, shells | a hulk and a tracer are in the picture at the range a hull is |
  | 3 km | guided rounds (rocket/SACLOS/top) | the dial and the receiver's gate are both `game.RWR_RANGE` 3 km and it reads *these* rows, so the alarm ceiling (2728 m) has to sit inside 3 km or the wire simply stops carrying the round |
  | 3 km | infantry, civilians | they are 90 % of the field and invisible past a few hundred metres |
  | 2 km | corpses, anything else | |
  | always | the pilot's own hull, both command bases | see below |

  Two exemptions are load bearing, not politeness: **the pilot's own hull**
  (`view.mech`), because `game.ownEntity()` returning null sends the camera to
  the base orbit - i.e. a drop looks like a death - and **the command bases**,
  because the HUD's base-integrity bars scan the entity table for `type === 5`
  (`hud.js`), so a base at 2 km would blink out of the top bar for most of a
  match.  Two rows are 2.4 kB/s.  A dead pilot's eye is his base
  (`view.at`), which is where the client's camera is anyway.
  **Enter at R, leave at 1.15 R** (`TIER_DEAD_BAND`): `net.js` deletes every
  entity the newest frame does not mention, so without the band a hull parked on
  a circle is re-created (position snap, `mvx` smoothing reset) every crossing.
  The client needs **no change** for any of this - every consumer is map-driven
  or `.get()`-guarded; what changes is that distant rows are absent, so what
  pops is late, not earlier.  Measured
  (`--check net`, minutes 8..15 of one pilot): **78 kB/s mean** against 468
  unfiltered and 112 before the row widths were split (**67.8** once the fx rows
  and the clock blocks were in) - 45 of it snapshots
  (6.7 minimap block) at 53 unit rows of **36 B each**, 18 meta, 14 fx; `--check tiers` prices
  alternative tables on the same battle.
  Not tiered: the `fx` channel.  Explosions are *not* rows - there is no blast
  entity - and an event carries both the flash and the bang, so a visual radius
  is a change to `fx` (which today travels by sound level, 7-17 km), not to the
  snapshot.
* **A frame every tick, and a clock block is a frame** (`Netcode.CLOCK`,
  `Server.Client.observe`).  TCP delivers everything, so a slow link cannot lose
  a frame - it can only accumulate a queue, and every frame built on top of it
  is one more that has to crawl through before the picture updates.  A lower
  frame rate does not help either: the pipe is the pipe.  So the server stops:
  while a pilot is congested he gets the clock block **and nothing else** -
  snapshots, fx and meta are not built for him at all - until his own heartbeat
  says he is current again.

  The other half is that **the clock never depends on the picture**: every
  binary frame starts with the same 16 B **clock block** -
  `magic u32, tick u32, echo u32, tps_x100 u16, loss_x10000 u16`
  (`Netcode._clock`).  A snapshot is that block plus its eight u16 counts
  (`HEADER` = 32 B); a hold frame *is* the block (`Netcode.ping_bytes`,
  `MAGIC_PING`); the ticks between snapshots also carry the block alone, so a
  healthy connection gets **30 frames/s** (15 snapshots, 15 blocks) instead of
  15 and a clock that steps 1/15 s at a time.  Cost measured: 0.2 kB/s for the
  blocks on top of the 16 B the header grew - 67.4 -> 67.8 kB/s per pilot over
  minutes 8..15 (`--check net`), i.e. under 1 %.

  `tps` and `loss` ride **in the frame, not in `meta`**: both are per-pilot
  (loss is *his* skipped snapshots) and both are stale within a second, while
  meta is one JSON a second at 10 Hz for the panel.  `TPS` is counted off the
  wall clock in `game_loop` once a second, so a server that cannot keep up
  reports < 30 instead of claiming the nominal rate.

  ```
  behind_ms := (tick - ack) * 1000/TARGET_HZ                 (ack >= 0 only)
  floor_ms  := min(behind_ms, MAX_FLOOR_MS, floor_ms + rise/TARGET_HZ)
  slack     := max(HOLD_SLACK_MS 200, floor_ms * 0.75)
  held      := silent > 0.4 s, or behind_ms - floor_ms >= slack
  resumed   := behind_ms - floor_ms <= slack/2, and the send queue is empty
  ```

  **Congestion is queueing, not distance.**  The first version compared the
  absolute lag against 0.4 s and held every pilot more than 400 ms away, which
  is a latency measurement, not a queue: measured with the Node probe, 233 ms
  and 666 ms of one-way lag both read as "congested" and the picture dropped to
  clock blocks for nothing.  So `floor_ms` is this path's own best sample - it
  falls instantly (always take the better one), rises at `FLOOR_RISE_MS_S` (2
  ms/s, or 20 ms/s once a hold has lasted `HELD_PATIENCE_S` = 10 s, so a path
  that really got slower is let out instead of being held for ever), and is
  capped at `MAX_FLOOR_MS` (2 s) so one bogus sample cannot blind the detector.
  A pilot an ocean away sits at `extra = 0` and is never held; the same pilot
  with 0.5 s of queue in front of him is.  Measured after the fix: lag 7 (233
  ms) 15 snapshots/s and zero hold, lag 20 (666 ms) likewise, no-ack likewise;
  ack frozen -> one `held: 267 ms behind (floor 34, slack 200)` a tick after the
  threshold and 30 frames/s of clock blocks with `loss 100.00%`; a 3 s queue
  (`jump`) -> `held: 533 ms behind (floor 33, slack 200)` then `resumed after
  45 clock blocks` at the far end of it.

  The client reports the newest tick it has **seen** - snapshot or clock block -
  on its own 30 Hz input message (`st`, piggybacked on `in`; no extra traffic,
  and it *is* the heartbeat), `Client.ack` keeps it, `Client.heard` is when he
  last said anything at all.  A report from before the connection is not a
  distance and is dropped (`st >= Client.born_tick`): a fresh client says
  `st: 0` while the match is at tick 4000, which would put a 133 s "queue" into
  the floor the moment he joins.

  **RTT is measured on the echo, and half a tick comes back off it.**  The
  block's `echo` is the pilot's own `ack` sent straight back, so the client can
  time a round trip without either side owning a synchronised clock: `net.ack()`
  stamps `ackSentAt` the first moment a tick value goes out, and when that same
  value returns (`_clock`) the sample is `now - ackSentAt - 500/TICK_HZ`, EMA
  0.15.  The half tick is the only quantization in the loop: the server reads
  the ack as it arrives (the socket handler, not the tick) but can only echo it
  on its *next* frame, i.e. a wait uniform over one tick - half of it on
  average.  Nothing is subtracted for our own side: the stamp *is* the send.
  (The first version took a whole tick off, double-counting; on loopback the
  raw sample measures 21.6 ms mean against 16.7 for the wait, so a full tick
  read 0 for every fast link.)  The sample is smoothed signed and clamped where
  it is drawn, since a link faster than the bias reads slightly negative.  The
  HUD shows it unhalved (`PING` is RTT, not one way) next to
  `TPS` and `LOSS`, the last being the closed second's `skipped/due`
  (`Client.close_window`, `WINDOW` = 30 ticks) - so a hold reads 100 %.
  On the client side a frame whose magic is `MAGIC_PING` moves `net.tick` and
  leaves `entities` / `projectiles` / `smoke` / `contacts` exactly as they were.
  `net.held` is **not** "this frame is a clock block" - half of a healthy
  link's frames are - but "no snapshot for more than 4 ticks" (snapshots come
  every second tick), which is what the HUD colours orange.
  **No frame that was built is ever discarded** - the queue is never cleared,
  so a "skipped" frame is one that was never built, and the queue-full
  `dropped` counter stays at 0.  `ack < 0` (a client that reports nothing, or
  hasn't yet) is not congested - only silence can hold it.
* **The minimap has its own block in the same frame**
  (`Netcode.contacts_bytes`, `CONTACT_*`).  The map is the *strategic* picture -
  16 km of hulls at 210 px, no tier - so it cannot come from a sphere around one
  eye: one row per unit, `x` / `z` in 4 m units (u16, 16 km -> 4000), `faction`,
  `etype`, 6 B, appended after the smoke rows.  Its counts are the last four of
  the snapshot's eight u16 (`CLOCK + 10..14`), the fourth being the contact
  rows -
  `0` means "no new picture this frame" and the client keeps the last list, which
  is how the block rides every `CONTACT_EVERY` (8 ticks = 3.75 Hz, 1.8 kB a
  frame, 6.7 kB/s measured) without a second message type.  A pixel of that map
  is 76 m, so 3.75 Hz cannot be seen to step - 15 Hz would cost 36 kB/s for
  nothing.  It is one list for everybody, so `broadcast` builds it once and
  hands the same bytes to each pilot's frame.  `hud._minimap` and the range's
  `TARGETS` counter read `net.contacts`; the base-integrity bars stay on
  `entities` (they need `hp`, and the base is never tiered out).
* **Debug messages** are accepted only when `MECH_DEBUG=1` (see `Net/Server.cs`),
  except `respawn`, which is always allowed. All of them are sent from the
  console as `g.net.send({t: '...', ...})`; the payloads are in
  [Debug knobs](#debug-knobs) below.  This flag gates the *console*, not the
  log: log verbosity is `MECH_LOG_LEVEL` (see "Logging" under Build).

### Debug knobs

Everything here is one websocket message from the browser console
(`const g = window.__game`). The three that have a button live in the **gear
menu** (top right of the HUD, needs a free pointer: press `ESC`):
`RESPAWN`, `RESTART`, `GOD ON/OFF`.

| Message | Payload | Effect |
| --- | --- | --- |
| `respawn` | — | skips the death timer (3-40 s, by where the hull died) on the next tick (always allowed) |
| `debug_restart` | — | wipes the field and starts a fresh round, same map (`world.reset_match()`); info `MATCH RESTARTED` |
| `debug_god` | `on: true/false` | stops the AI / damage from interrupting a test; meta `god` mirrors it (the menu button reads that back) |
| `debug_spawn` | `etype` (default `3` APC), `dist` (m, default `800`), `angle` (rad, relative to your yaw), `faction` (default the enemy), `weapons` (optional list) | spawns a unit `dist` metres ahead |
| `debug_trees` | `dist` (m, default `200`), `angle` (rad, relative to your yaw), `radius` (tiles either side, default `2`), `forest` (default `true`) | grows a stand of trees `dist` metres ahead (`Terrain.plant`): the server repaints the tiles, rebuilds the cull grid and hands the same rectangle to the clients, which repaint their ground and props. Buildings, rocks and bases are left alone |
| `debug_missile` | `wid` (default `4` SACLOS), `dist` (m, default `2000`), `angle` (rad, relative to your yaw), `alt` (m above your ground, default `2`), `speed` (m/s, default the weapon's `maxvel`), `miss` (m of lateral offset, default 0) | puts one round in the air on a course for your sight eye.  The launcher is nobody (`src = -1`), so a SACLOS coasts on its heading instead of riding a crew's beam - one round, one closing speed, no AI.  A top attack gets `target = me`, so its seeker locks.  `miss` aims a fixed distance to one side: the "it is going to pass" case the receiver must stay quiet on.  An `alt` of a couple of metres flies the round into the ground before it arrives |
| `debug_tp` | `dist` (m, default `300`), `angle` (rad, relative to your yaw) | teleports the mech forward along its yaw |
| `debug_hurt` | `module` (`0` hull / `1` torso, which charges the hull pool / `2` left arm / `3` right arm, default `1`), `amount` (default `100` = a whole hull), `base` (damage to the *enemy* command base) | wounds the player or the enemy base |

Entity types for `debug_spawn` come from `Core/Defs.cs`: `1` rifleman, `2` AT
rifleman, `3` APC, `4` mech, `5` command base, `6` civilian, `9` bunker.

```js
g.net.send({ t: 'debug_spawn', etype: 3, dist: 400, angle: 0.3 });  // APC 400 m ahead
g.net.send({ t: 'debug_hurt', module: 2, amount: 999 });            // blow off the left arm
g.net.send({ t: 'debug_tp', dist: 1500 });                         // hop 1.5 km forward
g.net.send({ t: 'debug_trees', dist: 150, radius: 2 });            // a tree stand in between
g.net.send({ t: 'debug_missile', wid: 5, dist: 2000, alt: 40 });   // a top attack inbound
g.net.send({ t: 'debug_missile', wid: 4, dist: 1800, alt: 250, miss: 400 });  // one that passes by
g.net.send({ t: 'debug_god', on: true });                          // then confirm g.net.meta.god
```

### Client

* The lobby's callsign is the one setting that is not the audio bus's
  (`audio.js` keeps `mech.volume` / `mech.alarmvolume`); `game.js` keeps
  `mech.name` and writes it at `DEPLOY`, so a reload prefills the last callsign
  instead of `PILOT-<pid>`.
* `game.js` sends input at 30 Hz (`{t:"in",yaw,pitch,mx,mz,fl,fr}`) and drives
  the frame: interpolate snapshots, aim, build the draw list, draw, HUD.  The
  `yaw` / `pitch` on the wire are the *sight* (where the pilot looks), never the
  hull: `player_input` writes them into `sight_yaw` / `sight_pitch`.
* **Two headings, and the client keeps them apart.** `_aim` solves the centre
  cross, the range readout and the seeker ring from the view (`dirFrom(input)`,
  the sight line from `_sightEye`) and each arm's impact marker from the *bore*
  (`own.yaw`, `own.pitch`, `_muzzle`): mid-flick the second trails the first,
  which is the lag the pilot is meant to see.  A marker whose point falls behind
  the camera draws nothing at all - the old fallback dropped it on the centre of
  the screen, where it read as "on target" for exactly the moment it is not.
* **The eye reads the picture, the gun reads the wire.** `own.x/y/z` is the
  *drawn* pose: an EMA (`alpha = 1-exp(-dt*14)`, a 71 ms time constant) chasing a
  15 Hz snapshot, which is what the camera, the fx and the line of sight are
  built from.  The fire control solves from `_firePose(own)` instead -
  `own.px/py/pz` (the newest authority) walked on by its velocity for
  `_fireLag()` = half a snapshot interval + RTT/2 + half a tick (60 ms on
  loopback, hundreds on a bad link), which is when the server actually fires -
  and reads the target the same way (`e.px/pz` + its velocity; the round's own
  flight time is `leadPoint`'s job).  What moves is the *geometry*, never the
  pose: the muzzle's offset is still turned by the laid `own.yaw` / `own.pitch`,
  so an arm still climbing after the sight lags exactly as it did.  The picked
  ring is then solved again every input frame (`_ringSolve`) rather than read
  off the snapshot's answer, because the muzzle moves between the two;
  `aimSnap` holds the *pick*, and the ring's world point stays the picker the
  HUD draws.  Measured on the range's 105 mm before the split: 7 rounds at
  146-597 m, all within 0.2 m vertically (the zero and the launch elevation
  were never the question) and 0.3-4.6 m off laterally - 2 hits, one of them
  by 4 cm, against a torso of r=1.3 with a 0.35 m round half-width.  With the
  bearing alone corrected, all seven land inside the spheres.
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
  `FORWARD + p` when a pitch is already in play. A **box** scales on X = along
  the bore, **Y = up, Z = across** — `put('box', p, dist, len, height, width)`,
  and swapping the last two draws a tall pod lying on its side.
* **An arm that carries its own launcher draws nothing behind it.** A weapon with
  a `rack` (the rocket nest, the top-attack rack) *is* the arm: the plain station
  body and the 0.66 m shoulder ball are skipped for it, because they swallow the
  tube mouths at both ends — which is exactly why the first top-attack rack could
  not be seen from behind. `rack()` draws the body `defs.rack` derived plus one
  hexagon per cell, dark for a cell that has fired.
* **`at()` turns a piece's offset with the same yaw it turns the mesh with**,
  which is what everything bolted to a hull wants - and what the sight head must
  *not* do: its pivot is fixed to the hull, so the head has to rotate on the
  pillar, not orbit the machine. `headAt()` is that case - the pivot's place
  comes from the hull's `yaw` (`SIGHT_Z`, the same 0.62 m as
  `defs.MECH_SIGHT_Z`) and only the offset from the pivot follows `syaw` /
  `spitch`. Getting this wrong (passing the sight's angles to `at`) draws the
  head a metre and a half off its own pillar whenever the two disagree.
* **The pre-aim ring reads the target's acceleration off the wire**
  (`leadPoint`'s `ax` / `az`): nobody broadcasts throttle, so `net.js` measures
  it - the difference of two velocity samples over the ticks between them, EMA
  smoothed on a 0.25 s time constant, a jump over 25 m/s^2 treated as a
  teleport rather than a pull, and a sample older than half a second ignored.
  It is a straight-line extrapolation of what is often a bend, so the term only
  counts for the first second of the flight (`LEAD_ACCEL_S`) and is capped at
  12 m/s^2. Measured against the range's patrolling mech at 1.4 s: 1.35 -> 0.67 m
  mean, 6.35 -> 2.45 m on the turns. A constant-curvature arc was measured too
  and is *worse* (6.98 against 4.71) - a mech's pull rotates while it turns.
* **Aiming has two headings on the wire, and the assist is the switch between
  them.** `_sightAim` is what the client sends as `yaw` / `pitch`: the view
  (`input.yaw` / `pitch`) or, with the aim assist on and a pre-aim ring under
  the centre cross, that ring's own aim point (`aimSnap`). The camera and the
  centre cross keep reading `input.viewYaw`, so a snap never moves the pilot's
  head - it moves the sight, and the hull and the arms slew after it as usual.
  So the client carries **two crosses**: `×` at the middle of the screen (the
  view, `_centreCross`) and a white `+` at the projection of the aim point
  (`_aimCross`), drawn only while a snap holds. Every consumer of the sight -
  the range readout, the arms' markers, the guided rounds' marker - reads
  `_sightAim`, which is what keeps them consistent with what the server does.
  The rings are laid out and picked in **screen space** (`_leadLayout`): a
  target's two rings, when they would overlap at the assist's width, are laid
  out as a pair along the line their own two solutions make - each ring on its
  own arm's side of its middle, and no closer in than `LEAD_PAIR_SEP` radii
  (`hud.js`, 1.0), so the two **overlap** instead of standing tangent - and the
  direction is kept from the last frame while the two points are closer than a
  pixel - so the pair rides the true points and a fast target cannot make it
  swap arrangements between two frames. Overlap is what makes the pair handy:
  28 px out to the side is a long way to swing a cross to shoot one arm's lead,
  and two circles cut by one white line still read as two. The cut is
  `hud._ringCut`: each ring keeps only the arc outside its partner, and the
  chord between the two crossings - the perpendicular bisector of the centres,
  i.e. exactly the set of points they are equally far from - is drawn white
  once for the pair. That line *is* the picker's boundary: selection is the
  nearest hull first (`lead.dist`), then the nearer of its two centres, so the
  white line is where the cross hands over from one arm to the other. The
  pair's whole stadium is the catch area, because the point a pilot aims at -
  the hull's own box - lands round the middle of it. A
  laid-out ring is a **picker**, not a position: `mark.x` / `y` is where it is
  drawn, `mark.wx` / `wy` / `wz` is the aim point it stands for and the one the
  sight is snapped to, which is why the `+` sits between the two rings instead
  of in either of them. `_ringScreen` is what puts it there: a mark's own
  projection when that lands in the frame, and **clamped one ring radius inside
  the frame** when it does not - a lead is metres to hundreds of metres from the
  hull (a rocket pod at 2 km flies 4-5 s) and a zoomed sight is a narrow slice of
  the world, so the answer to "where do I point this arm" goes off the screen
  regularly; a mark dropped for being out of frame took the whole pair with it
  (one ring left, no white line, nothing saying which arm the survivor was).
  `null` only for a point behind the camera, where there is no place to put it.
  ALT (`input.freeYaw`, the cross is no longer the
  sight) and an open panel both skip the snap. Width is
  `aimAssist ? LEAD_RING * assistRing : LEAD_RING` (`_ringPx`), 2x-8x, default
  4x, remembered in `localStorage` beside the switch - the same `key` convention
  as `mech.volume`.  In game `V` throws that same switch
  (`input.onToggleAssist` -> `_toggleAssist`), so the pilot keeps the pointer and
  the cross is back under his hand without the panel; the message list says
  which way it went, and the panel's button reads the same field.
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
* **A bang is 12 bytes, and its words are JSON.** `fx` is two channels since
  the row work: the events that fire dozens of times a *second* - `shot` and
  `boom`, i.e. a round leaving and a round landing - ride a binary frame
  (`MAGIC_FX`, 6 B header + `FX_STRIDE` 24 B a row: kind, wid, id, `pos` and
  `r` as the float32s the world carries, flags for big/blast/small/arm and three
  zero pad bytes - **the wire does not round a float**, what it saves is JSON's
  key names), and everything
  rare - a kill line, a capture, an intercept, a seeker losing its lock, the
  tile paint - stays a dictionary in the JSON `fx` message.  Eight gatlings are
  240 rounds a second and *every* one of them bursts where it lands, so a
  gatling storm is ~480 events/s: **11.5 kB/s** as rows against **33 kB/s** as
  JSON, whose key names alone (`"t":"fx","k":"boom","x":…`) were 40 of every
  68 bytes.  Measured on a real 6-mech fight: 190 events/s, 2.41 kB/s, and the
  same batch serialised as JSON is 2.9x bigger; over minutes 8..15 of a battle
  the fx line went 13.9 -> **4.1 kB/s**.  `net.js` dispatches binary frames by
  magic (`MECH` snapshot, `MECF` fx, `MECP` ping) and rebuilds the events in the
  shape `game._fx` already speaks, so nothing downstream knows the difference.
  `hearable` runs *before* the split, so the per-ear filter is unchanged: it
  drops a `shot`/`boom` under `SOUND_FLOOR`, and the binary frame is built from
  what survived for that ear.
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
* **Distance is four effects, not one curve.** `audio.attenuation` is inverse
  square *compressed* (`SOUND_ROLLOFF` 0.6, reference `SOUND_REF_DIST` 25 m):
  true 1/r over 16 km is 84 dB of dynamic range, i.e. silence, so the shape of
  the near field is kept (a gun at 100 m is 7 dB below one at 20 m) and the far
  field stays audible (-32 dB at 12 km). `audio.airCutoff` is the second: air
  absorbs the top end far faster than the bottom (0.4 dB/km at 125 Hz, 30 dB/km
  at 4 kHz), 18 kHz inside 300 m down to a 1.4 kHz floor past ~6.5 km - a
  distant cannon is a dull thud, and *that*, not the level, is what the ear
  reads as distance. It shares one filter with the "behind you" lowpass in
  `audio._bus(g, pan, behind, dist)`. Third is the direct/reverberant split: the
  wet send is lifted by `reverbLift(dist)` so the diffuse field decays slower
  than the direct sound (wet is ~13 dB above dry at 8 km) - far away is "echo",
  not "turned down". `SOUND_LEVEL` (shot .6 / boom .7 / big .8 / blast 1) is how
  loud a cue is at the reference distance.
* **A level curve cannot re-arrange a spectrum, so the far field is its own
  voice.** `audio._farBoom` is the fourth effect and the reason a fight 10 km
  off is still audible: a slow 95->42 Hz sine plus a 340->120 Hz lowpassed edge,
  mixed on `(25/d)^0.25` (`FAR_ROLLOFF`, 11 dB across the whole map instead of
  34) and faded in from `FAR_NEAR` 700 m to `FAR_FULL` 2.6 km. It is played at
  `dist` 0 *on purpose* - it is the already-through-the-atmosphere sample, so
  the air lowpass of `_bus` must not touch it again. Weight per source: 105 mm
  1.6, 30 mm 1.0, guided launch 0.4 (a whoosh, not a thud), round impact 1.0-1.3,
  hull going up 1.2 with a longer tail. The near cues are untouched inside
  700 m. It gets **no reverb send** - it is the valley answering already, and a
  convolver input per voice is the most expensive thing a battle does.
* **The far field is a bed, and a bed is compressed, not summed.** `_farBoom`
  lands on `this.far` -> `farLim`, a bus compressor at `FAR_CEIL` (-20 dBFS),
  and it is rate limited to one voice per `FAR_MIN_GAP` (50 ms). A battle is
  ~90 sound events a second and every one of them is kilometres away, so mixed
  per event straight into `master` the layer had no ceiling: measured on a
  scripted 90 event/s fight it put the mix **8.5 dB** over the one it was added
  to and cost **2.1x realtime** of audio DSP against 0.96 - i.e. the audio
  thread could no longer keep up, which is heard as a crackle and then as a tab
  that has gone silent. With the bus it is 1.09x and 2.5 dB up (the bed it is
  meant to be). Weight a single far event normally, do not raise `FAR_LEVEL` to
  win a loudness argument: `FAR_CEIL` is where a bed settles, `FAR_LEVEL` is how
  loud one bang is.
* **There is no per-voice bass makeup, and it was measured out.** `bassLift` (a
  180 Hz lowshelf in `_bus`, up to +18 dB past 6 km) is gone. It bought a solo
  5 km shot nothing at all (peak 0.117 with it, 0.117 without - that shot *is*
  the far voice), while in a battle it summed per event to +4 dB of bed and,
  being one biquad on every voice, cost 0.84 -> 1.30 of realtime by itself. The
  body of a distant report comes from the far bus; a makeup curve belongs on a
  bed, which is compressed, not on each event, which is not.
* **The master chain ends in a soft clipper, because a compressor is not a
  limiter.** `master` -> `limiter` (12:1 over a 6 dB knee, threshold -12 dBFS,
  2 ms attack: a hard curve holds a dense mix down but flattens the cues worth
  hearing, so the ratio carries the bursts and the knee keeps one round's
  transient) -> `top` (`CLIP_GAIN`) -> `clip` (a `WaveShaper`, linear to
  `CLIP_KNEE` 0.7 and tanh to exactly 1, `oversample` 4x) -> destination. Before
  it, a single 105 mm round next to the pilot and a single hull exploding 300 m
  away both measured **over 1.0** at the destination - a hard 0 dBFS clamp is
  broadband fizz on every transient, which is what a "broken speaker" is.
  **The threshold is the number that decides whether the shaper ever works**:
  measured per cue (a 2048 sample window on the clipped output, 24 plays), -8 dB
  left a hull going up at 0.93 peak with **14 % of its samples past the 0.7
  knee** and a 105 mm beside the pilot at 0.93 / 9 % - continuous soft-clip
  distortion, the faint buzz under the battle. At -12 dB the same cues read
  0.78 / 0.4 % and 0.86 / 0.8 %, and their third-octave shape matches the
  unclipped one to 0.5 dB. `CLIP_GAIN` is a measured constant, not a taste: the
  DynamicsCompressor adds its own makeup gain, so 0.86 is what puts the chain
  back at unity. Check it with a constant cue (`denied` reads 0.094 at the
  destination) before changing either number.
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
  lock tone, `denied`) stay centred - they are not in the world. There is no
  hit/kill tone: taking a hit is the red rim flash and a kill is the kill feed
  line, both visual.
* **Every cue has its own voice.** `audio.boom()` is a round going off;
  `audio.explosion()` is a hull going up (`ev.blast`) - four layers (crack, fire,
  sub-bass thump, rumble) written because a dead mech is a different event, not a
  louder round. The mix can sum past 0 dB on purpose, so the master bus runs
  through a limiter (`audio.limiter`) instead of clamping at the destination:
  scale a cue's gains up rather than adding another layer elsewhere.
* **A voice is a temporary chain, and the mix has a ceiling.** A cue opens six
  to a dozen `AudioNode`s (a source, a filter, gains, a panner, a reverb send),
  and each one of them is torn down in the source's `onended` - `_bus` hands its
  nodes back and `_verbSend` its send gain, exactly so the voice that owns them
  can disconnect the lot. Left connected they wait for a collector, and a battle
  opens hundreds a second: measured on a 4 s gatling-and-boom load, the graph
  held **3492 nodes after the load and never let go**, against **1** once the
  teardown runs (the ceiling during the load is the voices still sounding).
  Two rules sit on top, both keyed on arriving **gain** rather than on who
  fired: `MAX_VOICES` 48 slots, held for as long as a voice sounds, drop the
  quiet cues once the mix is full while anything over `STEAL_VOL` (0.05) still
  gets in - measured, 240 quiet cues a second hold the graph at 48 slots and
  ~380 nodes for as long as the load lasts, then release it to 1. `_pumpSounds`
  also skips the events that cannot have arrived yet (`tMin`, the wavefront's
  earliest possible arrival) and drops the whole list when the pilot is dead - a
  dead ear does not need fifty seconds of a battle. The reverb has **one fixed
  input** (`audio.send`): a convolver whose input connections change hundreds of
  times a second recomputes its own mix on every change.
* **The shared noise bed is longer than any voice.** Six seconds, so nothing has
  to loop it (`src.loop = dur > 5.5`): a buffer that ends on one random sample
  and starts on another is a step in the waveform, and a hull's 4.5 s rumble or
  a 105 mm's tail is loud enough for that seam to be heard. Every envelope also
  lands **on zero**, linearly, instead of stopping at an exponential ramp's
  -62 dB floor - a battle makes hundreds of those little steps a second and
  they are the other half of "there is a faint buzz under everything".
* The **gear** takes the screen's corner itself (`hud._gear`, `GEAR_EDGE` 8 px
  from the right and top edges - the warning dial is round, so the square the
  corner leaves free costs nothing) and it is the only HUD click target: it
  needs a free pointer (`ESC`) and opens the settings panel (`hud._settings`),
  the master volume plus the debug buttons. `input.onHudClick` fires for canvas
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
cd frontend && npm install && npm run build      # -> assets/
./dev.sh build                                    # same, with a shared npm cache
```

* Webpack production build, output `assets/{bundle.js,index.html}`,
  gitignored. The server serves them at `/` and `/static/*`.
* Frontend changes need **only** a rebuild — the server reads the files per
  request, no restart.
* Backend changes need a **server restart** (`./dev.sh stop && ./dev.sh start`,
  ~8 s). `Netcode.STATIC` (map + definitions) is built once at startup - which
  also means the 16 km map costs ~0.7 s to generate on every start.
* Server: .NET 10 SDK, no third-party packages.  Always build through
  `./dotnet.sh` - a bare `dotnet build` dies on a read-only `$HOME` in this
  sandbox (`dotnet.sh` points the CLI home, the NuGet cache and
  `XDG_RUNTIME_DIR` at `.dotnet_tmp/`, which is gitignored and survives between
  sessions).
* No test suite, no linter. `./dotnet.sh build` must be clean (zero warnings -
  `Nullable` is on) and every change is verified by running the game and watching
  `/tmp/mech-server.log` plus the browser console, or by one of the `--check`
  harnesses above.
* **Logging: a weapon-level line is DEBUG, never INFO.** A round bursting
  (`mech.prox: prox fuse: ...`), a seeker losing or retaking its lock
  (`mech.smoke`), a missile taking a hit and being shot down
  (`mech.intercept`) all happen hundreds of times a minute in a live battle —
  measured, one 20 minute scripted round wrote 693 lines / 148 KB, 95 % of it
  the fuse line — so they are `log.debug` and a normal run writes none of it.
  INFO is for the rare, once-a-session events; DEBUG is where anything that
  fires per round, per hit, per lock or per tick belongs.  `Net/Server.cs` sets the
  level from `MECH_LOG_LEVEL` (default `INFO`) in `Net/Server.cs`; anything
  Microsoft logs is filtered out so the file stays readable.
* **`dev.sh` does not start the server with debug logging**: it passes
  `MECH_LOG_LEVEL="${MECH_LOG_LEVEL:-INFO}"`, so the per-weapon lines are off.
  Turn them on deliberately, for the run that needs them:
  `MECH_LOG_LEVEL=DEBUG ./dev.sh start` (or `./dev.sh range`), and then
  `tail -f /tmp/mech-server.log`.
* **`MECH_DEBUG` is not a log level.** It is the websocket gate for the
  development messages (`debug_spawn`, `debug_god`, `debug_tp`, `debug_hurt`,
  `debug_trees`, `debug_restart`) and for the DEBUG block of the settings menu;
  `dev.sh` sets it to 1 and that has nothing to do with what is written to the
  log.
* **The headless harness is `Harness/`** (`./dotnet.sh run -- --check ...`), the
  replacement for the Python tools the port came from.  It drives a real `World`
  in-process - no socket, no wall clock - so a check is a number, not a session.
  Every "measured" figure below and in the product notes is one run of one of
  these: `MECH_SEED` makes it repeatable while a single thread does the drawing
  (which is the harness's own case), and the number is read for its magnitude -
  did the change make things worse - never as a baseline to diff:
  ```bash
  ./dotnet.sh run -- --check battle --minutes 12   # scripted battle, per-minute census
  ./dotnet.sh run -- --check range  --minutes 2    # range world, a pilot firing
  ./dotnet.sh run -- --check hit                   # every weapon vs a parked APC
  ./dotnet.sh run -- --check intercept             # every missile vs the defence post
  ./dotnet.sh run -- --check diag --minutes 2      # what a few AI hulls are doing
  ./dotnet.sh run -- --check net --minutes 30 --from 8   # one pilot's download, per minute
  ```
  `MECH_PERF=1` on the battle check runs it without the per-tick event scan and
  prints the per-minute cost: on a 12 minute battle it is **1.3 ms/tick**
  (0.06 ms of it the 15 Hz wire) against the Python server's 11.5 ms/tick, i.e.
  ~9x, and the curve is flat where Python's climbs to 18 ms as the field matures.
  Every check ends with `ecs.validate()`, so bookkeeping that a value comparison
  would not catch (a row pointing past its buffer) fails the run.
* **`--check net` prices the wire** (`Harness/NetCheck.cs`): one scripted pilot
  (`Check.drive`), every payload `Server.broadcast` would have offered him, off
  the same doors (`snapshot_bytes` / `meta_payload` / `hearable`) at the same
  cadence, flat out with no wall clock - a 30 minute battle costs ~100 s of CPU.
  Per minute it prints kB/s by source (snapshot / fx / meta / frame headers) and
  the row census; it ends with what the same field *could* ride in (deflate, a
  2 km interest sphere, a leaner row), so a wire change is a number before it is
  an edit.  Measured unfiltered on 30 minutes, one pilot's ear: **468 kB/s mean, 523 kB/s
  peak, 94 % of it snapshots** (326 unit rows x 80 B x 15 Hz), 4 % meta (1886 B
  x 10 Hz), 2 % fx; the field is 300-400 rows and ~90 % of it is infantry.
  With the shipped tier sphere, the per-class rows, the fx rows and the clock
  blocks it is **67.8 kB/s** (minutes 8..15): 45.5 snapshot (6.7 of it the
  minimap block), 18.4 meta, 3.7 fx, 0.2 clock blocks - i.e. meta and fx are
  now the ones worth looking at, and the unfiltered field
  would still be 168 kB/s.
  It measures *a* client, not every client (one `SnapshotView`, as the server
  builds it), and a scripted pilot's address decides how much lives inside his
  sphere: the numbers move with where he is.
* **`tools/gun_report.py`** is the weapon-report analysis (it reads a recording
  with ffmpeg and prints the third-octave spectrum per window; `--synth` renders
  the cue `HEAVY_REPORT` in `audio.js` describes).  It is the one Python script
  left, it needs `numpy` in `.venv`, and it never touches the server.
  `tools/smoke_lock_test.js` is a browser-console driver for the seeker's smoke
  A/B test.
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

Then open <http://127.0.0.1:8010/>; the lobby picks the callsign and the side
before `DEPLOY` - the machine always comes out on the default pair
(`defs.DEFAULT_LOADOUT`), a loadout is a rearm-panel purchase.  `DEPLOY` fires
once; pressing `ENTER` in game must not send another `join`.

`./dev.sh range` starts a second world (`run.py --range`, i.e. `MECH_RANGE=1`)
on <http://127.0.0.1:8011>: a 4 km test map, one 200 m hill NW, plains
elsewhere, 12 blue APC targets (2 patrolling) that respawn 3 s after dying, a
squad of six infantrymen 260 m in front of the berths (riflemen and AT riflemen
alternating) that respawn in place after the same 3 s - the fixture a proximity
fuse is for, since a man is the only thing on the range the 30 mm's airburst
fires on - one blue missile-defence post (`Modes/Range/Interceptor.cs`, 5 s
respawn) and a ring of per-pilot spawn berths. **The range's own fixtures never shoot at the
ground** - the targets and the post fire at nothing, so it is the place to test
ballistics or look at a model up close. A hull a pilot drops there himself
(`debug_spawn`) is the exception: it is spawned with `range_drone` +
`front_mission` + `crew_engage` (`Mode.console_spawn_extra`), so the range's own AI
block drives it and a spawned red mech picks its own fights with the blue targets -
which is how an AI crew's missile arm gets watched without waiting for a battle.
The fixture hulls are built with `crew: false` and carry no AI tag at all, so no
pass can reach them.  The tag, not a fixture list, is the test: a tool's
scripted shooter (`tools/intercept_check.py`) is spawned untagged and stays a
puppet. The range has no lanes, so a route is -1 there and the lane policy
(`Systems/AI/Strategies/Front.cs`) gives its crews the enemy post as a point instead. The side is the
*lobby's* choice (`fixed_faction`
is None here; `AUTO` balances, so a second browser lands opposite the first,
which is how the radar and the intercept are checked from the receiving end -
one pilot fires a missile, the other watches his RWR and shoots it down).
$99999, rearm anywhere, smoke cooldown 0.5 s.
Seeker lock breaks are logged by `mech.smoke` (you have to be shot at for
that - the battle mode AI does it) at DEBUG, so the run that wants to read them
is started with `MECH_LOG_LEVEL=DEBUG ./dev.sh range`; the browser script
`tools/smoke_lock_test.js` drives a whole A/B smoke run from the console when
such a missile is in the air and reads the event stream, not the log.
Log: `/tmp/mech-range.log`, overrides `MECH_RANGE_PORT` (8011), `MECH_RANGE_LOG`.
Product notes: `doc/product/range.md`.

**Measure a mechanism on the range, not on a battle.** Weapons, ballistics,
damage, the seeker and the whole audio chain are all measured this way, because a
battle is not a bench: two dozen AI crews, both lanes and the waves are firing,
dying and driving through the same world, so a level, a node count, a hit or a
spectrum read there is mostly a measurement of where the pilot's hull happened to
be and of who else was shooting - a scripted load has to fight the real one for
the limiter, and a target that dies of a stray round is not a target. The range's
fixtures never fire, so nothing moves or sounds but what the console asks for:
`./dev.sh range` (a 4 km map, targets that respawn, an infantry squad, a
missile-defence post), then `g.audio` / `g.net.send({t:'debug_spawn'})` /
`g.net.send({t:'debug_missile'})` from the console. A sound bench is a
`setInterval`/`await sleep` loop calling `shot` / `boom` / `explosion` directly
(or pushing events onto `g.sounds` with `play` closures); `evaluate` keeps a
background tab's timers running but stops an `requestAnimationFrame` frame loop,
so call `g._pumpSounds()` from the test loop
when the frame pass is the thing under test. The battle server is where a change
is *seen*, after the number is already right.

**The range records the top attack** (`Modes/Range/Record.cs`, the range's post
system, on by default there): one JSONL line a tick to `MECH_RECORD_PATH`
(`/tmp/mech-range-trace.jsonl`, `MECH_RECORD=0` turns it off), carrying each
`top` round's state under the component names it lives in - `pos`, `vel`,
`proj_age`, `proj_trav`, `target` (the hull it holds; `-1` = blinded), `tgt_mem`
(the stare *point*, deck height) and `tgt_vel` (the velocity that memory drifts
at) - plus the true state of every hull a seeker could hold, and the tick's
`boom` / `seeker` / `intercept` events, so an impact point and a lost lock are
read out of the file instead of out of a DEBUG log that a normal run does not
write. **A tick with no top-attack round on it is not written**: the dive is the
only thing the numbers are for, so a 30 mm burst or a SACLOS fired at the
defence post leaves no line behind. The one line with an empty `missiles` is a
round's last: it goes off inside `projectiles` and is destroyed before the
recorder runs, so the tick that carries its burst is the tick that carries the
impact point. A re-flight through `flight.step` off a recorded state is how a
change to the PN law gets compared against what the real one did.

Environment overrides: `MECH_SESSION` (default `mech`), `MECH_PORT` (8010),
`MECH_LOG` (`/tmp/mech-server.log`), `MECH_LOG_LEVEL` (`INFO`),
`MECH_RECORD` / `MECH_RECORD_PATH` (see the range section above).
`dev.sh` always sets `MECH_DEBUG=1`, which unlocks the debug websocket messages
and the DEBUG block of the settings menu (gear, top right: RESPAWN / RESTART /
GOD). Without it the menu keeps the volume slider only.  **`MECH_DEBUG` does not
turn on debug logging**: `dev.sh` starts every server at `MECH_LOG_LEVEL=INFO`,
so the per-weapon lines stay out of the log until a run asks for them
(`MECH_LOG_LEVEL=DEBUG ./dev.sh start`).

Equivalent raw tmux invocation:

```bash
tmux new-session -A -d -s mech \
  "cd $PWD && MECH_DEBUG=1 ./dotnet.sh run -- --port 8010 2>&1 | tee /tmp/mech-server.log"
```

Manual run without tmux (add `MECH_LOG_LEVEL=DEBUG` for the per-weapon lines):

```bash
MECH_DEBUG=1 ./dotnet.sh run -- --port 8010
```

`dotnet.sh` is not a convenience wrapper around a plain `dotnet` call: dotnet
writes first-run sentinels into `$HOME` and a `mkdtemp` under `XDG_RUNTIME_DIR`
before it reads the project, and in this sandbox both are read-only, so a bare
`dotnet build` fails with `EROFS` before it compiles anything.  The script
points `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `XDG_RUNTIME_DIR` and `TMPDIR` at
`.dotnet_tmp/` (gitignored) and runs from the repo root.

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
  // the eye is mounted on the hull `mech.sight_z` behind its rotation axis, so
  // a fake `own` needs a `yaw` too - without one the mount offset is NaN and
  // the camera goes with it
  return g.__origCam({ x: eye[0], y: eye[1] - 5.05, z: eye[2], yaw }, dt);
}; };
g.__cam([x, y, z], Math.PI / 4, -0.45);      // yaw: dirFrom convention
```

* `_camera` is a cockpit camera whose eye is the commander's sight head: it
  sits at `defs.mech.eye` (5.05 m) above `own.y`, `defs.mech.sight_z` (-0.62 m)
  behind the hull's rotation axis and carried by the hull's yaw - hence the
  `- 5.05` above and the `yaw` in the fake `own`. `_sightEye` returns that same
  point, and it is the origin the server's sight, seeker and fire control solve
  from, so the cross, the range readout and the lead circles cannot disagree.
  The sight *head's* own rotation does not move it (the head turns about the
  pillar, the eye is the pivot); the hull swinging underneath does, which is
  the geometry the decoupled sight is for. The pilot's own
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

* **Code is English, documentation is Chinese.** Source, comments, commit
  messages, identifiers, log lines and everything a player reads - HUD copy,
  menu labels, feed messages, error text - are written in English. Everything
  under `doc/` (`doc/product/`, `doc/plans/` and the design notes next to them)
  is written in Chinese; a Chinese document quotes UI strings and identifiers
  verbatim in English rather than translating them.
* The rule is about the *language of the file*, never about its name: a
  `README.md` under `doc/` is Chinese, an English `///` comment is English,
  and the header comment of a tool in `tools/` is English like the code it
  documents. `AGENTS.md` itself stays English - it is the agent's working
  notes, not a document.
* Minimal, scoped edits: match the surrounding style (flat arrays, batched
  systems, short helper functions, comment density of the file you touch).
* **Do not write negations in comments or documents.** A thing that does not
  exist is not worth a sentence: "the order is *not* load bearing", "do not use
  `groups()` here", "this is not a type switch" all carry a rule that will drift
  out of date while the code around them does not. Describe what the code *is*
  and what it does; when a rule really has to hold, state it positively ("a crew
  is visited once per tick, and its draws come off its own stream"). The one
  place a negation belongs is *this file*, in the section that states a design
  principle - that is what these notes are for. Everything else keeps the
  comment lean and true.
* Units are metres and seconds; the map is 16 km square, the clock is 30 Hz.
* **The product version lives in `MechServer.csproj`** (`frontend/package.json`'s
  own `version` is a private manifest, not the product's - never read it). Its
  `<Version>` is stamped into the boot page at build time:
  `frontend/webpack.config.js` reads that one property (a real read of
  `<Version>`, not a regex that could pick up a `<PackageReference>`'s version),
  fails the build if it is missing and hands the string to `HtmlWebpackPlugin`
  as `templateParameters`, which fills the `<p class="ver">` line under the
  lobby's subtitle (on the range server too). So the number a player reads on
  the start page is the one the build actually read - a stale `assets` shows a
  stale version, which is exactly the point. **Every change to the product bumps
  it: a feature change bumps the second number (`1.12.2` -> `1.13.0`, the third
  going back to 0), a change a player cannot see bumps the third
  (`1.12.2` -> `1.12.3`).** A change to a document or a comment alone bumps
  nothing - the number is the product's, not the repository's. A bump is part of
  the change, in the same commit, so a build and a screenshot name the same
  version; a feature bump also carries its `doc/product/CHANGELOG.md` entry,
  while a patch bump has nothing to write there, because a player sees no
  difference.
* Tuning values (damage, rpm, range, speeds, wave interval, capture time) live
  in `Core/Defs.cs` and at the top of the mode systems (`Modes/Battle/Waves.cs`,
  `Modes/Range/Targets.cs`) — prefer changing them there over editing the code
  paths.
* A weapon is a real projectile: keep `kind` semantics
  (`bullet`/`rocket`/`saclos`/`top`) intact when adding or retuning one.
