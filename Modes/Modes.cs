// Mode registry: pick the rule set the server was started with.
//
// Ported from `backend/server/modes/__init__.py`.

namespace Mech.Server.Modes;

public static class Modes
{
    public const string DEFAULT = "battle";

    public static readonly Dictionary<string, Func<Mode>> MODES = new()
    {
        ["battle"] = () => new Battle.BattleMode(),
        ["range"] = () => new Range.RangeMode(),
    };

    public static Mode get(string name) =>
        MODES.TryGetValue(name, out var f) ? f() : MODES[DEFAULT]();
}
