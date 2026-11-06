// ASP.NET Core host: static client, websocket game loop.
//
// Ported from `backend/server/main.py`.  Three pieces, the same three the Python
// file has:
//
//   * the routes      `GET /` (the built client), `/static/*` (the same
//                     `backend/assets` directory the Python server serves, so a
//                     frontend rebuild needs no restart), `GET /ws`
//   * the game loop   a hosted background service on a fixed 30 Hz step: update
//                     the world, send a snapshot every `SNAP_EVERY` ticks, the
//                     fx events every tick, a meta payload every `META_EVERY`
//   * the console     every message `ws_endpoint` in main.py answers, with the
//                     same semantics and the same `info` strings
//
// Nothing here ever blocks the tick on a socket: a connection has an outbound
// queue of 24 frames and a writer task of its own, and a client that falls
// behind simply loses frames (`Client.dropped`).
//
// One thing Python does not need and this does: `_wlock`.  asyncio runs the loop
// and the message handlers on one thread, so a message lands either before or
// after a whole tick and never inside one; here the handlers are Kestrel
// threads, so they take the same lock the tick holds.  A handler waits at most
// one tick (33 ms) and it is never the loop that waits.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Mech.Server;

/// <summary>
/// Per connection outbound queue.
///
/// Sends are decoupled from the simulation loop: a slow or half dead socket can
/// never stall the 30 Hz tick for everybody else, frames are simply dropped when
/// the socket falls behind.
/// </summary>
public sealed class Client
{
    public WebSocket ws;
    public int pid;
    public int dropped;
    // this pilot's interest sphere: the tier table is per eye, so his snapshot
    // is built for him alone (`Netcode.SnapshotView`)
    public readonly Netcode.SnapshotView view = new();
    // --- the frame clock -------------------------------------------------
    // **One binary frame every tick, always.**  A snapshot on every second
    // tick, and the 16 B clock block on all the others - so the client's sense
    // of the server's clock (and the round trip it measures on `echo`) never
    // depends on whether a snapshot happened to go out.
    //
    // While a pilot is *held* he gets the clock block only: a jammed link cannot
    // use a lower frame rate, because every frame queued in front of the
    // picture is one more it has to crawl through.  Nothing built is ever
    // discarded - a "lost" snapshot is one that was never built.
    //
    // Congestion is **queueing, not distance**: `behind` is how many ticks old
    // the newest frame this pilot has seen is, and what matters is how far that
    // is above the *floor* of the same measurement on his own path - a player
    // 200 ms away on a clean line is not congested, the same player with 400 ms
    // of queue in front of him is.  `floor` falls instantly and rises slowly,
    // so it tracks the path and forgets a bad minute.
    public int ack = -1;            // newest tick this pilot has *seen* (`st`)
    public int echo = -1;           // the ack to send back on the next frame
    public bool held;               // congested: clock blocks only
    public int holds;               // clock blocks sent while held
    public int behind;              // ticks between his newest seen frame and now
    public float floor_ms = 1e9f;   // the best `behind` this path has shown, ms
    public int held_ticks;          // how long he has been held, for the escape hatch
    public long heard;              // Stopwatch timestamp of his last message
    public int due;                 // snapshots this second should have gone out
    public int skipped;             // ... and did not (held)
    public int loss_x10000;         // the last closed second, in 0.01 %
    private static readonly ILogger log = MechLog.get("mech.main");

    // thresholds are *times*, turned into ticks once here, so a different tick
    // rate cannot silently change what "400 ms behind" means
    public const float TARGET_HZ = 30f;
    public const float HOLD_SLACK_MS = 200f;    // loose: we have players an ocean away
    public const float HOLD_PCT = 0.75f;        // ... and three quarters above their own floor
    public const float FLOOR_RISE_MS_S = 2f;    // how fast the floor forgets a bad path
    public const float FLOOR_RISE_HELD_MS_S = 20f;  // ... and how fast it lets a held one out
    public const float HELD_PATIENCE_S = 10f;   // memory of a path change takes this long
    public const float MAX_FLOOR_MS = 2000f;    // no path we serve is slower than this
    public const double HEARTBEAT_TIMEOUT = 0.4; // s of silence = congested
    public const int WINDOW = (int)TARGET_HZ;    // ticks in the loss window (1 s)

    /// <summary>Read the clock off the pilot's heartbeat: how far behind he is,
    /// what the floor of his path is, and whether he is congested.  Called for
    /// every tick (the ping is a frame too), so recovery is a tick away.</summary>
    public void observe(int tick)
    {
        double silent = System.Diagnostics.Stopwatch.GetElapsedTime(heard).TotalSeconds;
        if (ack < 0)
        {
            // he has not reported a tick yet, so there is no *distance* to judge
            // - only silence can hold him, and a client that never reports one
            // (an old client, `st` absent) is never held while it talks
            behind = 0;
            held = silent > HEARTBEAT_TIMEOUT;
            held_ticks = 0;
            return;
        }
        behind = tick - ack;
        float behind_ms = behind * (1000f / TARGET_HZ);
        // the floor falls instantly (always take the better sample) and rises at
        // a crawl, so the picture of "this path" is the best it has been lately
        // rather than a standing queue being mistaken for the path
        float rise = held_ticks > (int)(HELD_PATIENCE_S * TARGET_HZ)
            ? FLOOR_RISE_HELD_MS_S : FLOOR_RISE_MS_S;
        // a floor is a path, and no path we serve is slower than this: the cap
        // keeps one bogus sample from blinding the detector
        floor_ms = MathF.Min(MathF.Min(behind_ms, MAX_FLOOR_MS), floor_ms + rise / TARGET_HZ);
        if (floor_ms < 0f) floor_ms = 0f;
        float slack = MathF.Max(HOLD_SLACK_MS, floor_ms * HOLD_PCT);
        float extra = behind_ms - floor_ms;
        if (!held && (silent > HEARTBEAT_TIMEOUT || extra >= slack))
        {
            held = true;
            held_ticks = 0;
            log.LogDebug($"pid {pid} held: {behind_ms:0} ms behind (floor {floor_ms:0}, "
                         + $"slack {slack:0}), silent {silent:0.00}s");
        }
        else if (held)
        {
            held_ticks += 1;
            if (extra <= slack / 2 && q.Reader.Count == 0)
            {
                held = false;
                held_ticks = 0;
                log.LogDebug($"pid {pid} resumed after {holds} clock blocks "
                             + $"({behind_ms:0} ms behind, floor {floor_ms:0})");
            }
        }
    }

    /// <summary>Close the one-second loss window: what should have been sent
    /// against what was.  A held pilot reads 100 %.</summary>
    public void close_window()
    {
        loss_x10000 = due > 0 ? Math.Min(10000, 10000 * skipped / due) : 0;
        due = 0;
        skipped = 0;
    }

    // 24 frames is under a second of snapshots+meta: a client that cannot take
    // that many has a socket that is not coming back
    private readonly Channel<(string kind, byte[] data)> q;

    public readonly int born_tick;   // the world clock when he connected

    public Client(WebSocket ws, int pid, int born_tick)
    {
        this.ws = ws;
        this.pid = pid;
        this.born_tick = born_tick;
        heard = System.Diagnostics.Stopwatch.GetTimestamp();
        // `FullMode.Wait` + `TryWrite` is Python's `put_nowait`: a full queue
        // refuses the frame (-> `dropped`) instead of blocking the caller
        q = Channel.CreateBounded<(string kind, byte[] data)>(new BoundedChannelOptions(24)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void offer(string kind, byte[] data)
    {
        if (!q.Writer.TryWrite((kind, data))) dropped += 1;
    }

    public async Task writer(CancellationToken ct)
    {
        try
        {
            while (await q.Reader.WaitToReadAsync(ct))
                while (q.Reader.TryRead(out var item))
                    await ws.SendAsync(item.data,
                        item.kind == "b" ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                        true, ct);
        }
        catch
        {
            // a dead socket: the reader loop notices and tears the connection down
        }
    }
}

/// <summary>The tick loop, hosted so ASP.NET Core owns its lifetime.</summary>
public sealed class GameLoop : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Server.game_loop(stoppingToken);
}

/// <summary>A console logger in Python's shape:
/// `2026-01-01 12:00:00,123 INFO mech.smoke: re-locked on hull 42` - so a
/// `tail -f` on the log file reads the same as it does for the Python server.</summary>
public sealed class LineLoggerProvider : ILoggerProvider
{
    private readonly LogLevel min;
    public LineLoggerProvider(LogLevel min) { this.min = min; }

    public ILogger CreateLogger(string category) => new LineLogger(category, min);
    public void Dispose() { }

    private sealed class LineLogger : ILogger
    {
        private readonly string category;
        private readonly LogLevel min;

        public LineLogger(string category, LogLevel min)
        {
            this.category = category;
            this.min = min;
        }

        // uvicorn's own lines have no equivalent here, and Kestrel's are noise
        public bool IsEnabled(LogLevel level) =>
            level >= min && level != LogLevel.None
            && !category.StartsWith("Microsoft", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                               Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var msg = fmt(state, ex);
            if (ex is not null) msg += Environment.NewLine + ex;
            Console.Out.WriteLine(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {name(level)} {category}: {msg}");
        }

        private static string name(LogLevel l) => l switch
        {
            LogLevel.Trace or LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            _ => "ERROR",
        };
    }
}

public static class Server
{
    public static string MODE = "battle";
    public static World world = null!;
    public static readonly Dictionary<int, Client> CONNECTIONS = new();
    public static Dictionary<string, object?> STATIC = null!;
    public static string ASSETS = "";

    public const float TICK_DT = 1f / 30f;
    public const int SNAP_EVERY = 2;        // 15 Hz snapshots
    public const int META_EVERY = 3;        // 10 Hz meta
    public const int IDLE_SLEEP_MS = 100;   // how often an empty server looks for a connection
    // the tick rate the loop actually achieves, averaged over the last second:
    // what the HUD's `TPS` reads.  `TPS_X100` is the wire's form (3000 = 30.00)
    public static float TPS = 30f;
    public static int TPS_X100 = 3000;

    public static readonly bool DEBUG = Environment.GetEnvironmentVariable("MECH_DEBUG") == "1";
    // An empty server is a paused server: with nobody on the wire there is
    // nothing to simulate and no one to simulate it for.  `MECH_DEBUG=1` keeps
    // the loop running with no connections, because that is the run a script or
    // a console session inspects from the outside.
    public static readonly bool PAUSE_WHEN_EMPTY = !DEBUG;

    private static ILogger? _log;
    private static ILogger log => _log ??= MechLog.get("mech.main");

    // the tick, the world and the connection table: see the header comment
    private static readonly object _wlock = new();

    // ------------------------------------------------------------------
    // startup
    // ------------------------------------------------------------------
    /// <summary>Build the world and the payload everybody gets once.
    ///
    /// Python does this at import time, before uvicorn opens the socket, which
    /// is why `Program.cs` calls it before `app.Run()` - a joining browser must
    /// never see a half-built world.</summary>
    public static void init()
    {
        var env_mode = Environment.GetEnvironmentVariable("MECH_MODE");
        MODE = !string.IsNullOrEmpty(env_mode) ? env_mode
             : Environment.GetEnvironmentVariable("MECH_RANGE") == "1" ? "range" : "battle";
        var t0 = Stopwatch.GetTimestamp();
        world = new World(mode: MODE);
        log.LogInformation($"world built: {MODE} map {world.terrain.w}x{world.terrain.d} tiles, " +
                           $"{world.towns.Count} towns, {world.lanes.Count} lanes");
        STATIC = Netcode.static_payload(world);
        log.LogInformation($"init payload {JsonSerializer.SerializeToUtf8Bytes(STATIC, Defs.WIRE_JSON).Length} B " +
                           $"in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms");
    }

    /// <summary>The client build: `<root>/assets`, filled by `npm run build`
    /// (`./dev.sh build`).  The server is started from the repo root or from
    /// `bin/...`, so the directory is found by walking up to it.</summary>
    public static string find_assets()
    {
        var env = Environment.GetEnvironmentVariable("MECH_ASSETS");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return Path.GetFullPath(env);
        // the build output lives under `bin/<cfg>/<tfm>/`, so the repo root is a
        // few parents up whichever way the project was built
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, "assets");
            if (Directory.Exists(cand)) return cand;
        }
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets"));
    }

    public static async Task run(string host, int port)
    {
        // The named loggers (`mech.prox`, `mech.smoke`, ...) are resolved through
        // one factory, and `MECH_LOG_LEVEL` (default INFO) is the one knob that
        // turns the per-round DEBUG lines on - `MECH_DEBUG` is unrelated, it
        // unlocks the development websocket messages.
        var factory = LoggerFactory.Create(b =>
        {
            b.ClearProviders();
            b.SetMinimumLevel(LogLevel.Trace);      // the provider does the gating
            b.AddProvider(new LineLoggerProvider(parse_level(
                Environment.GetEnvironmentVariable("MECH_LOG_LEVEL"))));
        });
        MechLog.factory = factory;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),           // our own flags are parsed by Program.cs
        });
        builder.Services.AddSingleton<ILoggerFactory>(factory);   // last registration wins
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.Services.AddHostedService<GameLoop>();

        ASSETS = find_assets();
        init();

        var app = builder.Build();
        configure(app);
        Console.Out.WriteLine(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} INFO mech.main: " +
            $"{MODE} mode listening on http://{host}:{port} (assets {ASSETS})");
        await app.RunAsync();
    }

    private static LogLevel parse_level(string? s) => (s ?? "INFO").ToUpperInvariant() switch
    {
        "DEBUG" => LogLevel.Debug,
        "WARNING" or "WARN" => LogLevel.Warning,
        "ERROR" or "CRITICAL" => LogLevel.Error,
        _ => LogLevel.Information,
    };

    private static void configure(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // Make the browser revalidate the bundle instead of heuristically
        // caching it: a rebuild changes the file but not its URL, and a stale
        // bundle with its own snapshot layout is a broken game.  `no-cache`
        // still allows a 304, so it costs one round trip per file.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path == "/" || path.StartsWith("/static", StringComparison.Ordinal))
                ctx.Response.Headers.CacheControl = "no-cache";
            await next();
        });

        if (Directory.Exists(ASSETS))
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(ASSETS),
                RequestPath = "/static",
            });

        app.MapGet("/", () =>
        {
            var f = Path.Combine(ASSETS, "index.html");
            IResult r = File.Exists(f)
                ? Results.File(f, "text/html")
                : Results.Redirect("/static/index.html");
            return r;
        });

        // NOTE: the websocket route stays below the static mount, as in main.py
        app.Map("/ws", (RequestDelegate)ws_endpoint);
    }

    // ------------------------------------------------------------------
    // the tick
    // ------------------------------------------------------------------
    /// <summary>Where a pilot's ear is, or null while he has no hull to sit in.</summary>
    private static (float x, float y, float z)? _ear(Player? pl)
    {
        if (pl is null || !world.ecs.is_alive(pl.mech)) return null;
        var p = world.ecs.fv("pos").get3(pl.mech);
        return (p.x, p.y, p.z);
    }

    public static void broadcast(int tick)
    {
        if (CONNECTIONS.Count == 0) return;
        bool snap_tick = tick % SNAP_EVERY == 0;
        // the minimap block is one list for everybody and does not need 15 Hz:
        // built once here, appended to each pilot's frame on the way out
        byte[]? contacts = tick % Netcode.CONTACT_EVERY == 0
            ? Netcode.contacts_bytes(world) : null;
        List<Dictionary<string, object?>>? fx = null;
        if (world.events.Count > 0)
        {
            fx = new List<Dictionary<string, object?>>(world.events);
            world.events.Clear();
        }
        // the loss window: one second of "should have / did"
        bool window = tick % Client.WINDOW == 0;
        foreach (var (pid, client) in CONNECTIONS.ToArray())
        {
            if (!world.players.TryGetValue(pid, out var pl)) continue;
            client.observe(tick);
            if (window) client.close_window();
            client.view.echo = client.ack;
            client.view.loss_x10000 = client.loss_x10000;
            // one snapshot per pilot on every second tick - the rows outside his
            // tier are not built at all (measured: 468 -> ~100 kB/s per pilot) -
            // and the 16 B clock block on every other tick
            if (snap_tick)
            {
                client.due += 1;
                if (!client.held)
                {
                    client.view.at(world, pl);
                    client.offer("b", Netcode.snapshot_bytes(world, client.view, contacts));
                }
                else
                {
                    client.holds += 1;
                    client.skipped += 1;
                    client.offer("b", Netcode.ping_bytes(tick, client.ack,
                                                        TPS_X100, client.loss_x10000));
                }
            }
            else
            {
                client.offer("b", Netcode.ping_bytes(tick, client.ack,
                                                    TPS_X100, client.loss_x10000));
            }
            // a held pilot gets no sound and no panel either: the whole point is
            // to leave the pipe empty until his heartbeat catches up
            if (client.held) continue;
            if (fx is not null)
            {
                // sound is per ear: a bang nobody could hear is not put on the wire
                var mine = Netcode.hearable(fx, _ear(pl));
                if (mine.Count > 0)
                {
                    // rounds ride as 24 B rows, words as a dictionary
                    byte[]? bin = Netcode.fx_bytes(mine, out var words);
                    if (bin is not null) client.offer("b", bin);
                    if (words.Count > 0)
                        client.offer("t", json(new Dictionary<string, object?>
                        {
                            ["t"] = "fx", ["events"] = words,
                        }));
                }
            }
            if (tick % META_EVERY == 0)
                client.offer("t", json(Netcode.meta_payload(world, pl)));
        }
    }

    private static double _tps_t;
    private static int _tps_n;

    public static async Task game_loop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(200, ct);
            var sw = Stopwatch.StartNew();
            double next_t = sw.Elapsed.TotalSeconds;
            while (!ct.IsCancellationRequested)
            {
                if (PAUSE_WHEN_EMPTY && CONNECTIONS.Count == 0)
                {
                    // nobody is connected: no tick, no snapshot, no wall clock to
                    // keep.  `next_t` is re-based on the way out, so the world
                    // resumes on a clean tick instead of catching up on the time it
                    // stood still.
                    await Task.Delay(IDLE_SLEEP_MS, ct);
                    next_t = sw.Elapsed.TotalSeconds;
                    continue;
                }
                try
                {
                    lock (_wlock)
                    {
                        int tick = world.tick + 1;
                        world.update(TICK_DT);
                        broadcast(tick);
                    }
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "tick");
                    lock (_wlock) world.events.Clear();
                }
                // the honest tick rate: ticks per wall-clock second, closed once
                // a second (a server that cannot keep up reports < 30)
                _tps_n += 1;
                double now = sw.Elapsed.TotalSeconds;
                if (now - _tps_t >= 1.0)
                {
                    TPS = (float)(_tps_n / (now - _tps_t));
                    TPS_X100 = Math.Clamp((int)MathF.Round(TPS * 100f), 0, 65535);
                    _tps_t = now;
                    _tps_n = 0;
                }
                next_t += TICK_DT;
                double delay = next_t - sw.Elapsed.TotalSeconds;
                if (delay < 0)
                {
                    next_t = sw.Elapsed.TotalSeconds;
                    delay = 0;
                }
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    // ------------------------------------------------------------------
    // websocket
    // ------------------------------------------------------------------
    public static async Task ws_endpoint(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        Player pl;
        Client client;
        lock (_wlock)
        {
            pl = world.add_player("PILOT");
            client = new Client(ws, pl.pid, world.tick);
            CONNECTIONS[pl.pid] = client;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var writer = client.writer(cts.Token);
        try
        {
            var init = new Dictionary<string, object?>(STATIC)
            {
                ["t"] = "init",
                ["pid"] = pl.pid,
                ["faction"] = pl.faction,
                ["loadout"] = pl.loadout,
                ["name"] = pl.name,
            };
            client.offer("t", json(init));

            var buf = new byte[1 << 15];
            using var ms = new MemoryStream(1 << 14);
            while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null,
                                                  CancellationToken.None);
                        return;
                    }
                    ms.Write(buf, 0, res.Count);
                }
                while (!res.EndOfMessage);
                if (res.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                using var doc = JsonDocument.Parse(text);
                lock (_wlock) handle(pl, client, doc.RootElement);
            }
        }
        catch (Exception)
        {
            // a dropped socket, a malformed frame: the connection is over either
            // way, and the pilot is cleaned up below like any other
        }
        finally
        {
            lock (_wlock) CONNECTIONS.Remove(pl.pid);
            cts.Cancel();
            try { await writer; } catch { /* the socket is gone */ }
            lock (_wlock) world.remove_player(pl.pid);
            ws.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // the console
    // ------------------------------------------------------------------
    private static void handle(Player pl, Client client, JsonElement msg)
    {
        // anything he says is a heartbeat: the frame clock reads this
        client.heard = System.Diagnostics.Stopwatch.GetTimestamp();
        switch (str(msg, "t", ""))
        {
            case "in":
            {
                pl.in_yaw = f32(msg, "yaw", 0f);
                pl.in_pitch = f32(msg, "pitch", 0f);
                pl.in_move = (f32(msg, "mx", 0f), f32(msg, "mz", 0f));
                pl.fire = (flag(msg, "fl", false), flag(msg, "fr", false));
                // optics + viewport aspect drive the IR lock circle
                pl.zoom = Math.Clamp(f32(msg, "z", pl.zoom), 1f, 20f);
                pl.aspect = Math.Clamp(f32(msg, "a", pl.aspect), 0.4f, 4f);
                // sight setting: a fixed range (CTRL+wheel), not a solved zero
                float zr = f32(msg, "zr", pl.zero_range);
                pl.zero_range = Math.Clamp(zr, Defs.ZERO_STEPS[0], Defs.ZERO_STEPS[^1]);
                // Q: one-shot hot smoke, latched until Systems/Smoke.cs eats it
                if (flag(msg, "q", false)) pl.smoke_q = true;
                // the newest tick this client has *seen* (a snapshot or a clock
                // block): the frame clock reads it, and every binary frame
                // echoes it back so the client can time a round trip.  A report
                // from before he connected (a fresh client says `st: 0` while the
                // match is at tick 4000) is not a distance, so it is ignored -
                // otherwise it would poison the path's floor for ten seconds.
                int st = inum(msg, "st", client.ack);
                if (st >= client.born_tick) client.ack = st;
                pl.name = trunc(str(msg, "name", pl.name), 16);
                break;
            }
            case "join":
            {
                // lobby: callsign and side.  The machine always comes out on the
                // default pair - a loadout is bought at the wrench, not picked in
                // a menu, and `spawn_mech` hands out the standard one anyway
                int fac = inum(msg, "faction", -1);
                if (fac == Defs.FACTION_RED || fac == Defs.FACTION_BLUE) pl.faction = fac;
                if (world.mode_mod.fixed_faction is int fixed_fac) pl.faction = fixed_fac;
                var name = str(msg, "name", pl.name).Trim();
                if (name.Length > 0) pl.name = trunc(name, 16);
                if (world.ecs.is_alive(pl.mech)) world.ecs.destroy(pl.mech);
                pl.mech = -1;
                pl.respawn = 0f;
                pl.death_pos = null;
                world.spawn_mech(pl);
                client.offer("t", json(new Dictionary<string, object?>
                {
                    ["t"] = "joined", ["pid"] = pl.pid, ["faction"] = pl.faction,
                    ["name"] = pl.name, ["loadout"] = pl.loadout,
                }));
                break;
            }
            case "shop":
            {
                // the rearm panel: price the whole draft, buy it or refuse it
                if (!at_base(pl))
                {
                    info(client, "SHOP DENIED - BE AT BASE / TOWN");
                    break;
                }
                var req = Shop.parse_request(to_dict(msg));
                var (ok, note, _) = Shop.deal(world, pl, req);
                info(client, ok ? note : $"SHOP DENIED - {note}");
                if (ok)
                    client.offer("t", json(new Dictionary<string, object?>
                    {
                        ["t"] = "shop", ["loadout"] = pl.loadout,
                    }));
                break;
            }
            case "debug_spawn" when DEBUG:
            {
                // development helper: drop a target in front of the player
                if (!world.ecs.is_alive(pl.mech)) break;
                int etype = inum(msg, "etype", Defs.ETYPE_APC);
                float dist = f32(msg, "dist", 800f);
                float ang = f32(msg, "angle", 0f) + pl.in_yaw;
                var pos = world.ecs.fv("pos").get3(pl.mech);
                float x = pos.x + MathF.Cos(ang) * dist;
                float z = pos.z + MathF.Sin(ang) * dist;
                int faction = inum(msg, "faction", 1 - pl.faction);
                // the range has no lanes at all: a spawned hull just stands where
                // it was dropped
                world.spawn_unit(etype, faction, x, z,
                                 weapons: int_arr(msg, "weapons"),
                                 lane: world.lanes.Count > 0 ? 0 : -1,
                                 extra: world.mode_mod.console_spawn_extra);
                info(client, $"DEBUG SPAWN {etype}");
                break;
            }
            case "debug_missile" when DEBUG:
            {
                // development helper: put a round in the air on a course for this
                // hull.  `dist` / `angle` place it (angle relative to the pilot's
                // own yaw), it flies at the weapon's cruise speed straight at the
                // sight eye, and the launcher is nobody (`src = -1`): a SACLOS
                // then coasts on its heading instead of riding a crew's beam,
                // which is exactly what a receiver test wants - one round, one
                // closing speed, no AI in the loop.
                //   wid   weapon id (`Defs.WID_*`, default SACLOS)
                //   dist  m from this hull (default 2000)
                //   angle rad, relative to the pilot's yaw (default 0, ahead)
                //   alt   m above this hull's own ground (default 2)
                //   speed m/s, overriding the weapon's own cruise speed
                //   miss  m of lateral offset, so the course passes this hull by
                //         that much instead of through it (the "passing by" case
                //         the receiver has to stay quiet on)
                // A top attack gets `target = me`, so its seeker has something to
                // lock; every other kind flies ballistic or on its heading.
                if (!world.ecs.is_alive(pl.mech)) break;
                int wid = inum(msg, "wid", Defs.WID_SACLOS);
                if (wid <= 0 || wid >= Defs.WEAPONS.Length) break;
                var wd = Defs.WEAPONS[wid];
                float mdist = MathF.Max(30f, f32(msg, "dist", 2000f));
                float mang = f32(msg, "angle", 0f) + pl.in_yaw;
                var mp = world.ecs.fv("pos").get3(pl.mech);
                float fx = mp.x + MathF.Cos(mang) * mdist;
                float fz = mp.z + MathF.Sin(mang) * mdist;
                float fy = MathF.Max(1.5f, mp.y + f32(msg, "alt", 2f));
                // the sight eye, not the feet: that is what every gate on the
                // other end solves from (`MathUtil.sight_eye`)
                float ex = mp.x, ey = mp.y + 5f, ez = mp.z;
                // the aim point, slid sideways by `miss`: the course then passes
                // this hull at that lateral distance, which is the case the
                // receiver's `warn` has to call "not for me"
                float miss = f32(msg, "miss", 0f);
                if (miss != 0f)
                {
                    float hx = ex - fx, hz = ez - fz;
                    float hl = MathF.Sqrt(hx * hx + hz * hz);
                    if (hl > 1e-3f)
                    {
                        ex += -hz / hl * miss;
                        ez += hx / hl * miss;
                    }
                }
                float dx = ex - fx, dy = ey - fy, dz = ez - fz;
                float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len < 1f) break;
                float spd = f32(msg, "speed", 0f);
                if (spd <= 0f) spd = wd.maxvel > 0f ? wd.maxvel : wd.vel;
                int kind = Systems.Weapons.kind_id(wd.kind);
                int tgt = kind == 3 ? pl.mech : -1;      // only the seeker wants one
                world.spawn_round(-1, 1 - pl.faction, wid, kind,
                                  new Vec3(fx, fy, fz),
                                  new Vec3(dx / len * spd, dy / len * spd, dz / len * spd),
                                  wd.life, tgt);
                info(client, $"DEBUG MISSILE {wd.key} {mdist:0} m out");
                break;
            }
            case "debug_trees" when DEBUG:
            {
                // development helper: grow a tree stand in front of the player.
                // A tree hides what stands behind it but stops no round, so the
                // sight rules are tested by putting one between the sights and a
                // target - and the client has to draw the same stand it is hiding
                // behind, hence the painted rectangle goes out with it.
                if (!world.ecs.is_alive(pl.mech)) break;
                float dist = f32(msg, "dist", 200f);
                float ang = f32(msg, "angle", 0f) + pl.in_yaw;
                var pos = world.ecs.fv("pos").get3(pl.mech);
                float x = pos.x + MathF.Cos(ang) * dist;
                float z = pos.z + MathF.Sin(ang) * dist;
                var (i0, j0, rows, cols, patch) = world.terrain.plant(
                    x, z, inum(msg, "radius", 2), flag(msg, "forest", true));
                // the client reads `data` as a JSON array of tile ids, so a byte
                // plane has to travel as numbers (a byte[] would be base64)
                var data = new int[patch.Length];
                for (int k = 0; k < patch.Length; k++) data[k] = patch[k];
                world.emit("tiles", ("i0", i0), ("j0", j0), ("rows", rows), ("cols", cols),
                           ("data", data));
                info(client, "DEBUG TREES");
                break;
            }
            case "debug_tp" when DEBUG:
            {
                // development helper: teleport the mech along its yaw
                if (!world.ecs.is_alive(pl.mech)) break;
                float dist = f32(msg, "dist", 300f);
                float ang = f32(msg, "angle", 0f) + pl.in_yaw;
                var pos = world.ecs.fv("pos").get3(pl.mech);
                float x = pos.x + MathF.Cos(ang) * dist;
                float z = pos.z + MathF.Sin(ang) * dist;
                var p = world.ecs.fv("pos");
                p.set(pl.mech, 0, x);
                p.set(pl.mech, 2, z);
                p.set(pl.mech, 1, world.terrain.height_at(x, z));
                // a teleport is not motion: a hull that arrived with its old
                // velocity would carry a phantom stride into the sweep
                world.ecs.fv("vel").set3(pl.mech, 0f, 0f, 0f);
                break;
            }
            case "debug_hurt" when DEBUG:
            {
                // development helper: wound the player or the enemy base
                if (world.ecs.is_alive(pl.mech))
                    Combat.apply(world, pl.mech, inum(msg, "module", 1),
                                 f32(msg, "amount", 100f), -1);
                if (truthy(msg, "base"))
                {
                    int base_fac = 1 - pl.faction;
                    if (world.base_entity.TryGetValue(base_fac, out int base_eid)
                        && world.ecs.is_alive(base_eid))
                        Combat.apply(world, base_eid, Defs.MOD_CHASSIS,
                                     f32(msg, "base", 5000f), -1);
                }
                break;
            }
            case "debug_god" when DEBUG:
            {
                // development helper: stop the AI from interrupting a test
                pl.god = flag(msg, "on", true);
                info(client, "GOD MODE " + (pl.god ? "ON" : "OFF"));
                break;
            }
            case "respawn":
                // always allowed: it skips the death timer
                if (pl.mech < 0) pl.respawn = MathF.Min(pl.respawn, 0.05f);
                break;
            case "debug_restart" when DEBUG:
            {
                // development helper: wipe the field and start a fresh round
                world.reset_match();
                info(client, "MATCH RESTARTED");
                break;
            }
        }
    }

    private static bool at_base(Player pl)
    {
        if (world.mode_mod.rearm_anywhere) return true;      // range mode: rearm anywhere
        if (!world.ecs.is_alive(pl.mech)) return false;
        var p = world.ecs.fv("pos").get3(pl.mech);
        var (bx, bz) = world.terrain.bases[pl.faction];
        if (MathF.Sqrt((p.x - bx) * (p.x - bx) + (p.z - bz) * (p.z - bz)) < 220f) return true;
        foreach (var t in world.towns)
            if (t.owner == pl.faction
                && MathF.Sqrt((p.x - t.x) * (p.x - t.x) + (p.z - t.z) * (p.z - t.z)) < 220f)
                return true;
        return false;
    }

    // ------------------------------------------------------------------
    // JSON doors
    // ------------------------------------------------------------------
    // `Defs.WIRE_JSON` has `IncludeFields`, without which every definition table
    // would serialise as an empty object and the client would draw nothing
    public static byte[] json(object? payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, Defs.WIRE_JSON);

    private static void info(Client client, string msg) =>
        client.offer("t", json(new Dictionary<string, object?> { ["t"] = "info", ["msg"] = msg }));

    private static string trunc(string s, int n) => s.Length <= n ? s : s[..n];

    private static bool has(JsonElement o, string key, out JsonElement v)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out v)) return true;
        v = default;
        return false;
    }

    private static string str(JsonElement o, string key, string dflt) =>
        has(o, key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : dflt;

    private static float f32(JsonElement o, string key, float dflt) =>
        has(o, key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (float)v.GetDouble() : dflt;

    private static int inum(JsonElement o, string key, int dflt) =>
        has(o, key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (v.TryGetInt64(out long l) ? (int)l : (int)v.GetDouble()) : dflt;

    /// <summary>`bool(msg.get(key, dflt))` - a number is true when it is not 0.</summary>
    private static bool flag(JsonElement o, string key, bool dflt)
    {
        if (!has(o, key, out var v)) return dflt;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.Null or JsonValueKind.Undefined => dflt,
            _ => true,
        };
    }

    /// <summary>Python truthiness of whatever the client sent under `key`.</summary>
    private static bool truthy(JsonElement o, string key)
    {
        if (!has(o, key, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => v.GetString()!.Length > 0,
            _ => true,
        };
    }

    private static int[]? int_arr(JsonElement o, string key)
    {
        if (!has(o, key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var a = new int[v.GetArrayLength()];
        int k = 0;
        foreach (var item in v.EnumerateArray())
            a[k++] = item.ValueKind == JsonValueKind.Number ? (int)item.GetDouble() : 0;
        return a;
    }

    /// <summary>The message as the plain `dict` Python's handlers index: a
    /// non-integer number becomes a `float`, an integer a `long`, so `Shop`'s
    /// `int(...)` casts and truthiness tests behave exactly as they do there.</summary>
    private static Dictionary<string, object?> to_dict(JsonElement e)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (e.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in e.EnumerateObject()) d[p.Name] = to_obj(p.Value);
        return d;
    }

    private static object? to_obj(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Object => to_dict(v),
        JsonValueKind.Array => to_list(v),
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out long l) ? l : (float)v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static List<object?> to_list(JsonElement v)
    {
        var l = new List<object?>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray()) l.Add(to_obj(item));
        return l;
    }
}
