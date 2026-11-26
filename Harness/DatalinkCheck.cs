using System.Buffers.Binary;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class DatalinkCheck
{
    private const float DT = 1f / 30f;

    public static int run()
    {
        var world = new World(7, "range");
        world.systems.Remove(Modes.Range.Targets.run);
        world.systems.Remove(Modes.Range.Interceptor.run);
        var e = world.ecs;
        Clean(world);
        var (sx, sz) = world.terrain.bases[Defs.FACTION_RED];
        int observer = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, sx, sz,
                                        weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
        int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx + 500f, sz,
                                      weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
        int foot = world.spawn_unit(Defs.ETYPE_INF_RIFLE, Defs.FACTION_BLUE, sx + 520f, sz + 20f,
                                    crew: false);
        int bad = 0;
        Console.WriteLine("datalink: shared vehicle observation and faction-filtered snapshots");

        world.update(DT);
        bool independent = Datalink.known(world, Defs.FACTION_RED, target)
                            && !Datalink.known(world, Defs.FACTION_BLUE, target);
        bool vehicles_only = e.has(observer, "link_seen") && e.has(target, "link_seen")
                             && !e.has(foot, "link_seen");
        bool stamped = e.iv("link_seen_tick").get(target, Defs.FACTION_RED) == world.tick;
        Line("observe", $"red {Datalink.known(world, Defs.FACTION_RED, target)}, "
                         + $"blue {Datalink.known(world, Defs.FACTION_BLUE, target)}, "
                         + $"infantry component {e.has(foot, "link_seen")}",
             independent && vehicles_only && stamped, ref bad);

        var tp = e.fv("pos").get3(target);
        int second = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_RED, tp.x + 100f, tp.z,
                                      weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
        var eye = Weapons.aim_of(world, observer).Item1;
        var body = new Vec3(tp.x, tp.y + Defs.UNIT_DEFS[Defs.ETYPE_APC].height * 0.5f, tp.z);
        var mid = new Vec3((eye.x + body.x) * 0.5f, (eye.y + body.y) * 0.5f,
                           (eye.z + body.z) * 0.5f);
        int puff = e.create(Components.PUFF_COMPS);
        e.fv("pos").set3(puff, mid);
        e.fv("smoke_r").set3(puff, Defs.SMOKE_FLAT[0], Defs.SMOKE_FLAT[0], Defs.SMOKE_FLAT[0]);
        e.fv("smoke_t")[puff] = Defs.SMOKE_LIFE;
        e.fv("smoke_s")[puff] = 1f;
        world.update(DT);
        bool union = Sight.why(world, eye, body) == "smoke"
                     && Datalink.known(world, Defs.FACTION_RED, target);
        e.destroy(second);
        world.update(DT);
        int last = e.iv("link_seen_tick").get(target, Defs.FACTION_RED);
        bool lost = !Datalink.known(world, Defs.FACTION_RED, target) && last < world.tick;
        Line("union-loss", $"second observer kept contact {union}; all lost {lost}; last tick {last}",
             union && lost, ref bad);

        var view = new Netcode.SnapshotView { faction = Defs.FACTION_RED, x = tp.x, z = tp.z };
        byte[] hidden = Netcode.snapshot_bytes(world, view);
        bool no_row = FindUnit(hidden, target) < 0;
        byte[] contacts = Netcode.contacts_bytes(world, Defs.FACTION_RED);
        bool no_contact = !HasContact(contacts, Defs.ETYPE_APC, Defs.FACTION_BLUE);
        bool no_people = !HasContact(contacts, Defs.ETYPE_INF_RIFLE, Defs.FACTION_BLUE);
        bool soft_row = UnitCount(hidden, 4) > 0;
        Line("filter", $"enemy row {!no_row}, enemy contact {!no_contact}, "
                       + $"infantry contact {!no_people}, ordinary infantry row {soft_row}",
             no_row && no_contact && no_people && soft_row, ref bad);

        e.destroy(puff);
        world.update(DT);
        byte[] shown = Netcode.snapshot_bytes(world, view);
        int row = FindUnit(shown, target);
        bool linked_row = row >= 0 && (shown[row + 5] & (1 << 6)) != 0;
        bool linked_contact = HasContact(Netcode.contacts_bytes(world, Defs.FACTION_RED),
                                         Defs.ETYPE_APC, Defs.FACTION_BLUE);
        Line("restore", $"row {row >= 0}, link bit {linked_row}, minimap {linked_contact}",
             linked_row && linked_contact, ref bad);

        int friendly = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_RED, tp.x + 80f, tp.z + 80f,
                                        weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
        world.update(DT);
        byte[] allies = Netcode.snapshot_bytes(world, view);
        int fr = FindUnit(allies, friendly);
        bool ally_link = fr >= 0 && (allies[fr + 5] & (1 << 6)) != 0;
        Line("friendly", $"row {fr >= 0}, shared position bit {ally_link}", ally_link, ref bad);

        int missile = world.spawn_round(-1, Defs.FACTION_BLUE, Defs.WID_SACLOS, 2,
                                        new Vec3(tp.x + 200f, tp.y + 100f, tp.z),
                                        new Vec3(0f, 0f, 100f), 5f, -1);
        world.update(DT);
        bool missile_link = Datalink.missile_known(world, Defs.FACTION_RED, missile);
        byte[] missileFrame = Netcode.snapshot_bytes(world, view);
        int pr = FindProjectile(missileFrame, missile);
        bool missile_bit = pr >= 0 && (missileFrame[pr + 5] & 1) != 0;
        Line("missile", $"observed {missile_link}, wire bit {missile_bit}", missile_link && missile_bit, ref bad);
        Guidance(ref bad);

        e.validate();
        return bad == 0 ? 0 : 1;
    }

    private static void Guidance(ref int bad)
    {
        var world = new World(7, "range");
        world.systems.Remove(Modes.Range.Targets.run);
        world.systems.Remove(Modes.Range.Interceptor.run);
        var e = world.ecs;
        Clean(world);
        var (sx, sz) = world.terrain.bases[Defs.FACTION_RED];
        var pl = world.add_player("LINK-PILOT");
        e.fv("pos").set3(pl.mech, sx, e.fv("pos").get3(pl.mech).y, sz);
        int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx + 700f, sz,
                                      weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
        var tp = e.fv("pos").get3(target);
        var body = new Vec3(tp.x, tp.y + 1.4f, tp.z);
        var eye = Weapons.aim_of(world, pl.mech).Item1;
        var mid = new Vec3((eye.x + body.x) * 0.5f, (eye.y + body.y) * 0.5f,
                           (eye.z + body.z) * 0.5f);
        int puff = e.create(Components.PUFF_COMPS);
        e.fv("pos").set3(puff, mid);
        e.fv("smoke_r").set3(puff, Defs.SMOKE_FLAT[0], Defs.SMOKE_FLAT[0], Defs.SMOKE_FLAT[0]);
        e.fv("smoke_t")[puff] = Defs.SMOKE_LIFE;
        e.fv("smoke_s")[puff] = 1f;
        Aim(world, pl.mech, target);
        world.update(DT);
        bool blind = pl.lock_target < 0;
        world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_RED, tp.x + 100f, tp.z,
                         weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
        world.update(DT);

        var hull = e.arch_of(pl.mech)!;
        int row = e.row_of(pl.mech);
        hull.u8("weapon")[row * 2 + 1] = (byte)Defs.WID_SACLOS;
        hull.i("mag")[row * 2 + 1] = 4;
        pl.fire = (false, true);
        world.update(DT);
        pl.fire = (false, false);
        bool saclos = e.query("saclos").Length > 0;
        bool beam_ok = false, beam_lit = false;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
        {
            beam_ok |= e.uv("beam_ok").get(pl.mech, ch) != 0;
            beam_lit |= e.uv("beam_lit").get(pl.mech, ch) != 0;
        }
        Line("link-lock", $"blind {blind}, lock {pl.lock_target}/{target}, "
                           + $"link {Datalink.known(world, Defs.FACTION_RED, target)}, "
                           + $"source {Visibility.source(world, pl.mech, target, body)}",
             blind && pl.lock_target == target
             && Visibility.source(world, pl.mech, target, body) == VisibilitySource.Datalink, ref bad);
        Line("link-saclos", $"round {saclos}, track ok {beam_ok}, physically lit {beam_lit}",
             saclos && beam_ok && !beam_lit, ref bad);

        foreach (int id in world.projectiles()) e.destroy(id);
        Beams.free_all(world, pl.mech);
        pl.lock_target = -1;
        hull.u8("weapon")[row * 2 + 1] = (byte)Defs.WID_TOPATTACK;
        hull.i("mag")[row * 2 + 1] = 4;
        hull.f("cool")[row * 2 + 1] = 0f;
        world.update(DT);               // release the semi-automatic trigger edge
        pl.fire = (false, true);
        world.update(DT);
        pl.fire = (false, false);
        bool top = e.query("top_attack").Length > 0;
        Line("link-top", $"lock {pl.lock_target}/{target}, alive {e.is_alive(target)}, "
                          + $"link {Datalink.known(world, Defs.FACTION_RED, target)}, round {top}",
             top, ref bad);
        e.validate();
    }

    private static void Aim(World world, int eid, int target)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(target);
        var eye = Weapons.aim_of(world, eid).Item1;
        float dy = p.y + Defs.UNIT_DEFS[e.uv("etype")[target]].height * 0.5f - eye.y;
        float dx = p.x - eye.x, dz = p.z - eye.z;
        var yaw = e.row_f(eid, "sight_yaw", out int r1);
        var pitch = e.row_f(eid, "sight_pitch", out int r2);
        yaw[r1] = MathF.Atan2(dz, dx);
        pitch[r2] = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
    }

    private static int FindUnit(byte[] buf, int id)
    {
        int off = Netcode.HEADER;
        for (int c = 0; c < Netcode.ROWS; c++)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(Netcode.CLOCK + c * 2));
            int stride = Netcode.stride_of(c);
            for (int k = 0; k < n; k++, off += stride)
                if (BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off)) == id) return off;
        }
        return -1;
    }

    private static int FindProjectile(byte[] buf, int id)
    {
        int off = Netcode.HEADER;
        for (int c = 0; c < Netcode.ROWS; c++)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(Netcode.CLOCK + c * 2));
            off += n * Netcode.stride_of(c);
        }
        int np = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(Netcode.CLOCK + 10));
        for (int k = 0; k < np; k++, off += Netcode.PROJ_STRIDE)
            if (BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(off)) == id) return off;
        return -1;
    }

    private static int UnitCount(byte[] buf, int cls) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(Netcode.CLOCK + cls * 2));

    private static bool HasContact(byte[] buf, int etype, int faction)
    {
        for (int off = 0; off < buf.Length; off += Netcode.CONTACT_STRIDE)
            if (buf[off + 4] == faction && buf[off + 5] == etype) return true;
        return false;
    }

    private static void Clean(World world)
    {
        var e = world.ecs;
        foreach (int id in e.query())
        {
            if (!e.has(id, "etype")) continue;
            if (e.uv("etype")[id] == Defs.ETYPE_BASE) continue;
            e.destroy(id);
        }
        foreach (int id in world.projectiles()) e.destroy(id);
    }

    private static void Line(string name, string detail, bool ok, ref int bad)
    {
        Console.WriteLine($"{name,-12} {detail}  {(ok ? "ok" : "FAIL")}");
        if (!ok) bad++;
    }
}
