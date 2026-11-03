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
    // 24 frames is under a second of snapshots+meta: a client that cannot take
    // that many has a socket that is not coming back
    private readonly Channel<(string kind, byte[] data)> q;

    public Client(WebSocket ws, int pid)
    {
        this.ws = ws;
        this.pid = pid;
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
        byte[]? snap = null;
        if (tick % SNAP_EVERY == 0) snap = Netcode.snapshot_bytes(world);
        List<Dictionary<string, object?>>? fx = null;
        if (world.events.Count > 0)
        {
            fx = new List<Dictionary<string, object?>>(world.events);
            world.events.Clear();
        }
        byte[]? fx_all = null;
        if (fx is not null)
            fx_all = json(new Dictionary<string, object?> { ["t"] = "fx", ["events"] = fx });
        foreach (var (pid, client) in CONNECTIONS.ToArray())
        {
            if (!world.players.TryGetValue(pid, out var pl)) continue;
            if (snap is not null) client.offer("b", snap);
            if (fx is not null)
            {
                // sound is per ear: a bang nobody could hear is not put on the wire
                var mine = Netcode.hearable(fx, _ear(pl));
                if (mine.Count == fx.Count)
                    client.offer("t", fx_all!);       // nothing dropped: the shared message
                else if (mine.Count > 0)
                    client.offer("t", json(new Dictionary<string, object?>
                    {
                        ["t"] = "fx", ["events"] = mine,
                    }));
            }
            if (tick % META_EVERY == 0)
                client.offer("t", json(Netcode.meta_payload(world, pl)));
        }
    }

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
            client = new Client(ws, pl.pid);
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
