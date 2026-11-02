# AGENTS.md

Working notes for coding agents touching **MECH**, a browser-based mech
shooter: numpy-ECS Python server, WebGL2 client, 16 km tile map, three lanes,
RED vs BLUE.

## Read `doc/product` first

Everything under `doc/` is written in Chinese (the code, the comments and every
string a player sees are English - see [Conventions](#conventions)).
`doc/product/` is the product documentation and the source of truth for
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
doc/                       # Chinese: every document in the repo lives here
  product/                 #   player-facing rules: flow, screens, units, weapons, changelog
  plans/                   #   refactor and feature plans (e.g. speed_component.md)
  top_attack_plan.md       #   design notes next to the code they describe
backend/
  run.py                   # dev entry point: uvicorn server.main:app
  pyproject.toml           # fastapi, uvicorn[standard], numpy
  server/
    ecs.py                 # archetype ECS: component set is the entity's type
    components.py          # every component the game declares
    defs.py                # entity types, weapons, unit stats, mech geometry
    world.py               # World: terrain, players, spawning, tick, queries
    terrain.py             # 1000x1000 tiles of 16 m -> 16 km square map
    mathutil.py            # angles, lead solving, terrain helpers
    profiler.py            # white-box probes (MECH_PROBE=1), off and free by default
    grid.py                # 128 m cell index over the units, refiled every tick (AI broadphase)
    ballistics.py          # fire control: sight setting elevation (player) / sight-point solve (AI)
    flight.py              # aerodynamics + guidance of a winged round (the top-attack missile)
    seeker.py              # an IR round's lock: what it stares at, and what it re-locks on
    combat.py              # damage resolution: the only writer of `hp` (apply/hit/splash)
    netcode.py             # init payload, binary snapshots, JSON meta
    main.py                # FastAPI: websockets, static files, 30 Hz game loop
    systems/               # mechanics shared by every mode, each exposes run(world, dt)
                         #   gunnery.py: the sight leads, the hull and the arms slew after it
                         #   sight.py: the one line of sight (terrain + smoke), clear/why
                         #   smoke.py: hot smoke canisters, opaque puffs, blocks(a, b)
                         #   proximity.py: 30mm airburst fuse, people *and* missiles
                         #   blasts.py: an explosion is an entity; the only area damage
                         #   mortality.py: `hp <= 0` -> wreck, burst, kill money, victory
                         #   rearm.py: the bought job at the base, `work_remaining_time`
    modes/                 # per-mode rule sets: map, setup, pipeline, spawn rules
      base.py              #   Mode interface every mode implements
      battle/              #   waves.py: town capture + reinforcement waves
                           #   raids.py: the raiding parties - one mech per lane,
                           #   force-gradient + two-level (128 m / 16 m) pathfinding
      range/               #   targets.py: passive APC targets, bunkers.py: ridge blockhouses
                           #   test_ai.py: the ground AI for hulls a pilot drops there
                           #   (the range's own fixtures never shoot back)
  assets/                  # compiled client (webpack output, gitignored)
tools/
  replay.py                # scripted battle replay: the simulation's sanity harness
  fixtures/                # its recorded tick summaries (checked in)
  smoke_lock_test.js       # browser-console driver: seeker A/B smoke test
  vehicle_trace.py         # range APC route trace: SVG path plot + per-tick CSV
  raid_trace.py            # a battle's raiders: force-gradient + driven traces as SVG
  intercept_check.py       # range: fire every missile at the defence post, read the log
  profile_battle.py        # headless battle profiling: per-system + cProfile + per-minute timeline
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
    input.js               # pointer lock, keys, wheel, panel keys
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
* **One position, one velocity, on everything that moves.** `pos` and `vel` are
  `float32[3]` on every mover - a hull, a round, a smoke canister - and `vel` is
  **what the entity is actually doing**: `pos` is advanced by it, every collision
  is solved against it, a seeker leads on it. The order a hull was given lives in
  its own component, `vel_cmd`, and only a hull has one: the gap between the two
  *is* the machine (the mech's ramp, a car's steering, a wall). `movement` is the
  one writer of a hull's `vel` (including `vel[1]`, the slope it just drove
  over), `projectiles` of a round's, `smoke` of a canister's. A scripted mover or
  a teleport writes `pos` **and** zeroes `vel`; a controller writes `vel_cmd`.
  The invariant is checked - `World.spawn_unit` asserts it at every spawn and
  `tools/replay.py::_speed_invariant` over the archetypes - and it is worth
  keeping because the alternative was five subsystems each quietly rebuilding a
  velocity of their own: `seeker.track` handed proportional navigation a hull's
  *command* (50 m/s the instant a pilot nudged the stick) and the top attack
  missed a stationary hull by 8-36 m (`doc/plans/speed_component.md`,
  `tools/top_attack_dive.py`).
* **Wheeled locomotion state is three scalars, not a component.** `wheel_speed`
  (m/s along the hull, negative in reverse), `backout_t`, `blocked_t` belong to
  `movement._vehicle` alone: not on the wire, never written by a mode or an AI.
  They used to be one `veh` vec3, and `veh[1]` told nobody it was the back-out
  clock.
* **Write systems against buffers, not through `View`.** `ecs.groups(...)`
  hands out an archetype with its buffers hoisted and rows `0..n-1`: reach for
  it whenever the system integrates entities independently (movement, countdown
  timers, leg turning) and the whole pass becomes a handful of numpy calls.
  `ecs.slot(eid)` is the same thing for a per-entity loop that touches many
  components — one lookup, then `bufs["pos"][row, 0]`; `"combat" in bufs` is the
  component test. `ecs.locate(ids)` gives `(rows, archetypes)` when the loop has
  to stay in query order. `ecs.columns(ids, names)` is the batch door: it
  resolves the archetype split **once** and returns a dict of arrays for many
  components at once (`View` pays that split again for every component, which is
  why `netcode.snapshot_bytes` reads its twenty columns through it — 5× on the
  wire build). The values are the ones a `View` gives, defaults included.
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
  per-type *data* (hull geometry, death recipe, which arm fires what) and is only
  read for that.
* **World** (`world.py`) owns terrain, players, the ECS and the system
  pipeline. Shared order: `rearm`, `player_input`, the mode's pre-systems
  (battle: `ai`), `gunnery, locking, smoke, weapons, movement, projectiles,
  proximity, blasts, mortality, economy, debris`, then the mode's post-systems
  (battle: `waves`).
  `gunnery` sits between the crews saying where they are looking and anything
  reading a barrel, so `yaw` / `pitch` are the *laid gun* by the time a weapon
  fires or the client draws.  The last three are the damage chain and their
  order is load bearing: `projectiles` and `proximity` write hp where they land
  and leave bursts behind, `blasts` judges the bursts (including the ones the
  previous tick's deaths left), and `mortality` decides who died - after every
  damage source, before `waves` counts hulls to take a town.
  `World.update(dt)` is the only entry point; the loop in `main.py` calls it at
  a fixed 30 Hz (`TICK_DT = 1/30`).
* **An empty server is a paused server.**  With `CONNECTIONS` empty the tick
  loop in `main.py` skips `world.update` and `broadcast` altogether and just
  sleeps (`IDLE_SLEEP` = 0.1 s, re-basing `next_t` on the way out so the world
  resumes on a clean tick instead of catching up).  Nothing is simulated and
  nothing is sent while nobody is connected - the battle, the economy, the
  waves and the raiders all stand exactly where the last pilot left them, which
  is the point: a server that had been idle for an hour used to hand a joining
  pilot a mid-war map.  `MECH_DEBUG=1` disables the pause (`PAUSE_WHEN_EMPTY`),
  because that is the run a script, a replay or a console session inspects from
  the outside with no browser attached.  A tool that drives `world` directly
  (`tools/replay.py`, `tools/raid_trace.py`, ...) never goes through the loop
  and is unaffected either way.
* **The sight leads, the machine follows** (`systems/gunnery.py`, `defs.MECH_
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
  mech's `work_remaining_time` (a plain float, 0.0 = idle); `systems/rearm.py`
  counts it down and only at zero applies the lines. `player_input` zeroes the
  movement and both trigger bits while it is above zero, so a job is a real
  pause in a fight — the mech can still look around. The job dies with the
  mech (and the money with it), and a second order is refused until it ends.
  Durations are `defs.REARM_*` (swap 2 s per arm, ammo 4 s per arm, repair 2 s
  per 100 hp); `shop.quote` charges a swap both a fit and a reload. What was
  bought dies with the hull: `World.spawn_mech` resets `Player.loadout` to
  `defs.DEFAULT_LOADOUT`, so every respawn is a fresh standard machine and a
  105 mm is a thing to buy again.
* **An AI crew has no shop, so it draws its loadout.** `World.spawn_unit` hands
  a hull that declares `arms` (the mech — the one component test, not a type
  switch) a template off `defs.AI_LOADOUTS` when no `weapons` were passed: six
  configs at 30/10/10/25/15/10 %, the left arm always the 30 mm and the right
  arm the variable. A pilot's mech always passes `player.loadout`, and the
  range's defence post passes its own, so both are untouched by the table. The
  draw comes off `world.rng`, which makes the order of the list part of the
  simulation like every other draw.
* **An AI crew fires the arms it has.** `ai._aim_and_fire` used to pull the
  right trigger only for `kind in ("rocket", "saclos")`; it now fires any arm
  whose own `range` covers the target (still 2.2 km for those two, `ai_range`
  for everything else), and `weapons.run` gives a `lock` weapon on an
  ownerless hull the lock the crew already holds — the AI's `target`
  component — because there is no `pl.lock_target` to wait for. There is also a
  floor under the cycle: `defs.AI_LAUNCH_GAP` (15 s) for a lock round, or eight
  tubes are gone in four seconds and the machine is toothless for the rest of
  the match. SACLOS is not on that clock — its price is holding the sight.
* **The sight is one line, so the lead is solved for the slowest round.**
  `ai._engage` picks the aim speed as the minimum `mathutil.mean_speed` over
  the arms that reach the target, not the primary weapon's muzzle figure: a
  beam rider averages ~340 m/s at 1 km against the cannon's 1080, and leading
  it with the cannon's number put every missile tens of metres behind a moving
  hull (measured: 16 launches, no kills on movers; after: 3–4 kills per 16).
  `mean_speed` is the boost-and-coast profile off `vel` / `thrust` / `maxvel` /
  `burn`, so a new missile needs no entry in `ai.py`.
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
  `restart_respawn`, `mech_spawn`, `respawn_delay`). Adding a mode means adding
  a folder, never another `if` in a shared system. Battle runs `ai` + `waves`
  (capture and reinforcement waves); range runs `targets`, the missile post, and
  `ai` only over the hulls a pilot spawned there (`range/test_ai.py`).
* **A death is planned from where it happened.** `World.kill` records
  `Player.death_pos` and asks the mode for `respawn_delay`; the mode's
  `mech_spawn` later turns that position into a place. Battle's version
  (`modes/battle/__init__.py`) picks the town with `waves.respawn_town`, which
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
  Airburst is blast only (it is not a direct hit), and the burst is logged at
  DEBUG (`mech.prox: prox fuse: ...`, with the leg it flew and the closest
  approach) so a fuse hunt can read the burst position out of the log —
  `MECH_LOG_LEVEL=DEBUG`, see "Logging" under Build.  It fires once per round
  that fuses, so it is never an INFO line.
* **A missile is a target, and the ECS says so** (`components.MISSILE_COMPS`).
  `weapons.fire` picks the archetype off `defs.is_interceptable(wid)`: a
  `kind` `rocket`/`saclos`/`top` round is spawned with `interceptable` + `hp` +
  `hpmax` + `armor` + `last_hit` (10 points, 1/1 of plate), a bullet keeps
  `PROJ_COMPS` and pays nothing.  A guided round also carries the seeker's
  memory (`tgt_mem` / `tgt_vel`, `seeker.py`) - see the seeker bullet below.
  The tag is what puts a round in the
  hit-sphere table and in the fuse's target set, so `combat.hit`/`splash`/
  `apply` work on it with no special case, and `systems/mortality.py::
  _kill_round` is the second death channel: a killed missile leaves **nothing** -
  no warhead, no wreck, no credit (a SACLOS that still went off 300 m out would
  make shooting it down pointless).
* **The hit-sphere table carries motion** (`projectiles.sphere_table(world, dt)`).
  Rows are `(centres, radii, ids, modules, factions, blocks, cull, prev, disp)`
  and the sweep solves the *relative* leg: `rel = prev - p0`, `w = seg - disp`,
  `t* = clamp(rel·w/|w|²)`, `d_min = |rel - w t*|`.  A missile covers 20 m a tick
  against a 4 m fuse and a 0.08 m ball, so a solve against a standing target is
  decided by the phase of the clock (measured: the head-on crossing case is 6 m
  off at the end of the leg, i.e. a clean miss).  The two blocks are gathered at
  different moments and each says so: a **unit** has already been moved by
  `movement` (it runs earlier), so its `pos` is the end of its tick and
  `prev = pos - vel*dt`; a **missile** has not moved yet (this system moves it),
  so its `pos` is the start and `disp` is predicted from its velocity.  The
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
* **The geometry is batched, the bodies are not.**  `terrain.ground_hits_many`
  takes a whole tick's worth of round legs at once and answers where each one
  meets the ground, because a leg is 36 m of terrain sampling and there are
  dozens of them — `projectiles.run` therefore flies every round first, asks once,
  and only then resolves impacts in ascending `pid` order (entity creation order
  is part of the simulation, not an implementation detail).  `walk` in its return
  says "this leg crossed a tree tile", which is handed back to the scalar
  `ground_hit`: a trunk is a cylinder solved analytically, not something to
  sample.  What is *not* worth batching is the integration itself — a few
  numpy calls over one or two rows cost more than the scalar arithmetic they
  replace (measured, twice).
* **The fuse settles the round it fused on.**  `systems/proximity.py` fuses on
  `person` ∪ `interceptable` and bursts at the round's position at `t*`; a
  missile found there takes `combat.splash` **immediately**, at the gap it was
  at when the fuse fired, and is handed to the burst as `skip`.  `blasts` runs a
  stage later against positions gathered for the whole tick, by which time a
  600 m/s round is twenty metres out of a four metre radius - settling it
  directly is what turns "did the blast catch it" into an exact answer.  The
  numbers are unchanged: blast only, at the true gap (14 at contact, 10 at
  2.67 m, 8 at the 4 m edge, against 10 hp) - so the fuse **triggers** at 4 m
  and only **kills** inside 2.67 m.  That gap is deliberate.
* **Radar is one equation, two implementations** (`defs.radar_k`): a doppler
  receiver only sees what closes on it, so `radar_strength * rcs / d^4 *
  (v_c / RADAR_V_REF)^2 >= 1`, i.e. `d² <= K·v_c` with `K = sqrt(S·rcs)/V_REF`.
  `V_REF` (600 m/s, `defs.RADAR_V_REF`) only makes the speed term dimensionless;
  `radar_strength` (m⁴, on the unit type: mech 5e13, APC 3.125e12, nobody else)
  is the one knob, and `d ∝ S^¼` is why an APC sees half as far.  `v_c <= 0`
  fails the test by itself, which is why there is no speed threshold any more
  (the client's old `RADAR_SPEED` is gone).  Discovery distances against a mech:
  SACLOS 1000 m, S-8 713 m, top-attack 563 m, RPG 398 m.  Both sides read the
  same numbers: `netcode.static_payload` sends a `radar` block (`v_ref`, the
  largest `rcs`) and the client derives the dial's outer ring from it - the
  `1 km` label is no longer a constant.  The receiver is **gated at 1 km**
  (`RWR_RANGE`, or the machine's own ring when that is shorter) and **gated on
  line of sight** (`map.sightBlocked`: terrain, canopies, smoke - the same door
  the AI's lookout, the IR lock and the pre-aim box go through), because
  without either of them every missile on a 16 km map that merely closed on the
  hull rang the alarm: measured, 29 % of hull-frames were ringing at a median
  threat range of 5.5 km, 0 % of them aimed at that hull.  The client draws
  three states: seen
  (diamond at true bearing/range + a pre-aim box through the sight rule), heard
  closing (a 5 Hz red spoke from the dial's edge - bearing, no range) and shot
  down (`intercept` fx: a few sparks for everyone who can see it, and no cue of
  its own - the event still carries `pid` (the crew that fired the burst) and
  `firer` (whose round it was) so a script can read them, but a chime per round
  was the busiest sound in the game, since the battle's AI crews shoot missiles
  down all over the map).  **Plotting is not warning**: a tracked round is
  drawn whether or not it is coming for this hull, but only an *incoming* one
  (`warn`) sings and turns the rim red - the course is solved on the **relative
  velocity** (the round's against the hull's) for the nearest approach, inside
  the blast's reach (`er + 20`), and a guided round (SACLOS, top attack) is
  judged on its horizontal track because its profile is a climb and a dive, not
  a line.
* **AI missile defence** (`systems/ai.py::_missile_step`) runs inline in
  `ai.run` before the ground fight and preempts it: a crew that has a round
  coming for it lays the sight on the lead point (`mu.solve_lead`, which honours
  the target's vertical velocity - a climbing ATGM is not a hull) and pulls the
  trigger only when the *bore* is within `atan((prox + hit_r)/d)` (0.23° at
  1 km, not the 0.14 rad a hull gets).  Only a fused arm fires (`PROX[wid] > 0`,
  today just the 30 mm), so a 12.7 mm carrier stays out of it - data driven, not
  a type switch.  That gate is the *AI's* rule only: a missile is a 10 hp / 1
  armour target like any other, so **any** round landing on one kills it
  (one 12.7 mm bullet is `AD 10`), which is why a pilot in an APC can and does
  shoot an incoming round down with the gatling while the AI crew ignores it -
  the fuse buys margin (4 m trigger, 2.67 m kill), not the ability.  "Coming for me" is `seeker target == me` for a top-attack (it
  climbs 150 m and dives, so its straight line misses) - or, once that lock is
  cut, a stare point inside `CPA_MEM` (100 m) of the hull, because a blinded
  round still flies at its memory and can take the hull back - and closest
  approach < `CPA_HIT` (40 m) for everything else; a `t_cpa` gate would say
  nothing (a round is 1.1-2.6 s from its closest approach the moment it is
  first seen).
  `ai.intercept(world, dt)` is the same step as a standalone pass, for a mode
  whose hulls do not fight.
* **The range has a missile-defence post** (`modes/range/interceptor.py`): one
  blue AI mech at `(1550, 2250)` with a 30 mm only, running `ai.intercept` and
  none of the ground AI - it never shoots at the pilot and never drives, and it
  comes back 5 s after being destroyed.  `tools/intercept_check.py` fires every
  missile type at it and prints the outcome per round, read out of the server
  log (`missile N took X (prox Y m) - hp A -> B`, `intercept: ... round N shot
  down at ...` - both DEBUG, the tool raises those loggers itself).  Measured:
  34 of 36 rounds fused and shot down (two got through).
* **A guided round flies an airframe, it does not follow a curve** (`flight.py`).
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
  state.  It stays in `MISSILE_COMPS`; the two components that are state
  (`tgt_mem` / `tgt_vel`) belong to the seeker and never change the wire.
  Its `life` is a **battery**: at zero `projectiles` calls
  `impact` and the warhead goes off where the round happens to be, which is what
  puts the range ceiling at ~4.3 km, not the motor.  `tools/top_attack_trace.py`
  is the harness: it flies the same model headless, prints the envelope table and
  draws the plan view / profile / speed-AoA plot.
* **The seeker remembers, and the guidance does not care** (`seeker.py`).  A
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
* **Damage is not a system.** `combat.py` is a library next to `ballistics.py`
  and `shop.py`: a round writes hp where it lands, inside the system that fired
  it. There is deliberately no damage queue - a system owns state, and damage
  owns none. The consequence to remember: **hp a system reads is the hp at its
  own stage**, and a hull at 0 is still in the record until `mortality` runs
  (last of the damage chain), which is why `combat.apply` also zeroes `vel` and
  `vel_cmd` - a hull at 0 stops where it is instead of driving on for a stage.
* **Death has one judge.** `systems/mortality.py` asks one question for every
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
  mech draws one off `defs.AI_NAMES` at spawn (a draw off `world.rng`, so the
  pool's order is part of the simulation) and a pilot's hull carries his
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
* **An explosion is an entity** (`systems/blasts.py`). Whoever wants one - a
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
  the factory gives it `arm_hp` / `arm_hpmax` (a mech: hull 100, one arm 50
  each). Every system tests *the component*, never the type - `combat.arm_alive`
  is `arm_hp[eid, arm] > 0`, and the arm index is the weapon index. Three rules
  live in `combat._mech_hit` and nowhere else: a hit on an arm stays off the
  hull, an arm at 0 loses its ammunition, and a round into an arm that is
  already gone goes inwards. `MOD_TORSO` is hit geometry only - it charges the
  hull, and column 1 of the wire `hp` is always 0.
* **Debris** — `systems/mortality.py` leaves an inert entity behind: a corpse
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
  The command interface is unchanged — the AI still writes a direction and a
  speed into `vel_cmd`, and `movement` turns it into `vel` — but the AI *drives
  the way points* (`ai._advance_route`):
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
  `_march` is the one ray walker four systems share (AI sight, ballistic
  elevation, hull collision, a round landing), and it is the hot loop of the
  whole simulation: **87.6 % of its calls are ground legs of 50 m or less**, so
  what pays off there is the cost of one sample, not the length of the ray.
  Keep it scalar and inline (`_spans` unrolled into the loop, `sample()` caching
  the previous tile's four corner heights, the tree test hoisted out of
  `_trunk_hit`/`_canopy_hit`).  Vectorising a *single* ray is a trap: five
  samples are cheaper in plain Python than in a dozen small numpy calls (tried,
  measured 10× slower).  `ground_hits_many` is the other side of the same coin -
  many rays, one batch.
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
  A unit row is 80 bytes (`UNIT_DTYPE`): `yaw` / `pitch` are the *laid gun*
  (hull and arms), `syaw` / `spitch` where the crew is looking, and `vx` / `vy` /
  `vz` the velocity the hull is **actually** moving at (`vel`, not `vel_cmd`),
  which is what the client's lead boxes and speed readout use.  The client's
  own machine reads its pose from the snapshot like any other - overriding it
  with the view is exactly the teleporting hull this split removed - while the
  *camera* is the local view (an instant sight) sitting on the server-side
  eye.  `defs.MECH_SIGHT_Z` travels in the init payload (`mech.sight_z`) so
  `game._sightEye` mounts the eye where `mathutil.sight_eye` does.
* **Debug messages** are accepted only when `MECH_DEBUG=1` (see `main.py`),
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
  lock tone, `denied`) stay centred - they are not in the world. There is no
  hit/kill tone: taking a hit is the red rim flash and a kill is the kill feed
  line, both visual.
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
* **Logging: a weapon-level line is DEBUG, never INFO.** A round bursting
  (`mech.prox: prox fuse: ...`), a seeker losing or retaking its lock
  (`mech.smoke`), a missile taking a hit and being shot down
  (`mech.intercept`) all happen hundreds of times a minute in a live battle —
  measured, one 20 minute scripted round wrote 693 lines / 148 KB, 95 % of it
  the fuse line — so they are `log.debug` and a normal run writes none of it.
  INFO is for the rare, once-a-session events; DEBUG is where anything that
  fires per round, per hit, per lock or per tick belongs.  `main.py` sets the
  level from `MECH_LOG_LEVEL` (default `INFO`) and uvicorn stays at `info`.
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
* **`tools/gunnery_check.py`** is the sight/hull/arm check: headless on the
  range map it flicks the view 90 degrees and measures the hull rate, the arm
  rate, the stop at the elevation limit, the eye's place on the pivot (turning
  the sight must not move it, turning the hull must walk it round the 0.62 m
  arc) and, by firing mid-flick, that a round leaves along the *barrel* rather
  than along the cross.  Every line it prints is a number with what it should
  be.
  ```bash
  .venv/bin/python tools/gunnery_check.py        # -v for a per-tick trace
  ```
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
  the fixture when a change is meant to move the numbers.  Three self-checks run
  before the battle and assert instead of comparing
  (`_cache_after_growth`, `_damage_model`, `_death_blast`, `_raid_plan`):
  `_damage_model` is the hp layout - one pool per hull, `hp <= 0` kills, the mech
  is the only hull with `arm_hp` and losing an arm costs its ammunition - and
  `_death_blast` is the wreck going up, which needs two `world.update` calls
  because the kill and the burst it leaves are deliberately a tick apart, and
  `_speed_invariant` is the one-velocity rule: every mover owns `pos` + `vel`,
  every hull owns `vel_cmd` too, and `vel` is the motion rather than the order
  (a mech told to run flat out still leaves the mark at `MECH_ACCEL`).
  ```bash
  .venv/bin/python tools/replay.py          # compare
  .venv/bin/python tools/replay.py --save   # re-baseline
  ```
* **`tools/profile_battle.py`** is where "what does a tick cost" is answered:
  it drops the 30 Hz sleep and runs a scripted battle as fast as the CPU allows,
  then prints wall time, ticks/s, every entry of `world.systems` (total, mean per
  tick, worst tick), the netcode cost per tick and a per-minute timeline - and
  with cProfile on, the function ranking too (inflated ~1.7-2.5×, so only read it
  for the order).  `--no-profile` is the honest read; `--minutes 20` is the
  meaningful window, because the field only matures after ~10 minutes and three
  of the four optimisations in `doc/profiling_results.md` scale with it.
  ```bash
  .venv/bin/python tools/profile_battle.py --minutes 5 --no-profile
  .venv/bin/python tools/profile_battle.py --minutes 5,10,20 --out /tmp/prof.json
  # an A/B needs a clean checkout, not a stash of the work in progress.
  # `backend/assets` is gitignored, so make it or `server.main` refuses to import;
  # the worktree has no venv of its own, so name the main one.
  git worktree add /tmp/mech-base HEAD && mkdir -p /tmp/mech-base/backend/assets
  cd /tmp/mech-base && /path/to/mech/.venv/bin/python tools/profile_battle.py \
      --minutes 20 --no-profile --out /tmp/prof_base.json
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
* **`tools/intercept_check.py`** fires every missile type at the range's
  defence post and reads the outcome out of the log: how much health the 30 mm
  took off each round, at what fuse gap, and whether it came down.  It is the
  measurement behind the interception numbers - run it after touching the fuse,
  the radar constants or the AI's intercept branch.
  ```bash
  .venv/bin/python tools/intercept_check.py              # every type, two ranges
  .venv/bin/python tools/intercept_check.py -v           # and every log line
  ```
* **`tools/gun_report.py`** is where the 105 mm cue's numbers come from: point it
  at a recording (a video is fine, ffmpeg pulls the audio out) and give it the
  shots' times, and it prints each one's third-octave spectrum per window, its
  total envelope out to 2.5 s and, per band, when it peaks and when it is 6 and
  20 dB down.  `--synth` renders the same table for the cue as `HEAVY_REPORT`
  describes it, so the two are comparable without a browser.  Tuning a weapon
  sound by ear is guessing: a real report is five events (crack, cough, boom,
  rumble, tail) tens of milliseconds apart, and the gaps are the weight.
  ```bash
  .venv/bin/python tools/gun_report.py plan_imgs/m1fire.mkv --at 90.43,114.90
  .venv/bin/python tools/gun_report.py plan_imgs/m1fire.mkv --at 114.90 --synth
  ```
* **`tools/profile_battle.py`** profiles the simulation instead of playing it:
  it drops the 30 Hz sleep and runs a battle as fast as the CPU allows,
  timing every entry of `world.systems` plus the grid build and the rest of
  `World.update` (total, mean per tick, worst tick), the netcode the loop also
  pays for (one snapshot every `SNAP_EVERY`, one meta per pilot every
  `META_EVERY`), a per-minute timeline of the entity census, and an optional
  cProfile breakdown.  Two scripted pilots fly it, so every duration replays
  the same battle.  `backend/server/profiler.py` is the door for the *inside*
  of a system - a stage calls `PROBE.start()` / `PROBE.end(name, t0)`, and
  `MECH_PROBE=1` turns those into a per-name accumulator (unset they are two
  no-ops).  No call site uses it yet; that is the next pass.
  ```bash
  .venv/bin/python tools/profile_battle.py --minutes 5
  .venv/bin/python tools/profile_battle.py --minutes 5,10 --no-profile --out /tmp/p.json
  ```
* **`tools/top_attack_dive.py`** is the tuning harness for the missile's dive:
  it reads a range trace (`MECH_RECORD`, `modes/range/record.py`), re-flies
  every recorded shot through `flight.step` off the state the real round had,
  and reports the miss distance per shot under a candidate `pn_n` / `pn_r` /
  `pn_v`.  Two inputs are worth comparing, and they are the whole argument for
  the velocity refactor: `--vel recorded` is what the *old* engine handed
  proportional navigation (a hull's velocity *command*, up to 50 m/s the instant
  a pilot nudged the stick) and `--vel truth` the velocity it was really moving
  at.  Measured on five dives at a hull that mostly stood still: 8-36 m misses
  fed the command, 0.7-2.8 m fed the truth, and no `pn_n`/`pn_r` can out-vote a
  50 m/s phantom.  `--sweep` prints the grid for both.
  ```bash
  .venv/bin/python tools/top_attack_dive.py --sweep
  .venv/bin/python tools/top_attack_dive.py --pn-n 2 --pn-r 800 -v
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

Then open <http://127.0.0.1:8010/>; the lobby picks the callsign and the side
before `DEPLOY` - the machine always comes out on the default pair
(`defs.DEFAULT_LOADOUT`), a loadout is a rearm-panel purchase.  `DEPLOY` fires
once; pressing `ENTER` in game must not send another `join`.

`./dev.sh range` starts a second world (`run.py --range`, i.e. `MECH_RANGE=1`)
on <http://127.0.0.1:8011>: a 4 km test map, one 200 m hill NW, plains
elsewhere, 12 blue APC targets (2 patrolling) that respawn 3 s after dying, one
blue missile-defence post (`modes/range/interceptor.py`, 5 s respawn) and a
ring of per-pilot spawn berths. **The range's own fixtures never shoot at the
ground** - the targets and the post fire at nothing, so it is the place to test
ballistics or look at a model up close. A hull a pilot drops there himself
(`debug_spawn`) is the exception: it is tagged `range_drone`
(`Mode.console_spawn_extra`) and `modes/range/test_ai.py` runs `ai.run(hulls=)`
over exactly those hulls, so a spawned red mech picks its own fights with the
blue targets - which is how an AI crew's missile arm gets watched without
waiting for a battle.  The tag, not a fixture list, is the test: a tool's
scripted shooter (`tools/intercept_check.py`) is spawned untagged and stays a
puppet. The range has no lanes, so `ai._advance`
and `ai._advance_route` fall back to walking at the enemy post. The side is the
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

**The range records the top attack** (`modes/range/record.py`, the range's post
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
  "cd $PWD/backend && MECH_DEBUG=1 .venv/bin/python run.py --port 8010 2>&1 | tee /tmp/mech-server.log"
```

Manual run without tmux (add `MECH_LOG_LEVEL=DEBUG` for the per-weapon lines):

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
  `README.md` under `doc/` is Chinese, a `docstring` in `defs.py` is English,
  and the header comment of a tool in `tools/` is English like the code it
  documents. `AGENTS.md` itself stays English - it is the agent's working
  notes, not a document.
* Minimal, scoped edits: match the surrounding style (numpy vectorised
  systems, short helper functions, comment density of the file you touch).
* Units are metres and seconds; the map is 16 km square, the clock is 30 Hz.
* **The product version lives in `backend/pyproject.toml`** (`frontend/package.json`'s
  own `version` is a private manifest, not the product's - never read it). Its
  `version = "1.0.0"` is stamped into the boot page at build time:
  `frontend/webpack.config.js` parses that file (`smol-toml`, a build-time dev
  dependency - a regex is not a TOML reader and a `version` key in another
  table must never win), fails the build if the key is missing and hands the
  string to `HtmlWebpackPlugin` as `templateParameters`,
  which fills the `<p class="ver">` line under the lobby's subtitle (on the
  range server too). So the number a player reads on the start page is the one
  the build actually read - a stale `backend/assets` shows a stale version,
  which is exactly the point. **Every feature change bumps the second number
  (`1.0.0` -> `1.1.0` -> `1.2.0`); the third stays 0** - it is reserved, not
  another counter, and internal refactors with no player-visible effect do not
  bump at all. A bump is part of the change, in the same commit as the
  `doc/product/` entry, so a build, a changelog line and a screenshot all name
  the same version.
* Tuning values (damage, rpm, range, speeds, wave interval, capture time) live
  in `defs.py` and at the top of the mode systems (`modes/battle/waves.py`,
  `modes/range/targets.py`) — prefer changing them there over editing the code
  paths.
* A weapon is a real projectile: keep `kind` semantics
  (`bullet`/`rocket`/`saclos`/`top`) intact when adding or retuning one.
