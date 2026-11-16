// `GET /events/kills`: every kill a pilot is on one end of, as server-sent events.
//
// For a reader outside the game - a script, an overlay, a second screen - and best
// effort on purpose: a viewer that cannot keep up drops kills rather than holding
// the tick up, a late subscriber hears the next kill rather than the last, and
// nothing is stored anywhere between the simulation and this queue.  A reader is a
// listener, not a pilot: it never un-pauses an empty server, it only gets the
// heartbeat (`Server.kill_stream_endpoint`).
//
// The record itself is built by the simulation (`Systems/Support/KillFeed.cs`,
// which publishes it through the one hook assigned here), so the words on this
// wire and the line on the HUD come from the same freeze - `Combat.HitAttributions`
// - and cannot disagree about who killed whom, with what, or from how far.

using System.Text.Json;
using System.Threading.Channels;

namespace Mech.Server;

public static class KillStream
{
    // A viewer an hour behind is not a viewer: 64 kills of queue is minutes of a
    // battle, and past that the oldest goes.
    private const int QUEUE = 64;

    /// <summary>One connected reader's queue.  `Dispose` takes it off the list and
    /// ends the frame loop waiting on it.</summary>
    public sealed class Sub : IDisposable
    {
        private readonly Channel<string> _ch = Channel.CreateBounded<string>(
            new BoundedChannelOptions(QUEUE) { FullMode = BoundedChannelFullMode.DropOldest });
        private bool _closed;

        public ChannelReader<string> reader => _ch.Reader;

        internal void push(string frame) => _ch.Writer.TryWrite(frame);

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _ch.Writer.TryComplete();
            _drop(this);
        }
    }

    private static readonly List<Sub> _subs = new();

    public static Sub subscribe()
    {
        var s = new Sub();
        lock (_subs) _subs.Add(s);
        return s;
    }

    private static void _drop(Sub s)
    {
        lock (_subs) _subs.Remove(s);
    }

    /// <summary>One kill, written once and handed to every reader.</summary>
    public static void publish(Systems.KillFeed.KillRecord rec)
    {
        lock (_subs)
        {
            if (_subs.Count == 0) return;         // nobody reading: keep the tick free
            string frame = "data: " + JsonSerializer.Serialize(_payload(rec), Defs.WIRE_JSON)
                           + "\n\n";
            foreach (var s in _subs) s.push(frame);
        }
    }

    private static Dictionary<string, object?> _payload(Systems.KillFeed.KillRecord rec) => new()
    {
        ["killed"] = new Dictionary<string, object?>
        {
            ["name"] = rec.victim,
            ["is_player"] = rec.victim_is_player,
        },
        ["by"] = new Dictionary<string, object?>
        {
            ["name"] = rec.killer,
            ["is_player"] = rec.killer_is_player,
        },
        // the weapon's name as the HUD says it: `Defs` keeps the table in capitals
        // (`30mm AUTOCANNON`), and both readers of this name write it small
        ["weapon"] = rec.weapon.ToLowerInvariant(),
        // metres, one decimal: the same number the kill line writes as m or km
        ["distance"] = MathF.Round(rec.distance, 1),
    };
}
