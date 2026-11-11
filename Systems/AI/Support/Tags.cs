// The names of everything an AI crew is: its identity tags and its state word.
//
// A crew is a *component*, decided once at `World.spawn_unit(crew: true)` - never
// a type switch and never a per-row `owner` test.  Three questions are answered
// separately, because they are separate things:
//
//   * who drives - `ai_crew` (every AI hull owns it, and no pilot's hull does);
//   * how it aims - `mech_ai` (a sight on the roof and two arms), `foot_ai` (a man
//     turns his whole body onto it), `vehicle` (its own type's tag: a turret on a
//     hull the driver steers);
//   * what it is doing - `crew_engage` (it fights ground targets) plus a mission
//     tag (`front_mission`, `raider`).
//
// A query is a conjunction, so the tags are positive: `March`-style "moves but is
// not wheeled" would need a per-row subtraction and a tag does not.  A mech crew
// carries `ai_crew` + `mech_ai`; a raider carries the same two plus `raider`;
// a defence post carries them with no `crew_engage` at all, which is why it never
// picks a ground fight and still shoots missiles down.

namespace Mech.Server.Systems.AI;

public static class AiTags
{
    public const string CREW = "ai_crew";         // driven by a crew
    public const string MECH = "mech_ai";         // ...and it is a mech's crew
    public const string FOOT = "foot_ai";         // ...or a man's
    public const string ENGAGE = "crew_engage";   // it fights ground targets
    public const string FRONT = "front_mission";  // ...on the lane
    public const string RAID = "raider";          // ...on a raiding party's own route
}

/// <summary>The one state word every policy writes (`ai_state`) and the only
/// vocabulary there is.  A mission reads and writes its own subset of it: the lane
/// policy drives ADVANCE / ENGAGE / SUPPRESS, a raiding party ADVANCE (marching) /
/// ASSAULT (standing in the ring) / HOLD (keeping it).
///
/// The states at the end are the ones this scaffold is *for* - the hooks are the
/// policy's `decide` and nothing else, and they are named here so a new tactic has
/// one word to claim rather than a new component.</summary>
public static class AiState
{
    public const byte ADVANCE = 0;      // follow the route the mission chose
    public const byte ASSAULT = 1;      // stand in the capture ring
    public const byte HOLD = 2;         // the town is ours: stay and keep it
    public const byte ENGAGE = 3;       // a target is in sight: work it
    public const byte SUPPRESS = 4;     // lost it: keep firing at where it was

    // not implemented yet: a policy case and an intent, nothing else (see
    // `Systems/AI/Strategies/Front.cs`)
    public const byte ATTACK_FLANK = 5;
    public const byte CAPTURE = 6;
    public const byte WITHDRAW = 7;
    public const byte RESUPPLY = 8;
}
