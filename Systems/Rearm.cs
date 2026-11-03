// Rearm work: the wrench, not just the bill.
//
// Ported from `backend/server/systems/rearm.py`.
//
// A trade in `Shop` is booked on the pilot instead of landing on the mech on the
// spot.  Fitting a weapon, loading its racks and welding armour back on all take
// seconds, and while the job runs the machine is held still with its triggers
// dead - that is what stops a repair inside an owned town from doubling as a
// mid-fight reload.  The order is charged when it is booked.
//
// `work_remaining_time` is the timer, a plain float on the mech: the shop writes
// the seconds the draft cost into it, this system counts it down every tick and,
// when it reaches zero, applies the bought lines.  Everything else that cares
// (`Systems/PlayerInput.cs` zeroing the movement and fire bits) just asks whether
// it is still above zero.  A pilot whose mech is destroyed mid-job loses the job
// - and the money, it was spent when the deal was booked.

namespace Mech.Server.Systems;

public static class Rearm
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var rem = e.fv("work_remaining_time");
        foreach (var pl in world.players.Values)
        {
            int eid = pl.mech;
            if (pl.work is null)
            {
                if (eid >= 0 && e.is_alive(eid) && rem[eid] > 0f)
                    rem[eid] = 0f;          // a timer with no order behind it
                continue;
            }
            if (eid < 0 || !e.is_alive(eid))
            {
                pl.end_work();              // the hull went up with the wrench out
                continue;
            }
            // the last chunk of work is the one that finishes the job, whether
            // the tick had a whole second of it or a hundredth
            rem[eid] = MathF.Max(0f, rem[eid] - dt);
            if (rem[eid] > 0f) continue;
            var work = pl.work;
            foreach (var line in work) Shop.apply_line(world, pl, line);
            string label = Shop.work_label(work);
            pl.end_work();
            world.emit("rearm", ("pid", pl.pid), ("label", label));
        }
    }
}
