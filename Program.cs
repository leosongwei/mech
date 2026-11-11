// Dev entry point: dotnet run -- [--host 0.0.0.0] [--port 8000] [--range]
//
// Ported from `backend/run.py`.  `--range` sets `MECH_RANGE` *before* the world
// is built (the world is created in `Server.init`, which reads the mode off the
// environment, exactly like `server.main` does at import time).
//
// Ports: `--port`, else `MECH_PORT`, else 8000 (`dev.sh` uses 8010 for the
// battle and 8011 for the range).

using Mech.Server;

string host = "0.0.0.0";
int port = 0;
bool range = false;
string? check = null;
int minutes = 3;
int players = 4;
float fromMin = 8f;      // --check net: the minute the meter starts

for (int k = 0; k < args.Length; k++)
{
    switch (args[k])
    {
        case "--host" when k + 1 < args.Length:
            host = args[++k];
            break;
        case "--port" when k + 1 < args.Length:
            port = int.Parse(args[++k]);
            break;
        case "--range":
            range = true;
            break;
        // headless harnesses (Tools/Check.cs): no socket, no client
        case "--check" when k + 1 < args.Length:
            check = args[++k];
            break;
        case "--minutes" when k + 1 < args.Length:
            minutes = int.Parse(args[++k]);
            break;
        case "--players" when k + 1 < args.Length:
            players = int.Parse(args[++k]);
            break;
        case "--from" when k + 1 < args.Length:
            fromMin = float.Parse(args[++k]);
            break;
        default:
            Console.Error.WriteLine($"mech: unknown argument '{args[k]}'" +
                                    " (usage: --host H --port N [--range] | --check battle|range|hit|intercept|diag|net|tiers|fcs [--minutes N])");
            return 1;
    }
}
if (check is not null)
{
    return check switch
    {
        "battle" => Mech.Server.Harness.Check.battle(minutes, players, perf: Environment.GetEnvironmentVariable("MECH_PERF") == "1"),
        "range" => Mech.Server.Harness.Check.range(minutes),
        "fcs" => Mech.Server.Harness.FcsCheck.run(),
        "diag" => Mech.Server.Harness.Check.diag(minutes),
        "hit" => Mech.Server.Harness.InterceptCheck.hits(),
        "intercept" => Mech.Server.Harness.InterceptCheck.run(Environment.GetEnvironmentVariable("MECH_IC_ONLY")),
        "net" => Mech.Server.Harness.NetCheck.bandwidth(minutes, fromMin),
        "tiers" => Mech.Server.Harness.Tiers.run(minutes, fromMin),
        _ => fail($"mech: unknown check '{check}' (battle | range)"),
    };
}
if (range) Environment.SetEnvironmentVariable("MECH_RANGE", "1");
if (port == 0)
{
    var env = Environment.GetEnvironmentVariable("MECH_PORT");
    port = int.TryParse(env, out int p) ? p : 8000;
}

await Server.run(host, port);
return 0;

static int fail(string msg)
{
    Console.Error.WriteLine(msg);
    return 2;
}
