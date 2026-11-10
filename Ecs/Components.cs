// Component registration.
//
// Every component is a dense buffer over the entities that own it; which
// components an entity owns is its *type* and is fixed when it spawns (see
// `Ecs/Ecs.cs`).  A component value of zero is therefore a perfectly valid
// value.
//
// The groups below are the component sets the spawning factories hand to
// `ECS.create`: one group per archetype.  Ported one for one from
// `backend/server/components.py` - the comments there carry the reasoning, and
// the ones that explain a rule rather than a line are kept here.

namespace Mech.Server;

public static class Components
{
    public static void register(ECS ecs)
    {
        // ---- transform --------------------------------------------------
        // Two velocities, and the split is the whole point:
        //   `vel`     the velocity the entity is *actually* moving at - `pos` is
        //             advanced by it, every collision is solved against it, and a
        //             seeker may lead on it.  Every mover owns one.
        //   `vel_cmd` the velocity it was *told* to make - a pilot's stick, a
        //             crew's intent, a mode's patrol.  Intent, not physics.
        // `movement` is the only writer of a hull's `vel`; `projectiles` of a
        // round's; `smoke` of a canister's.
        ecs.register("pos", Kind.F, 3, 0.0);
        ecs.register("vel", Kind.F, 3, 0.0);            // the velocity it really has
        ecs.register("vel_cmd", Kind.F, 3, 0.0);        // the velocity it was told to make
        // Wheeled locomotion state - a car has a throttle, not a velocity, and
        // these three are what `movement._vehicle` integrates between ticks.
        ecs.register("wheel_speed", Kind.F);            // m/s along the hull, negative in reverse
        ecs.register("backout_t", Kind.F);              // s of reverse left, nose against something
        ecs.register("blocked_t", Kind.F);              // s the nose has been against something
        ecs.register("yaw", Kind.F);
        ecs.register("pitch", Kind.F);
        ecs.register("leg_yaw", Kind.F);
        ecs.register("turret_yaw", Kind.F);
        // Where the gunner is *looking*, as opposed to where the metal points.
        // For a mech the sight is a free turret on the roof: it answers the view
        // instantly, and `yaw` / `pitch` slew towards it at the machine's own
        // rates (`Systems/Gunnery.cs`), never the other way round.
        ecs.register("sight_yaw", Kind.F);
        ecs.register("sight_pitch", Kind.F);

        // ---- identity ---------------------------------------------------
        // `etype` is the entity's data (hull geometry, hp slots, which arm fires
        // what).  What an entity *is* lives in the marker components below: an
        // entity carries the tags of its type (`Defs.UNIT_DEFS[...]["tags"]`)
        // and every system queries the tag it can handle instead of switching on
        // the type.  A smoke puff has none of them, so no weapon can hit it and
        // no AI can pick it as a target.
        ecs.register("etype", Kind.U8);
        ecs.register("faction", Kind.U8);               // units and rounds only
        ecs.register("owner", Kind.I, 1, -1);           // player id, -1 = AI
        ecs.register("unit", Kind.U8);                  // a hull on the battlefield
        ecs.register("combat", Kind.U8);                // fights, may be fought, holds towns
        ecs.register("lockable", Kind.U8);              // an IR seeker can track it
        ecs.register("moves", Kind.U8);                 // has locomotion
        ecs.register("vehicle", Kind.U8);               // a wheeled hull: steers, does not strafe
        ecs.register("person", Kind.U8);                // a man on foot: a prox fuse goes off on him
        ecs.register("invulnerable", Kind.U8);          // rounds stop, damage does nothing
        // what it is *called*: an index into `World.names`, 0 for a hull with no
        // name at all (a car, a man, a base - they answer to their type).
        ecs.register("name_id", Kind.U16);
        // a round in the air that can itself be fought (`Systems/FirstHit.cs`,
        // `Systems/Mortality.cs`).  A plain bullet does not carry the tag.
        ecs.register("interceptable", Kind.U8);
        // ...and what a round *flies* as, one tag per kind (`Defs.KIND_TAGS`):
        // the same rule a hull's etype is a tag, so a flight system queries the
        // kind it knows instead of switching on `proj_kind`.
        ecs.register("bullet", Kind.U8);
        ecs.register("unguided_missile", Kind.U8);
        ecs.register("saclos", Kind.U8);
        ecs.register("top_attack", Kind.U8);

        // ---- health -----------------------------------------------------
        // One hull, one pool: `hp` is *the* health of the entity and `hp <= 0` is
        // what kills it.
        ecs.register("hp", Kind.F);
        ecs.register("hpmax", Kind.F);
        // A hull built out of separately damaged modules carries one extra pool
        // per arm, and *only* it does (the mech): the index is the weapon arm.
        ecs.register("arm_hp", Kind.F, 2, 0.0);
        ecs.register("arm_hpmax", Kind.F, 2, 0.0);
        // `dp` / `de`: penetration and blast armour, copied off the type at spawn
        // so the damage path never reads a definition dict.
        ecs.register("armor", Kind.F, 2, 0.0);
        // who put the last *lethal* hit in - the credit for a kill.
        ecs.register("last_hit", Kind.I, 1, -1);
        // ...and what killed it, frozen at the hit rather than looked up at the
        // death: the shooter may be a wreck by then, its id handed out again.
        ecs.register("last_name", Kind.U16);            // the killer's name id, 0 = none
        ecs.register("last_fac", Kind.U8);              // the killer's side, 255 = unknown
        ecs.register("last_etype", Kind.U8);            // the killer's type, for a nameless one
        ecs.register("last_wid", Kind.U8);              // the weapon that took the point

        // ---- weapons ----------------------------------------------------
        ecs.register("weapon", Kind.U8, 2, 0);          // 0 = left arm, 1 = right arm
        ecs.register("ammo", Kind.I, 2, 0);
        ecs.register("mag", Kind.I, 2, 0);
        ecs.register("cool", Kind.F, 2, 0.0);
        ecs.register("reload_t", Kind.F, 2, 0.0);
        ecs.register("trigger", Kind.U8);               // bit0 left, bit1 right
        ecs.register("trigger_prev", Kind.U8);          // last tick's bits, for semi auto
        // rearm work: seconds left at the wrench (`Systems/Rearm.cs`).
        ecs.register("work_remaining_time", Kind.F);

        // ---- ai ---------------------------------------------------------
        // Identity, never a type switch: `ai_crew` is put on at spawn by the one
        // `ECS.create` call (`World.spawn_unit(crew:)`) and is what every AI
        // system queries; a pilot's hull does not own it, so it is out of every
        // pass by construction.  The platform tags say how the crew aims, the
        // mission tags say what it is doing (`Systems/AI/Support/Tags.cs`).
        ecs.register("ai_crew", Kind.U8);
        ecs.register("mech_ai", Kind.U8);                // a mech crew: sight on the roof, two arms
        ecs.register("foot_ai", Kind.U8);                // a man: he turns his whole body onto it
        ecs.register("crew_engage", Kind.U8);            // this crew fights ground targets
        ecs.register("front_mission", Kind.U8);          // ...and its mission is the lane
        ecs.register("target", Kind.I, 1, -1);
        ecs.register("ai_state", Kind.U8);               // Systems/AI/Support/Tags.cs::AiState
        ecs.register("ai_timer", Kind.F);
        ecs.register("path_i", Kind.I, 1, 0);            // the driver's cursor along `nav_route`
        ecs.register("lane", Kind.U8);
        // the missile alarm: a round is coming for this hull, so the gun is the
        // intercept's this tick (`Systems/AI/Intercept.cs` and `Engage*`)
        ecs.register("ai_cover", Kind.U8);
        ecs.register("ai_threat", Kind.I, 1, -1);        // the round being fired at
        // the objective a mission is working towards (a town index, 255 = the
        // enemy command base)
        ecs.register("ai_goal", Kind.U8, 1, 255);
        // ---- intent: what the crew wants, never how it is achieved ----------
        // The crews write these four and nothing else that moves; the drivers
        // (`Systems/Movement/`) turn them into `vel_cmd`, wheel speed and
        // position.  `nav_route` is an index into `world.routes` (`Core/Nav.cs`),
        // -1 = steer at the point `nav_x` / `nav_z` instead.
        ecs.register("nav_route", Kind.I, 1, -1);
        ecs.register("nav_x", Kind.F);
        ecs.register("nav_z", Kind.F);
        ecs.register("nav_speed", Kind.F);
        // where an AI last *saw* its target: with the sight broken (smoke, a
        // crest, the hull ducking behind a building) it keeps shooting at that
        // spot for a few seconds instead of giving up
        ecs.register("spot_x", Kind.F);
        ecs.register("spot_y", Kind.F);
        ecs.register("spot_z", Kind.F);
        ecs.register("spot_t", Kind.F);

        // ---- raid (battle mode) -----------------------------------------
        // A raider is a *mission*, not a type: the hulls are ordinary mechs that
        // carry the `raider` tag, which is what takes them out of the lane AI's
        // hands (`Modes/Battle/Raids.cs`) and what `raids.run` queries.
        ecs.register("raider", Kind.U8);
        ecs.register("raid_party", Kind.U8);             // which launch a hull belongs to
        ecs.register("raid_stuck", Kind.F);              // seconds spent pushing at something
        ecs.register("raid_t", Kind.F);                  // seconds left of the HOLD phase

        // ---- misc -------------------------------------------------------
        // a hull a pilot dropped from the console (`debug_spawn`) on a mode whose
        // own units stay passive - a mission marker, not a type.
        ecs.register("range_drone", Kind.U8);
        ecs.register("flags", Kind.U8);
        ecs.register("home_x", Kind.F);                  // wandering civilians
        ecs.register("home_z", Kind.F);

        // ---- debris -----------------------------------------------------
        ecs.register("wreck_t", Kind.F);                 // seconds left before it fades
        ecs.register("wreck_kind", Kind.U8);             // 0 infantry 1 apc 2 mech

        // ---- blast ------------------------------------------------------
        ecs.register("blast", Kind.U8);                  // a burst in the air
        ecs.register("blast_power", Kind.F, 2, 0.0);     // [ed, er]
        ecs.register("blast_t", Kind.F);                 // seconds left (<= dt: one tick)
        ecs.register("blast_friendly", Kind.U8);         // 0 = enemies only, 1 = no sides
        ecs.register("blast_skip", Kind.I, 1, -1);       // already hit directly
        ecs.register("blast_pid", Kind.I, 1, -1);        // credit, resolved at creation
        ecs.register("blast_name", Kind.U16);            // the shooter's name id, 0 = none
        ecs.register("blast_etype", Kind.U8);            // ...and its type, if nameless
        ecs.register("blast_wid", Kind.U8);              // the weapon that left it

        // ---- smoke ------------------------------------------------------
        // A pull throws a lattice of `canister` entities; each one trades its
        // tag for `smoke` + `smoke_r` + `smoke_t`, an oblate ellipsoid that eats
        // missile lock-on.  Neither has `unit` or `faction`, so nothing can
        // target them and rounds fly straight through.
        ecs.register("canister", Kind.U8);               // in flight, not yet a cloud
        ecs.register("smoke", Kind.U8);                  // deployed puff
        ecs.register("smoke_r", Kind.F, 3, 0.0);         // ellipsoid semiaxes, metres
        ecs.register("smoke_t", Kind.F);                 // seconds to pop / of life left
        ecs.register("smoke_s", Kind.F, 1, 1.0);         // this canister's size factor

        // ---- projectile -------------------------------------------------
        ecs.register("proj_kind", Kind.U8);              // 0 bullet 1 rocket 2 saclos 3 top
        ecs.register("proj_wid", Kind.U8);
        ecs.register("proj_life", Kind.F);
        ecs.register("proj_src", Kind.I, 1, -1);
        // who is *owed* the round, frozen at the muzzle - the hull behind
        // `proj_src` can be a wreck by the time the round lands.
        ecs.register("proj_pid", Kind.I, 1, -1);         // the pilot who fired it
        ecs.register("proj_name", Kind.U16);             // ...the crew it belongs to
        ecs.register("proj_etype", Kind.U8);             // ...or its type, if nameless
        ecs.register("proj_age", Kind.F);
        ecs.register("proj_seed", Kind.F);               // per round, drives the thrust jitter
        // where the round started this tick: the prox fuse solves the leg it
        // just flew, because at 36 m per tick a distance check at the tick
        // boundaries would step clean over a man
        ecs.register("proj_prev", Kind.F, 3, 0.0);
        // metres flown since the muzzle: a wire-guided round has a maximum
        // flight distance and cooks off where it runs out
        ecs.register("proj_trav", Kind.F);

        // ---- first_hit_record -------------------------------------------
        // Where this tick's leg of the round ends.  Every collision system
        // offers its own answer through `if (t < first_hit_t)` and one `Impact`
        // reads the record, so the order the collision systems run in decides
        // the cost of a compare and nothing else.  `FIRST_HIT_ARMED` is
        // re-armed by the round's own flight system, which is the same moment
        // it writes `proj_prev`: `(proj_prev, pos)` is the leg and this is
        // where along it the round stops.
        ecs.register("first_hit_t", Kind.F, 1, Defs.FIRST_HIT_ARMED);
        // where it stops, written as a point by whoever got there first - the
        // fuse bursts a fraction of a metre into a leg and an expiring round
        // bursts where it stands, neither of which is the end of the leg
        ecs.register("first_hit_pos", Kind.F, 3, 0.0);
        ecs.register("first_hit_kind", Kind.U8);          // Defs.HIT_*: what it ran into
        ecs.register("first_hit_unit", Kind.I, 1, -1);    // the hull hit, once structures get ids
        ecs.register("first_hit_mod", Kind.U8);           // which module a hull hit charges
        // the gap the fuse measured, because a missile fused at 2.4 m takes its
        // splash at 2.4 m and not at the burst's own radius
        ecs.register("first_hit_gap", Kind.F);

        // ---- seeker -----------------------------------------------------
        // What the IR seeker is looking at, kept on the round rather than
        // derived from a live target id, because the target does not have to
        // survive the flight: `tgt_mem` is the point it stares at (the deck of
        // the hull it will dive on) and `tgt_vel` the velocity that hull had when
        // it was last seen (`Seeker.cs`).
        ecs.register("tgt_mem", Kind.F, 3, 0.0);
        ecs.register("tgt_vel", Kind.F, 3, 0.0);
    }

    // ---------------------------------------------------------------------
    // the component sets
    // ---------------------------------------------------------------------

    public static readonly string[] UNIT_COMPS = {
        "pos", "vel", "vel_cmd", "wheel_speed", "backout_t", "blocked_t",
        "yaw", "pitch", "leg_yaw", "turret_yaw",
        "sight_yaw", "sight_pitch", "etype", "faction", "name_id",
        "owner", "hp", "hpmax", "armor", "last_hit",
        "last_name", "last_fac", "last_etype", "last_wid",
        "weapon", "ammo", "mag", "cool", "reload_t",
        "trigger", "trigger_prev", "work_remaining_time", "target", "ai_state", "ai_timer",
        "path_i", "lane", "spot_x", "spot_y", "spot_z", "spot_t", "flags",
        "ai_cover", "ai_threat", "ai_goal",
        "nav_route", "nav_x", "nav_z", "nav_speed",
    };

    /// <summary>The extra pair of pools a modular hull (the mech) carries, added
    /// by the factory when the type declares the `arms` tag.</summary>
    public static readonly string[] ARM_COMPS = { "arm_hp", "arm_hpmax" };

    public static readonly string[] CIV_COMPS = Cat(UNIT_COMPS, "home_x", "home_z");

    /// <summary>a wreck is scenery: it has a place and no velocity.</summary>
    public static readonly string[] DEBRIS_COMPS = {
        "pos", "yaw", "pitch", "leg_yaw", "turret_yaw", "etype", "faction",
        "owner", "hp", "hpmax", "flags", "wreck_t", "wreck_kind",
    };

    public static readonly string[] BLAST_COMPS = {
        "pos", "faction", "blast_power", "blast_t", "blast_friendly",
        "blast_skip", "blast_pid", "blast_name", "blast_etype", "blast_wid",
        "blast",
    };

    /// <summary>Every round owns a place, a velocity and its `first_hit_record`.
    /// What it *flies* as is a tag from `Defs.KIND_TAGS` (bullet /
    /// unguided_missile / saclos / top_attack), the same rule a hull's type is a
    /// tag; `proj_kind` stays on board as the wire byte and the `Defs.WEAPONS`
    /// key, i.e. the round's `etype`.</summary>
    public static readonly string[] PROJ_COMPS = {
        "pos", "vel", "faction", "proj_kind", "proj_wid", "proj_life", "proj_src",
        "proj_pid", "proj_name", "proj_etype",
        "proj_age", "proj_seed", "proj_prev", "proj_trav", "target",
        "first_hit_t", "first_hit_pos", "first_hit_kind",
        "first_hit_unit", "first_hit_mod", "first_hit_gap",
    };

    /// <summary>The half of a round that can be fought: a pool of health and its
    /// own armour.  A *different archetype* from a bullet - the same rule the
    /// smoke canister and the puff obey.</summary>
    public static readonly string[] MISSILE_COMPS = Cat(PROJ_COMPS,
        "interceptable", "hp", "hpmax", "armor", "last_hit");

    /// <summary>The seeker's memory, and only the kind that has a seeker:
    /// `tgt_mem` is the point it stares at and `tgt_vel` the velocity that hull
    /// had when it was last seen (`Core/Seeker.cs`).  A rocket and a beam rider
    /// carry no such state.</summary>
    public static readonly string[] SEEKER_COMPS = Cat(PROJ_COMPS, "tgt_mem", "tgt_vel");

    public static readonly string[] CANISTER_COMPS = {
        "pos", "vel", "smoke_r", "smoke_t", "smoke_s", "canister",
    };

    public static readonly string[] PUFF_COMPS = {
        "pos", "smoke_r", "smoke_t", "smoke_s", "smoke",
    };

    /// <summary>A component list plus tags: the query a schema is built from
    /// (`Systems/Flights.cs` appends the round kind to the shared list).</summary>
    public static string[] Cat(string[] a, params string[] b)
    {
        var outv = new string[a.Length + b.Length];
        Array.Copy(a, outv, a.Length);
        Array.Copy(b, 0, outv, a.Length, b.Length);
        return outv;
    }
}
