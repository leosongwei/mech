// The sight leads, the machine follows.
//
// Ported from `backend/server/systems/gunnery.py`.
//
// A mech's aim is a free turret on the roof.  It answers the crew - a pilot's
// mouse or the AI's intercept solution - *instantly*, and everything the crew
// sees is seen through it: the eye the camera sits in (`MathUtil.sight_eye`),
// the IR seeker, and the beam a SACLOS missile rides.
//
// The hull underneath and the arms beside it are steel on pivots, so they can
// only catch up: `sight_yaw` is where the sight points, `yaw` swings after it at
// `MECH_HULL_RATE` and the arms' `pitch` climbs after it at `MECH_ARM_RATE`.
// Flick the view faster than that and the machine lags the cross - the barrels,
// and the impact markers the client draws off them, trail behind the reticle for
// a moment.
//
// Because the sight stands `Defs.MECH_SIGHT_Z` behind the hull's rotation axis,
// that catch-up is not free of side effects: a hull swinging under a sight that
// already points somewhere else carries the sight - and the pilot's eye - round
// an arc.  `MathUtil.sight_eye` is that geometry; this system is what makes it
// happen.
//
// Every other kind of unit aims with its whole mount and has nothing to catch up
// with, so its sight is simply mirrored from the mount it aims with: the hull for
// a rifleman or a civilian, the turret for a carrier.  That keeps one meaning for
// `sight_yaw` on the wire - where the crew is looking - whoever is reading it.

namespace Mech.Server.Systems;

public static class Gunnery
{
    // ------------------------------------------------------------------
    // the columns a sight needs
    // ------------------------------------------------------------------

    /// <summary>Every mount's sight lines: where the crew looks and where the
    /// metal points.  `has_vehicle` is the one branch a whole archetype takes - a
    /// carrier's hull belongs to its driver, so its sight is its turret.</summary>
    public sealed class SightColumns : Columns<SightRow>
    {
        // bound by `bind`, which the constructor calls: hence the `null!`
        public float[] yaw = null!, pitch = null!;
        public float[] sight_yaw = null!, sight_pitch = null!;
        public float[] turret_yaw = null!;
        public byte[] etype = null!;
        public bool has_vehicle;

        internal static readonly string[] names =
            { "sight_yaw", "sight_pitch", "yaw", "pitch", "turret_yaw", "etype" };

        public SightColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            yaw = g.f("yaw"); pitch = g.f("pitch");
            sight_yaw = g.f("sight_yaw"); sight_pitch = g.f("sight_pitch");
            turret_yaw = g.f("turret_yaw");
            etype = g.u8("etype");
            has_vehicle = g.has("vehicle");
        }

        public override SightRow row(int i) => new SightRow(this, i);
    }

    /// <summary>One mount: the angles of the tick, read and written in place.</summary>
    public readonly struct SightRow : IRow<SightColumns>
    {
        private readonly SightColumns _c;
        public readonly int i;

        public SightRow(SightColumns c, int i) { _c = c; this.i = i; }

        public SightColumns columns => _c;
        public int index => i;
        public int eid => _c.ids[i];
        public bool has_vehicle => _c.has_vehicle;

        public ref float yaw => ref _c.yaw[i];
        public ref float pitch => ref _c.pitch[i];
        public ref float sight_yaw => ref _c.sight_yaw[i];
        public ref float sight_pitch => ref _c.sight_pitch[i];
        public float turret_yaw => _c.turret_yaw[i];
        public byte etype => _c.etype[i];
    }

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        float hull_step = Defs.MECH_HULL_RATE * dt;
        float arm_step = Defs.MECH_ARM_RATE * dt;
        foreach (var g in e.groups(SightColumns.names))
        {
            var c = new SightColumns(g);
            if (c.has_vehicle)
            {
                // a carrier's hull belongs to the driver - `yaw` is its steering
                // state - and the crew turns nothing but the turret, which the AI
                // turns directly: its sight *is* the turret
                foreach (var r in c)
                {
                    r.sight_yaw = r.turret_yaw;
                    r.sight_pitch = r.pitch;
                }
                continue;
            }
            // a rifle is aimed with the man's whole body: nothing lags anything
            foreach (var r in c)
            {
                if (r.etype != Defs.ETYPE_MECH)
                {
                    r.sight_yaw = r.yaw;
                    r.sight_pitch = r.pitch;
                    continue;
                }
                r.yaw = MathUtil.turn_toward(r.yaw, r.sight_yaw, hull_step);
                // the mount's mechanical stops: the sight may look higher than
                // the arms can elevate, and an arm sits against its stop until
                // the sight comes back into the envelope
                float want = Math.Clamp(r.sight_pitch, Defs.MECH_PITCH_MIN, Defs.MECH_PITCH_MAX);
                r.pitch = Math.Clamp(
                    MathUtil.turn_toward(r.pitch, want, arm_step),
                    Defs.MECH_PITCH_MIN, Defs.MECH_PITCH_MAX);
            }
        }
    }
}
