// The physics a round is made of that the barrel has to know: the constants the model
// is written in, the frontal area a calibre implies, and the one flight that has to be
// flown outside a world.
//
// In the simulation a round is flown by eleven passes (`Systems/Flights/`), and the
// arithmetic of each force lives in the pass that applies it.  One caller has no world:
// the fire control solves a barrel's angle by flying the round it is about to fire
// (`Ballistics.flight_at`), and that round is unguided - no seeker, no beam, no lift
// demand - so `slide` below is the whole of what Core keeps.  The guided forms of the
// same airframe are the two guidance passes plus the seven stages in
// `Systems/Flights/Airframe.cs`, and `--check airframe` is what keeps those and `slide`
// the same law.

namespace Mech.Server;

public static class Flight
{
    public const float RHO = 1.225f;        // kg/m³, sea level - the apex is under 1 km
    public const float GRAVITY = 9.81f;     // m/s²
    public const float MACH = 340f;         // m/s, "0.9 M" in this model

    /// <summary>Frontal area from the calibre - the round's own size, not a knob.</summary>
    public static float area(WeaponDef wd)
    {
        float r = wd.dia * 0.5f;
        return MathF.PI * r * r;
    }

    /// <summary>Fly an unguided round for one tick: the airframe with nothing asked of
    /// it, which is the whole difference between a rocket and a missile, and the arc the
    /// fire control solves its elevation against.
    ///
    /// A rocket has no seeker, no beam and no wing: there is no point to fly to and no
    /// lift to spend, so its nose stays on the flight path, thrust goes along it for
    /// `burn` seconds and drag comes off it from the speed it is doing - profile drag
    /// plus the transonic rise, from the same polar the two guided kinds pay.  Past
    /// burnout the speed *decays*: 610 m/s at the end of the pod's motor is ~550 m/s at
    /// 1 km, and the round is 6.6 m lower there than a constant-speed one would be,
    /// which is what the fire control is solved against.
    ///
    /// The round's own size is `dia`, its weight `mass`, and `maxvel` is the structure's
    /// ceiling, not the motor's: the motor's window is `ignite` .. `ignite + burn`, and
    /// past either limit the cue is out.  `age` is the seconds the round has been in the
    /// air, so `Ballistics.flight_at` and the round in flight are the same function and
    /// the barrel's angle and the round's arc are the same line.</summary>
    public static Vec3 slide(Vec3 v, float age, WeaponDef wd, float dt)
    {
        float speed = v.len;
        var vhat = v.unit;
        float q = 0.5f * RHO * speed * speed;
        float push = age > wd.ignite && age <= wd.ignite + wd.burn && speed < wd.maxvel
            ? wd.thrust
            : 0f;
        // profile drag and the transonic rise; nothing is asked of the wing, so there is
        // no induced term
        float over = MathF.Max(0f, speed / MACH - 0.80f);
        float cd = wd.cd0 + 0.4f * over * over;
        float drag = q * area(wd) * cd / wd.mass;
        // the nose stays on the flight path, so thrust and drag are the same line
        var acc = -vhat * drag + vhat * push;
        acc = new Vec3(acc.x, acc.y - GRAVITY, acc.z);
        var nv = new Vec3(v.x + acc.x * dt, v.y + acc.y * dt, v.z + acc.z * dt);
        float ns = nv.len;
        if (ns <= wd.maxvel) return nv;
        float k = wd.maxvel / ns;
        return new Vec3(nv.x * k, nv.y * k, nv.z * k);
    }
}
