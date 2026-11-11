// The shared case table (`tests/cases/fcs.json`), read the way the front end reads
// it: one row is one `FireControl.Solve(shot, track)` call, so the two hosts are
// answering the same questions rather than two similar ones.
//
// Only the weapon fields the solver reads are taken from the file, and the row's
// `accAge` is applied here - a pull measured more than half a second ago is not a
// pull - so a test can assert the ageing rule without a stopwatch.

using System.Text.Json;

using Mech.Server;

namespace Mech.Tests;

public sealed class Case
{
    public string id = "";
    public string tag = "";
    public string pair = "";
    public string why = "";
    public FireControl.Shot shot;
    public FireControl.Track track;
}

public static class Cases
{
    public static readonly List<Case> All = new();

    /// <summary>s; a measurement this old says nothing about now (the same figure the
    /// wire's own estimate uses, `tests/frontend/fcs/cases.mjs`).</summary>
    public const float AccelStale = 0.5f;

    public static void Load(string path)
    {
        All.Clear();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var row in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var c = new Case
            {
                id = row.GetProperty("id").GetString() ?? "",
                tag = row.TryGetProperty("tag", out var tag) ? tag.GetString() ?? "" : "",
                pair = row.TryGetProperty("pair", out var pair) ? pair.GetString() ?? "" : "",
                why = row.TryGetProperty("why", out var why) ? why.GetString() ?? "" : "",
            };
            var shot = row.GetProperty("shot");
            var wd = shot.GetProperty("wd");
            var weapon = new WeaponDef
            {
                key = wd.GetProperty("kind").GetString() ?? "bullet",
                kind = wd.GetProperty("kind").GetString() ?? "bullet",
                vel = wd.GetProperty("vel").GetSingle(),
                g = wd.GetProperty("g").GetSingle(),
                thrust = wd.GetProperty("thrust").GetSingle(),
                maxvel = wd.GetProperty("maxvel").GetSingle(),
                life = wd.GetProperty("life").GetSingle(),
                bias = wd.GetProperty("bias").GetSingle(),
                hit_r = wd.GetProperty("hit_r").GetSingle(),
                spread = wd.GetProperty("spread").GetSingle(),
                range = wd.GetProperty("range").GetInt32(),
            };
            c.shot = new FireControl.Shot(ReadVec3(shot.GetProperty("muzzle")),
                                          ReadVec3(shot.GetProperty("eye")), weapon,
                                          shot.GetProperty("zero").GetSingle(),
                                          shot.GetProperty("maxElev").GetSingle());
            var track = row.GetProperty("track");
            var acc = track.GetProperty("acc");
            bool stale = track.GetProperty("accAge").GetSingle() > AccelStale;
            c.track = new FireControl.Track(
                ReadVec3(track.GetProperty("pos")),
                ReadVec3(track.GetProperty("vel")),
                stale ? default : new Vec3(acc[0].GetSingle(), 0f, acc[1].GetSingle()),
                track.GetProperty("aim").GetSingle(),
                track.GetProperty("radius").GetSingle());
            All.Add(c);
        }
    }

    public static Case? ById(string id) => All.FirstOrDefault(c => c.id == id);

    private static Vec3 ReadVec3(JsonElement e) =>
        new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
}
