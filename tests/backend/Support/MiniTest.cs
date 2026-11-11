// A test runner in seventy lines: `[Test]` static methods, a substring filter, and
// an exit code.  Everything a `dotnet test` would add on top of this is a package
// reference, and the repo has none.
//
// Deliberately plain, and deliberately not a framework: a test is a static method
// that throws on failure, the runner reports `ok` / `FAIL name: message`, and the
// process exits non-zero if anything threw.

using System.Reflection;

namespace Mech.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertFailed : Exception
{
    public AssertFailed(string message) : base(message) { }
}

public static class Assert
{
    public static void True(bool ok, string ctx)
    {
        if (!ok) throw new AssertFailed(ctx);
    }

    public static void Same(bool a, bool b, string ctx)
    {
        if (a != b) throw new AssertFailed($"{ctx}: {a} != {b}");
    }

    /// <summary>`|actual - tol| <= tol` - the one comparison every numeric test
    /// uses, with the tolerance always passed in by the caller so the number is
    /// visible at the call site.</summary>
    public static void Close(double actual, double expected, double tol, string ctx)
    {
        double d = Math.Abs(actual - expected);
        if (!(d <= tol))
            throw new AssertFailed($"{ctx}: {actual:R} vs {expected:R} (tol {tol:R}, off {d:R})");
    }

    public static void AtMost(double value, double limit, string ctx)
    {
        if (!(value <= limit))
            throw new AssertFailed($"{ctx}: {value:R} > {limit:R}");
    }
}

/// <summary>The one group of tolerance constants.
///
/// Each host keeps its own copy of these numbers (`tests/frontend/support/tol.mjs`
/// is the other) and they are the same numbers: they are a contract, so changing one
/// without the other is exactly what the cross-side comparison catches.  Measured
/// context for the figures: float32 against float64 moves the solver's answer by
/// 0.13 mm over a 4 km shot, while a 30 mm shell's own dispersion is 1.1 m at 1 km -
/// the tolerance is set by neither, it is set by how far two *independent
/// implementations of the same formula* can drift (a convergence threshold, an
/// iteration count).</summary>
public static class Tol
{
    /// <summary>m, two hosts' intercept points.</summary>
    public static double Point(double dist) => Math.Max(0.25, 1e-4 * dist);
    /// <summary>s, the time of flight.</summary>
    public const double Tof = 0.02;
    /// <summary>rad, the elevation and the two sight angles.</summary>
    public const double Angle = 1e-3;
    /// <summary>m, L1: the round must pass inside half the target's own radius.</summary>
    public static double Miss(double radius) => 0.5 * radius;
}

public static class MiniTest
{
    public static int Run(Assembly asm, string? filter)
    {
        int pass = 0, fail = 0;
        foreach (var type in asm.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                              .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
                              .OrderBy(m => m.Name, StringComparer.Ordinal);
            foreach (var m in methods)
            {
                string name = $"{type.Name}.{m.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    m.Invoke(null, null);
                    pass++;
                    Console.WriteLine($"ok   {name}");
                }
                catch (TargetInvocationException ex)
                {
                    fail++;
                    Console.WriteLine($"FAIL {name}: {ex.InnerException?.Message ?? "threw"}");
                }
            }
        }
        Console.WriteLine($"\n{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
